using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PvZDynamicMusic
{
    /// <summary>Loop point in frame (campioni per canale) del file. End = 0 significa "fine file".</summary>
    internal struct LoopPoints
    {
        public int Start;
        public int End;
    }

    /// <summary>Durate dei fade in secondi (tempo pieno 0 -> 1).</summary>
    internal struct FadeSettings
    {
        public float HordeIn;    // horde: 0 -> 1 quando arriva l'orda
        public float HordeOut;   // horde: 1 -> 0 quando l'orda finisce
        public float BaseIn;     // base:  0 -> 1 (solo crossfade, es. Moongrains) quando l'orda finisce
        public float BaseOut;    // base:  1 -> 0 (solo crossfade) quando arriva l'orda
        public float Horde30In;  // horde_30: sale quando ci sono piu' zombie della soglia
        public float Horde30Out; // horde_30: scende quando tornano sotto la soglia
    }

    /// <summary>Configurazione letta da config.ini nella cartella della musica.</summary>
    internal sealed class MusicConfig
    {
        public const int CurrentVersion = 3;
        public const string FileName = "config.ini";

        public string Folder;
        public bool SilenceUnmapped;   // musica NON elencata in [mappa]: false = resta l'originale, true = silenzio (le elencate restano originali se mancano i file)
        public bool Debug;

        public readonly Dictionary<string, string> TuneMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, LoopPoints> Loops = new Dictionary<string, LoopPoints>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> _fade = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _crossfade = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public string ConfigPath => Path.Combine(Folder, FileName);

        /// <summary>Prefissi candidati (in ordine di preferenza) per una musica del gioco; lista vuota = nessun file previsto.</summary>
        public List<string> Candidates(string tuneName)
        {
            var list = new List<string>();
            if (TuneMap.TryGetValue(tuneName, out var v)) AddSplit(list, v);
            return list;
        }

        private static void AddSplit(List<string> list, string value)
        {
            foreach (var p in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = p.Trim();
                if (t.Length > 0 && !list.Exists(x => x.Equals(t, StringComparison.OrdinalIgnoreCase))) list.Add(t);
            }
        }

        /// <summary>true se la musica compare in [mappa] (anche con valore vuoto = "non sostituirla").</summary>
        public bool IsMapped(string tuneName) => TuneMap.ContainsKey(tuneName);

        public LoopPoints GetLoops(string key) => Loops.TryGetValue(key, out var lp) ? lp : default(LoopPoints);

        /// <summary>Fade per una musica: valori globali, sovrascritti da quelli "prefisso.Chiave".</summary>
        public FadeSettings GetFades(string prefix)
        {
            var f = new FadeSettings
            {
                HordeIn = FadeValue(prefix, "HordeFadeIn", 8f),
                HordeOut = FadeValue(prefix, "HordeFadeOut", 12f),
                BaseIn = FadeValue(prefix, "BaseFadeIn", 12f),
                BaseOut = FadeValue(prefix, "BaseFadeOut", 8f)
            };
            // horde_30 ha di default le stesse durate della horde
            f.Horde30In = FadeValue(prefix, "Horde30FadeIn", f.HordeIn);
            f.Horde30Out = FadeValue(prefix, "Horde30FadeOut", f.HordeOut);
            return f;
        }

        /// <summary>Quanti zombie a schermo servono per far entrare _horde_30 (si attiva con PIU' zombie della soglia).</summary>
        public int GetHorde30Threshold(string prefix) => (int)Math.Round(Lookup(prefix, "Horde30Zombie", 30f));

        private float Lookup(string prefix, string key, float def)
        {
            float v;
            if (!_fade.TryGetValue(prefix + "." + key, out v) && !_fade.TryGetValue(key, out v)) v = def;
            return v;
        }

        private float FadeValue(string prefix, string key, float def)
        {
            float v = Lookup(prefix, key, def);
            return v < 0.05f ? 0.05f : v;
        }

        /// <summary>true/false se forzato da config (prefisso.Crossfade), null = decide il gioco.</summary>
        public bool? GetCrossfadeOverride(string prefix) => _crossfade.TryGetValue(prefix, out var b) ? b : (bool?)null;

        public static MusicConfig Load(string folder, Action<string> log, Action<string> warn)
        {
            var cfg = new MusicConfig { Folder = folder };
            Directory.CreateDirectory(folder);

            string path = cfg.ConfigPath;
            if (!File.Exists(path))
            {
                File.WriteAllText(path, DefaultText, new UTF8Encoding(false));
            }
            else
            {
                // Se il config e' di una versione precedente lo sostituiamo con quello nuovo (tenendo una copia).
                string old = File.ReadAllText(path);
                var m = Regex.Match(old, @"^\s*Versione\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                int ver = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
                if (ver < CurrentVersion)
                {
                    string bak = path + ".v" + ver + ".bak";
                    File.Copy(path, bak, true);
                    File.WriteAllText(path, DefaultText, new UTF8Encoding(false));
                    log?.Invoke($"config.ini aggiornato alla versione {CurrentVersion} (copia della vecchia in {Path.GetFileName(bak)})");
                }
            }

            string section = "";
            int lineNo = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                lineNo++;
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) { warn?.Invoke($"config.ini riga {lineNo}: ignorata ('{line}')"); continue; }
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                switch (section)
                {
                    case "generale":
                        if (key.Equals("MusicaNonMappata", StringComparison.OrdinalIgnoreCase))
                            cfg.SilenceUnmapped = val.Equals("silence", StringComparison.OrdinalIgnoreCase)
                                               || val.Equals("silenzio", StringComparison.OrdinalIgnoreCase);
                        else if (key.Equals("Debug", StringComparison.OrdinalIgnoreCase))
                            cfg.Debug = val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1";
                        break;

                    case "mappa":
                        cfg.TuneMap[key] = val;
                        break;

                    case "fade":
                    {
                        if (key.EndsWith(".Crossfade", StringComparison.OrdinalIgnoreCase))
                        {
                            string prefix = key.Substring(0, key.Length - ".Crossfade".Length);
                            if (val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1") cfg._crossfade[prefix] = true;
                            else if (val.Equals("false", StringComparison.OrdinalIgnoreCase) || val == "0") cfg._crossfade[prefix] = false;
                            break;
                        }
                        if (float.TryParse(val.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float sec)
                            && !float.IsNaN(sec) && !float.IsInfinity(sec))
                            cfg._fade[key] = sec;
                        else
                            warn?.Invoke($"config.ini riga {lineNo}: valore non numerico '{val}'");
                        break;
                    }

                    case "loop":
                    {
                        var parts = val.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2
                            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int a)
                            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b)
                            && a >= 0 && b >= 0)
                            cfg.Loops[key] = new LoopPoints { Start = a, End = b };
                        else
                            warn?.Invoke($"config.ini riga {lineNo}: loop non valido '{val}' (usa: inizio, fine)");
                        break;
                    }
                }
            }
            return cfg;
        }

        public const string DefaultText =
@"# =====================================================================
#  PvZ Dynamic Music - configurazione
# =====================================================================
#  Metti i file .wav in QUESTA cartella (accanto a config.ini).
#  Nomi dei file (senza spazi, tutto minuscolo):
#     cd.wav                      menu principale / Crazy Dave / Zen Garden
#     cys.wav                     Choose Your Seeds (scelta semi)
#     gw_base.wav  gw_horde.wav   Grasswalk      (giorno)
#     mg_base.wav  mg_horde.wav   Moongrains     (notte)   <- horde = traccia diversa, crossfade
#     wg_base.wav  wg_horde.wav   Watery Graves  (piscina)
#     rm_base.wav  rm_horde.wav   Rigor Mormist  (nebbia)
#     gtr_base.wav gtr_horde.wav  Graze the Roof (tetto)
#     lb.wav                      Loonboon (mini-giochi: Wall-nut Bowling, Whack a Zombie, ...)
#     cb.wav                      Cerebrawl (Vasebreaker, I Zombie)
#     ub.wav                     Ultimate Battle (livelli 1-10, 2-10, 3-10 e Column Like You See 'Em)
#     bm.wav                      Brainiac Maniac (boss finale 5-10)
#     zg.wav                      Zen Garden e Tree of Wisdom (se manca si usa cd.wav)
#     credits.wav                 (facoltativo) crediti
#
#  Zombotany (e ogni altro livello/mini-gioco) suona la musica dello sfondo su cui si trova,
#  cioe' gw, mg, wg, rm o gtr: come nel gioco, non serve un file apposta.
#
#  _alt (facoltativo, per qualsiasi musica con _base, es. rm_alt.wav e wg_alt.wav):
#     stessa durata e stessi loop point di _base. Resta in riproduzione sotto _base
#     (sincronizzato) ma si sente a giri alterni: finisce _base -> parte _alt,
#     finisce _alt -> riparte _base, e cosi' via. _horde si sovrappone a entrambi.
#
#  _base_var (facoltativo, per gw): strato in piu' SOPRA la base (non al suo posto), stessa durata e loop.
#     Suona solo nel PRIMO giro del loop (mai nel livello 1-1) e poi tace; quando arriva
#     l'orda rientra insieme a _horde e con le sue stesse dissolvenze.
#  _horde_alt (facoltativo, per rm): la horde da usare mentre suona _alt (con _base suona _horde).
#     Non vale per le musiche in crossfade (es. mg).
#  _horde_30 (facoltativo, per gw): si aggiunge sopra _horde solo con l'orda in corso e quando ci sono PIU' di 30 zombie
#     a schermo (soglia e dissolvenze in [fade]: Horde30Zombie, Horde30FadeIn, Horde30FadeOut).
#
#  I file possono stare in sottocartelle in qualsiasi modo (es. DynMus\gw\gw_base.wav): vengono
#  cercati per nome in tutta la cartella. Se lo stesso nome compare due volte si usa quello piu'
#  vicino alla cartella principale.
#
#  _vic (facoltativo, una per musica di livello: gw_vic.wav, mg_vic.wav, wg_vic.wav, rm_vic.wav,
#  gtr_vic.wav, e volendo lb_vic, cb_vic, ub_vic, bm_vic):
#     musica di VITTORIA. Suona una sola volta, senza loop, quando completi il livello raccogliendo
#     il premio lasciato dall'ultimo zombie, al posto del jingle del gioco. Usa il volume musica
#     delle opzioni. Si usa solo se anche quella musica e' sostituita (esiste il file base); se
#     il file non c'e', resta il jingle originale.
#
#  Se un file manca, per quella musica resta quella ORIGINALE del gioco.
#  Dopo aver modificato questo file riavvia il gioco. I commenti vanno su righe a parte
#  (che iniziano con # o ;), mai dopo un valore.

[generale]
Versione = 3
# original = le musiche NON elencate in [mappa] restano quelle originali del gioco
# silence  = le musiche NON elencate in [mappa] non si sentono. Le musiche elencate in [mappa]
#            (anche con valore vuoto) restano sempre originali se mancano i loro file.
MusicaNonMappata = original
# true = scrive nella console di MelonLoader lo stato della musica dinamica e attiva i tasti di prova
# (Ctrl+H/S/D/V solo dentro un livello, Ctrl+L con una tua traccia in riproduzione):
#   Ctrl+H = fa arrivare un'orda (messaggio + 25 zombie)      Ctrl+S = spawna 10 zombie
#   Ctrl+D = rimuove tutti gli zombie                          Ctrl+V = fa cadere la pala del premio:
#                                                              raccoglila per finire il livello e provare _vic
#   Ctrl+L = porta la musica a 4 secondi dalla fine del loop (per sentire il punto di loop)
Debug = false

[mappa]
# NomeMusicaDelGioco = prefisso del file. Puoi mettere piu' prefissi separati da virgola:
# viene usato il primo di cui esiste il file. Lascia vuoto per non sostituire quella musica.
TitleCrazyDaveMainTheme = cd
ChooseYourSeeds = cys
DayGrasswalk = gw
NightMoongrains = mg
PoolWaterygraves = wg
FogRigormormist = rm
RoofGrazetheroof = gtr
PuzzleCerebrawl = cb
MinigameLoonboon = lb
Conveyer = ub
FinalBossBrainiacManiac = bm
ZenGarden = zg, cd
CreditsZombiesOnYourLawn = credits
DayGrasswalkCredits = credits

[fade]
# Durate in SECONDI di ogni dissolvenza (tempo per andare da 0 a 1 o da 1 a 0).
# Il quando parte/finisce l'orda lo decide il gioco (>= 10 zombie in campo accende, < 4 spegne);
# qui decidi solo quanto durano le dissolvenze.
#   HordeFadeIn   = _horde sale quando arriva l'orda
#   HordeFadeOut  = _horde scende quando l'orda finisce
#   BaseFadeOut   = _base scende quando arriva l'orda      (solo crossfade: Moongrains)
#   BaseFadeIn    = _base risale quando l'orda finisce     (solo crossfade: Moongrains)
HordeFadeIn = 8
HordeFadeOut = 12
BaseFadeOut = 8
BaseFadeIn = 12
# _horde_30: soglia di zombie a schermo (si attiva con PIU' zombie di questo numero) e dissolvenze
# (se non le scrivi hanno le stesse durate della horde).
Horde30Zombie = 30
# Horde30FadeIn = 8
# Horde30FadeOut = 12
# Valori diversi per una sola musica: prefisso.Chiave = secondi, ad esempio
# mg.HordeFadeIn = 6
# mg.BaseFadeOut = 10
# Modo crossfade (la horde sostituisce la base e riparte da capo ogni volta che arriva l'orda):
# di solito lo decide il gioco (e' cosi' solo per mg). Per forzarlo o toglierlo:
# gw.Crossfade = true
# mg.Crossfade = false

[loop]
# prefisso = inizio, fine        (in campioni per canale / frame del file WAV)
# Fine = 0 vuol dire ""fino alla fine del file"". Con inizio > 0 la parte prima
# dell'inizio (intro) viene suonata una volta sola, poi si ripete da inizio a fine.
# _base, _alt e _horde (dove e' agganciata alla base) usano gli STESSI loop point.
# Nel modo crossfade (mg) la horde e' una traccia a se': ha i suoi loop point con la chiave
# mg_horde (se non c'e', si ripete da 0 a fine file).
cd = 0, 0
cys = 0, 0
gw = 0, 0
mg = 0, 0
# mg_horde = 0, 0
wg = 0, 0
rm = 0, 0
gtr = 0, 0
lb = 0, 0
cb = 0, 0
ub = 0, 0
bm = 0, 0
zg = 0, 0
credits = 0, 0
";
    }
}
