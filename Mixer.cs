using System;
using System.Threading;

namespace PvZDynamicMusic
{
    internal enum LayerRole { Base, Alt, BaseVar, Horde, HordeAlt, Horde30 }

    /// <summary>
    /// Una traccia in riproduzione con il suo playhead. Volume e Hold sono scritti dal thread principale e letti dal thread audio;
    /// Pos/Wraps/Current appartengono al thread audio.
    /// </summary>
    internal sealed class MixLayer
    {
        public readonly PcmData Pcm;
        public readonly LayerRole Role;
        public readonly bool Independent;   // playhead a se': resta a 0 mentre e' spenta (Hold) e riparte da capo quando rientra
        public bool OneShot;                // suona una volta sola (vittoria): a fine file resta in silenzio, senza loop
        public readonly int LoopStart;      // in frame di QUESTA traccia
        public readonly int LoopEnd;
        public readonly int LoopLen;

        public int Gate;                    // 0 = sempre udibile, 1 = solo nei giri pari (base con alt), 2 = solo nei giri dispari (alt),
                                            // 3 = anche da solo (FirstVol) ma solo nel primo giro del loop (base_var)
        public volatile float Target;       // volume voluto
        public volatile float FirstVol;     // solo Gate 3: volume nel primo giro del loop (indipendente da Target)
        public volatile bool Hold;          // solo Independent: tieni ferma a 0
        public float Current;               // volume applicato all'ultimo blocco
        public double Pos;                  // playhead in frame di questa traccia
        public long Wraps;                  // quanti giri di loop completati

        public MixLayer(PcmData pcm, int loopStart, int loopEnd, LayerRole role, bool independent)
        {
            Pcm = pcm;
            Role = role;
            Independent = independent;
            int frames = pcm.Frames;
            if (loopEnd <= 0 || loopEnd > frames) loopEnd = frames;
            if (loopStart < 0 || loopStart >= loopEnd) loopStart = 0;
            LoopStart = loopStart;
            LoopEnd = loopEnd;
            LoopLen = loopEnd - loopStart;
        }

        public void Reset()
        {
            Pos = 0;
            Wraps = 0;
        }

        /// <summary>Avanza il playhead senza produrre audio (traccia muta ma da tenere sincronizzata).</summary>
        public void Advance(double frames)
        {
            Pos += frames;
            if (OneShot) return;
            if (Pos >= LoopEnd)
            {
                long k = (long)Math.Floor((Pos - LoopStart) / LoopLen);
                Pos -= (double)k * LoopLen;
                Wraps += k;
                while (Pos >= LoopEnd) { Pos -= LoopLen; Wraps++; }
            }
        }
    }

    /// <summary>Le tracce della musica corrente + gli inviluppi di volume (thread principale).</summary>
    internal sealed class MixSession
    {
        public readonly string Prefix;
        public readonly bool Crossfade;     // la horde sostituisce la base (Moongrains) invece di sovrapporsi
        public readonly MixLayer Base;
        public readonly MixLayer Alt;
        public readonly MixLayer BaseVar;   // variazione della base: primo giro (fuori dal 1-1) + con l'orda
        public readonly MixLayer Horde;
        public readonly MixLayer HordeAlt;  // horde da usare mentre suona _alt
        public readonly MixLayer Horde30;   // si aggiunge con piu' di N zombie
        public readonly MixLayer[] All;

        public float HordeEnv;              // 0..1, thread principale
        public float Horde30Env;
        public float BaseEnv = 1f;
        public bool VarDecided;             // _base_var: deciso se e' permessa (non nel livello 1-1)?
        public bool VarAllowed;
        public volatile float SeekBeforeLoopEnd = -1f;   // debug (Ctrl+L): >= 0 = porta tutte le tracce a N secondi prima della fine del loop

        public MixSession(TrackGroup g, bool crossfade, bool oneShot = false)
        {
            Prefix = g.Prefix;

            if (oneShot)
            {
                // vittoria: solo la traccia base, una volta sola
                Crossfade = false;
                Base = new MixLayer(g.Base, 0, g.Base.Frames, LayerRole.Base, false) { OneShot = true };
                All = new[] { Base, null, null, null, null, null };
                return;
            }

            Crossfade = crossfade && g.Horde != null;

            Base = new MixLayer(g.Base, g.LoopStart, g.LoopEnd, LayerRole.Base, false);

            if (g.Alt != null)
            {
                Alt = Dependent(g.Alt, g.Base, g.LoopStart, g.LoopEnd, LayerRole.Alt);
                Base.Gate = 1;
                Alt.Gate = 2;
            }

            if (g.BaseVar != null)
            {
                BaseVar = Dependent(g.BaseVar, g.Base, g.LoopStart, g.LoopEnd, LayerRole.BaseVar);
                BaseVar.Gate = 3;
            }

            if (g.Horde != null)
            {
                Horde = Crossfade
                    ? new MixLayer(g.Horde, g.HordeLoopStart, g.HordeLoopEnd, LayerRole.Horde, true) { Hold = true }
                    : Dependent(g.Horde, g.Base, g.LoopStart, g.LoopEnd, LayerRole.Horde);

                // horde_alt: con _base suona _horde, con _alt suona _horde_alt (stessa alternanza a giri)
                if (g.HordeAlt != null && Alt != null && !Crossfade)
                {
                    HordeAlt = Dependent(g.HordeAlt, g.Base, g.LoopStart, g.LoopEnd, LayerRole.HordeAlt);
                    Horde.Gate = 1;
                    HordeAlt.Gate = 2;
                }
            }

            if (g.Horde30 != null)
                Horde30 = Dependent(g.Horde30, g.Base, g.LoopStart, g.LoopEnd, LayerRole.Horde30);

            All = new[] { Base, Alt, BaseVar, Horde, HordeAlt, Horde30 };
        }

        /// <summary>Layer agganciato alla base: stessi loop point (convertiti se la frequenza e' diversa).</summary>
        private static MixLayer Dependent(PcmData p, PcmData b, int baseStart, int baseEnd, LayerRole role)
        {
            double r = (double)p.SampleRate / b.SampleRate;
            int s = Math.Abs(r - 1.0) < 1e-9 ? baseStart : (int)Math.Round(baseStart * r);
            int e = Math.Abs(r - 1.0) < 1e-9 ? baseEnd : (int)Math.Round(baseEnd * r);
            return new MixLayer(p, s, e, role, false);
        }
    }

    /// <summary>
    /// Mixer sample-accurate. Non conosce Il2Cpp: Render() riempie un buffer float interleaved.
    /// Viene chiamato dal thread audio di Unity (OnAudioFilterRead).
    /// </summary>
    internal sealed class MixerCore
    {
        private MixSession _session;
        private MixSession _extra;         // vittoria (una volta sola): si sovrappone alla musica e non segue la pausa
        private volatile bool _paused;
        private float _gain = 1f;          // rampa pausa/ripresa (thread audio)
        private float[] _buf = new float[16384];

        public int OutRate = 48000;
        public long Callbacks;             // per la diagnostica
        public float LastPeak;

        public MixSession Session => Volatile.Read(ref _session);
        public bool Active => Volatile.Read(ref _session) != null;
        public bool Paused { get => _paused; set => _paused = value; }

        public void Start(MixSession s)
        {
            _gain = 1f;
            Volatile.Write(ref _session, s);
        }

        public void Stop() => Volatile.Write(ref _session, null);

        public MixSession Extra => Volatile.Read(ref _extra);
        public void StartExtra(MixSession s) => Volatile.Write(ref _extra, s);
        public void StopExtra() => Volatile.Write(ref _extra, null);

        /// <summary>Punto d'ingresso dal thread audio: scrive il mix nel buffer di Unity.</summary>
        public void Process(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float> data, int channels)
        {
            Interlocked.Increment(ref Callbacks);
            if ((Volatile.Read(ref _session) == null && Volatile.Read(ref _extra) == null) || channels <= 0) return;

            int n = data.Length;
            if (n <= 0) return;
            if (_buf.Length < n) _buf = new float[n];
            else Array.Clear(_buf, 0, n);

            Render(_buf, n / channels, channels);

            float peak = 0f;
            for (int i = 0; i < n; i++)
            {
                float v = _buf[i];
                if (float.IsNaN(v)) v = 0f;   // NaN (file float corrotto o valore di config assurdo): silenzio, mai rumore
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                float a = v < 0 ? -v : v;
                if (a > peak) peak = a;
                data[i] = v;
            }
            LastPeak = peak;
        }

        /// <summary>Somma nel buffer (gia' azzerato) tutti i layer per <paramref name="frames"/> frame.</summary>
        public void Render(float[] mix, int frames, int outCh)
        {
            var s = Volatile.Read(ref _session);
            if (s != null)
            {
                float g0 = _gain;
                float g1 = _paused ? 0f : 1f;
                _gain = g1;
                if (g0 > 0f || g1 > 0f)            // in pausa (gain 0): silenzio e playhead fermi
                {
                    float seek = s.SeekBeforeLoopEnd;
                    if (seek >= 0f)
                    {
                        s.SeekBeforeLoopEnd = -1f;
                        // tutte le tracce nello stesso blocco: restano sincronizzate
                        foreach (var L in s.All)
                        {
                            if (L == null || (L.Independent && L.Hold)) continue;
                            double target = Math.Round(L.LoopEnd - (double)seek * L.Pcm.SampleRate);   // frame intero: salto sample-exact
                            if (target < L.LoopStart) target = L.LoopStart;
                            L.Pos = target;
                        }
                    }

                    foreach (var L in s.All)
                    {
                        if (L != null) RenderLayer(L, mix, frames, outCh, g0, g1);
                    }
                }
            }

            var x = Volatile.Read(ref _extra);
            if (x != null)
            {
                foreach (var L in x.All)
                {
                    if (L != null) RenderLayer(L, mix, frames, outCh, 1f, 1f);
                }
            }
        }

        private void RenderLayer(MixLayer L, float[] mix, int frames, int outCh, float g0, float g1)
        {
            float v0 = L.Current;
            float v1 = L.Target;
            L.Current = v1;

            if (L.Independent && L.Hold)            // orda spenta: pronta a ripartire da capo
            {
                L.Reset();
                return;
            }

            double step = (double)L.Pcm.SampleRate / OutRate;
            int gate = L.Gate;
            float firstVol = gate == 3 ? L.FirstVol : 0f;

            // muta (nessun volume ne' dalla horde ne' dal primo giro): nessun calcolo, ma il playhead avanza (resta sincronizzata)
            if (v0 <= 1e-5f && v1 <= 1e-5f && firstVol <= 1e-5f)
            {
                L.Advance(frames * step);
                return;
            }

            double p = L.Pos;
            long wraps = L.Wraps;
            int sc = L.Pcm.Channels;
            float invFrames = 1f / frames;

            for (int f = 0; f < frames; f++)
            {
                float t = (f + 1) * invFrames;
                float vr = v0 + (v1 - v0) * t;
                bool audible;
                if (gate == 0) audible = true;
                else if (gate == 3)
                {
                    if (wraps == 0 && firstVol > vr) vr = firstVol;   // primo giro: volume pieno; dopo solo quello legato all'orda
                    audible = true;
                }
                else audible = ((wraps & 1) == 0) == (gate == 1);
                if (L.OneShot && p >= L.Pcm.Frames) audible = false;   // vittoria finita
                if (vr <= 1e-6f) audible = false;

                if (audible)
                {
                    float amp = vr * (g0 + (g1 - g0) * t);

                    int i1 = (int)p;
                    float fr = (float)(p - i1);
                    int o = f * outCh;

                    for (int c = 0; c < outCh; c++)
                    {
                        int sch;
                        if (sc == 1) sch = 0;
                        else if (c < sc) sch = c;
                        else continue;

                        float y1 = Sample(L, i1, sch);
                        float val;
                        if (fr < 1e-6f)
                        {
                            val = y1;
                        }
                        else
                        {
                            float y0 = Sample(L, i1 - 1, sch);
                            float y2 = Sample(L, i1 + 1, sch);
                            float y3 = Sample(L, i1 + 2, sch);
                            val = Hermite(y0, y1, y2, y3, fr);
                        }
                        mix[o + c] += val * amp;
                    }
                }

                p += step;
                if (!L.OneShot)
                {
                    while (p >= L.LoopEnd) { p -= L.LoopLen; wraps++; }
                }
            }

            L.Pos = p;
            L.Wraps = wraps;
        }

        private static float Sample(MixLayer L, int i, int ch)
        {
            if (L.OneShot)
            {
                if (i < 0) i = 0;
                if (i >= L.Pcm.Frames) return 0f;
                return L.Pcm.Samples[i * L.Pcm.Channels + ch];
            }
            if (i >= L.LoopEnd) i = L.LoopStart + (i - L.LoopEnd) % L.LoopLen;   // il vicino oltre la fine ricomincia dal loop
            if (i < 0) i = 0;
            var pcm = L.Pcm;
            if (i >= pcm.Frames) return 0f;
            return pcm.Samples[i * pcm.Channels + ch];
        }

        private static float Hermite(float y0, float y1, float y2, float y3, float t)
        {
            float c0 = y1;
            float c1 = 0.5f * (y2 - y0);
            float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
            float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
            return ((c3 * t + c2) * t + c1) * t + c0;
        }
    }
}
