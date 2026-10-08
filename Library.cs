using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace PvZDynamicMusic
{
    /// <summary>Percorsi dei file di una musica (risolti sul thread principale).</summary>
    internal sealed class TrackPaths
    {
        public string Base;       // xx_base.wav oppure xx.wav
        public string Alt;        // xx_alt.wav
        public string BaseVar;    // xx_base_var.wav
        public string Horde;      // xx_horde.wav
        public string HordeAlt;   // xx_horde_alt.wav
        public string Horde30;    // xx_horde_30.wav
    }

    /// <summary>Le tracce di una musica: base (obbligatoria) + variazioni facoltative.</summary>
    internal sealed class TrackGroup
    {
        public string Prefix;
        public PcmData Base;
        public PcmData Alt;        // xx_alt: stessa durata/loop della base, si sente a giri alterni
        public PcmData BaseVar;    // xx_base_var: variazione della base, solo al primo giro (e con l'orda)
        public PcmData Horde;      // xx_horde
        public PcmData HordeAlt;   // xx_horde_alt: la horde da usare mentre suona _alt
        public PcmData Horde30;    // xx_horde_30: in piu' quando ci sono piu' di 30 zombie
        public int LoopStart;      // in frame della traccia base
        public int LoopEnd;
        public int HordeLoopStart; // usati solo se la horde e' indipendente (crossfade)
        public int HordeLoopEnd;
    }

    /// <summary>
    /// Trova i file su disco (anche nelle sottocartelle, in qualunque modo siano organizzate),
    /// li decodifica in background e tiene in cache le ultime musiche usate.
    /// </summary>
    internal sealed class TrackLibrary
    {
        private const int CacheSize = 3;

        private readonly MusicConfig _cfg;
        private readonly Action<string> _log;
        private readonly Action<string> _warn;
        private readonly Dictionary<string, Task<TrackGroup>> _cache = new Dictionary<string, Task<TrackGroup>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new List<string>();

        // indice nome-file (senza estensione) -> percorso, su tutta la cartella e le sottocartelle
        private Dictionary<string, string> _index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _indexTime = DateTime.MinValue;
        private readonly HashSet<string> _dupWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public TrackLibrary(MusicConfig cfg, Action<string> log, Action<string> warn)
        {
            _cfg = cfg;
            _log = log;
            _warn = warn;
        }

        // ------------------------------------------------------------------ ricerca dei file

        private void EnsureIndex()
        {
            if ((DateTime.UtcNow - _indexTime).TotalSeconds < 1.5) return;
            _indexTime = DateTime.UtcNow;

            var best = new Dictionary<string, (string path, int rank)>(StringComparer.OrdinalIgnoreCase);
            string root = Path.GetFullPath(_cfg.Folder);
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.wav", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    string dir = Path.GetDirectoryName(f) ?? root;
                    string rel = Path.GetRelativePath(root, dir);
                    int depth = rel == "." ? 0 : rel.Split(Path.DirectorySeparatorChar).Length;
                    string parent = new DirectoryInfo(dir).Name;
                    // preferisci: piu' vicino alla radice, e nella cartella che porta il nome della musica (es. gw\gw_base.wav)
                    int rank = depth * 2 + (name.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ? 0 : 1);

                    if (best.TryGetValue(name, out var cur))
                    {
                        if (_dupWarned.Add(name))
                            _warn($"'{name}.wav' compare piu' volte ({Path.GetRelativePath(root, cur.path)} e {Path.GetRelativePath(root, f)}): uso quello piu' vicino alla cartella principale");
                        if (rank >= cur.rank) continue;
                    }
                    best[name] = (f, rank);
                }
            }
            catch (Exception e)
            {
                _warn("lettura della cartella musica fallita: " + e.Message);
            }

            var idx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in best) idx[kv.Key] = kv.Value.path;
            _index = idx;
        }

        /// <summary>Elenco (percorsi relativi) dei .wav trovati, per il log.</summary>
        public List<string> ListFiles()
        {
            EnsureIndex();
            string root = Path.GetFullPath(_cfg.Folder);
            var list = new List<string>();
            foreach (var p in _index.Values) list.Add(Path.GetRelativePath(root, p));
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public string FindFile(string prefix, string suffix)
        {
            EnsureIndex();
            if (_index.TryGetValue(prefix + suffix, out var path) && File.Exists(path)) return path;
            return null;
        }

        /// <summary>File base: xx_base.wav oppure, per le musiche senza orda, xx.wav.</summary>
        public string FindBase(string prefix) => FindFile(prefix, "_base") ?? FindFile(prefix, "");

        public bool HasFiles(string prefix) => !string.IsNullOrWhiteSpace(prefix) && FindBase(prefix) != null;

        /// <summary>Primo prefisso della lista di cui esiste il file base, oppure null.</summary>
        public string FirstAvailable(IEnumerable<string> candidates)
        {
            foreach (var c in candidates) if (HasFiles(c)) return c;
            return null;
        }

        private TrackPaths ResolvePaths(string prefix)
        {
            return new TrackPaths
            {
                Base = FindBase(prefix),
                Alt = FindFile(prefix, "_alt"),
                BaseVar = FindFile(prefix, "_base_var"),
                Horde = FindFile(prefix, "_horde"),
                HordeAlt = FindFile(prefix, "_horde_alt"),
                Horde30 = FindFile(prefix, "_horde_30")
            };
        }

        // ------------------------------------------------------------------ caricamento

        /// <summary>Avvia (o riusa) il caricamento in background di una musica. Solo dal thread principale.</summary>
        public Task<TrackGroup> GetAsync(string prefix)
        {
            if (_cache.TryGetValue(prefix, out var existing))
            {
                if (!existing.IsFaulted)
                {
                    _order.Remove(prefix);
                    _order.Add(prefix);
                    return existing;
                }
                _cache.Remove(prefix);
                _order.Remove(prefix);
            }

            var paths = ResolvePaths(prefix);
            var task = Task.Run(() => Load(prefix, paths));
            _cache[prefix] = task;
            _order.Add(prefix);

            while (_order.Count > CacheSize)
            {
                string oldest = _order[0];
                _order.RemoveAt(0);
                _cache.Remove(oldest);
            }
            return task;
        }

        /// <summary>Pre-carica in background (menu e scelta semi servono subito).</summary>
        public void Preload(string prefix)
        {
            if (HasFiles(prefix)) GetAsync(prefix);
        }

        // ---- vittoria: xx_vic.wav (suonata una volta sola quando prendi il premio del livello)

        private readonly Dictionary<string, Task<TrackGroup>> _vicCache = new Dictionary<string, Task<TrackGroup>>(StringComparer.OrdinalIgnoreCase);

        public bool HasVic(string prefix) => !string.IsNullOrWhiteSpace(prefix) && FindFile(prefix, "_vic") != null;

        public Task<TrackGroup> GetVicAsync(string prefix)
        {
            if (_vicCache.TryGetValue(prefix, out var t) && !t.IsFaulted) return t;
            if (_vicCache.Count >= 3) _vicCache.Clear();
            string path = FindFile(prefix, "_vic");
            t = Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pcm = WavLoader.Load(path);
                _log($"[{prefix}] vittoria caricata in {sw.ElapsedMilliseconds} ms: {pcm.Name} ({pcm.SampleRate} Hz, {pcm.Channels} ch, {pcm.Seconds:F2}s)");
                return new TrackGroup { Prefix = prefix, Base = pcm, LoopStart = 0, LoopEnd = pcm.Frames };
            });
            _vicCache[prefix] = t;
            return t;
        }

        public void PreloadVic(string prefix)
        {
            if (HasVic(prefix)) GetVicAsync(prefix);
        }

        // ------------------------------------------------------------------ decodifica (thread in background)

        private TrackGroup Load(string prefix, TrackPaths paths)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var g = new TrackGroup { Prefix = prefix };

            if (paths.Base == null) throw new FileNotFoundException("manca " + prefix + "_base.wav / " + prefix + ".wav");
            g.Base = WavLoader.Load(paths.Base);
            g.Alt = TryLoad(paths.Alt);
            g.BaseVar = TryLoad(paths.BaseVar);
            g.Horde = TryLoad(paths.Horde);
            g.HordeAlt = TryLoad(paths.HordeAlt);
            g.Horde30 = TryLoad(paths.Horde30);

            int frames = g.Base.Frames;
            g.LoopStart = 0;
            g.LoopEnd = frames;
            var lp = _cfg.GetLoops(prefix);
            int start = lp.Start;
            int end = lp.End <= 0 ? frames : lp.End;
            if (end > frames)
            {
                _warn($"[{prefix}] loop fine ({end}) oltre la fine del file ({frames}): uso la fine del file");
                end = frames;
            }
            if (start < 0 || start >= end)
            {
                _warn($"[{prefix}] loop inizio ({start}) non valido: uso 0");
                start = 0;
            }
            g.LoopStart = start;
            g.LoopEnd = end;

            // tutte queste tracce si agganciano alla base: devono avere la stessa durata e gli stessi canali
            CheckSameAsBase(prefix, g.Base, g.Alt);
            CheckSameAsBase(prefix, g.Base, g.BaseVar);
            CheckSameAsBase(prefix, g.Base, g.HordeAlt);
            CheckSameAsBase(prefix, g.Base, g.Horde30);

            if (g.Horde != null)
            {
                int hf = g.Horde.Frames;
                var hlp = _cfg.GetLoops(prefix + "_horde");
                int hs = hlp.Start;
                int he = hlp.End <= 0 ? hf : hlp.End;
                if (he > hf) he = hf;
                if (hs < 0 || hs >= he) hs = 0;
                g.HordeLoopStart = hs;
                g.HordeLoopEnd = he;
            }

            _log($"[{prefix}] caricato in {sw.ElapsedMilliseconds} ms: base {Path.GetFileName(paths.Base)} ({g.Base.SampleRate} Hz, {g.Base.Channels} ch, {g.Base.Seconds:F2}s)"
                 + (g.Alt != null ? " + alt" : "")
                 + (g.BaseVar != null ? " + base_var" : "")
                 + (g.Horde != null ? $" + horde ({g.Horde.Seconds:F2}s)" : "")
                 + (g.HordeAlt != null ? " + horde_alt" : "")
                 + (g.Horde30 != null ? " + horde_30" : "")
                 + $", loop {start}..{end}");
            return g;
        }

        private void CheckSameAsBase(string prefix, PcmData b, PcmData other)
        {
            if (other == null) return;
            if (Math.Abs(other.Seconds - b.Seconds) > 0.05 || other.Channels != b.Channels)
                _warn($"[{prefix}] {other.Name} deve avere la stessa durata e gli stessi canali di {b.Name} "
                      + $"({other.Seconds:F3}s/{other.Channels}ch contro {b.Seconds:F3}s/{b.Channels}ch): non resteranno allineate");
        }

        private PcmData TryLoad(string path)
        {
            if (path == null) return null;
            try { return WavLoader.Load(path); }
            catch (Exception e)
            {
                _warn($"file ignorato {Path.GetFileName(path)}: {e.Message}");
                return null;
            }
        }
    }
}
