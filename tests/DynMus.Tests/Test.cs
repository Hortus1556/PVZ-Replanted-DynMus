using System;
using System.IO;
using PvZDynamicMusic;

static class T
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "OK    " : "FAIL  ") + what); if (!ok) fails++; }

    static PcmData Ramp(int frames, int rate, int ch = 1, float scale = 1f, float offset = 0f)
    {
        var s = new float[frames * ch];
        for (int i = 0; i < frames; i++) for (int c = 0; c < ch; c++) s[i * ch + c] = (i / (float)frames) * scale + offset;
        return new PcmData { Name = "ramp", Samples = s, Channels = ch, SampleRate = rate };
    }

    static float[] RenderAll(MixerCore m, int total, int block, int ch = 1)
    {
        var o = new float[total * ch];
        for (int off = 0; off < total; off += block)
        {
            int n = Math.Min(block, total - off);
            var blk = new float[n * ch];
            m.Render(blk, n, ch);
            Array.Copy(blk, 0, o, off * ch, n * ch);
        }
        return o;
    }

    static MixSession Sess(PcmData b, int ls, int le, PcmData alt = null, PcmData horde = null, bool cross = false, int hls = 0, int hle = 0,
                           PcmData baseVar = null, PcmData hordeAlt = null, PcmData horde30 = null)
    {
        var g = new TrackGroup { Prefix = "t", Base = b, Alt = alt, Horde = horde, LoopStart = ls, LoopEnd = le, HordeLoopStart = hls, HordeLoopEnd = hle,
                                 BaseVar = baseVar, HordeAlt = hordeAlt, Horde30 = horde30 };
        return new MixSession(g, cross);
    }

    static void Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "check")
        {
            // Verifica sola lettura dei file reali: carica ogni musica con la libreria del mod e stampa cosa trova.
            string tmp = Path.Combine(Path.GetTempPath(), "mixtest_cfgcheck"); if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            string realCfg = Path.Combine(args[1], MusicConfig.FileName);
            if (File.Exists(realCfg)) File.Copy(realCfg, Path.Combine(tmp, MusicConfig.FileName), true);   // sola lettura: lavoriamo su una copia
            var cfg = MusicConfig.Load(tmp, Console.WriteLine, m => Console.WriteLine("WARN: " + m));
            cfg.Folder = args[1];
            var lib = new TrackLibrary(cfg, Console.WriteLine, m => Console.WriteLine("WARN: " + m));
            foreach (var p in new[] { "cd", "cys", "gw", "mg", "wg", "rm", "gtr", "lb", "cb", "zg", "ub", "bm", "credits" })
            {
                if (!lib.HasFiles(p)) { Console.WriteLine("[" + p + "] nessun file"); continue; }
                try
                {
                    var g = lib.GetAsync(p).Result;
                    if (g.Alt != null) Console.WriteLine("      alt: " + g.Alt.Frames + " frame vs base " + g.Base.Frames + " -> " + (g.Alt.Frames == g.Base.Frames && g.Alt.SampleRate == g.Base.SampleRate && g.Alt.Channels == g.Base.Channels ? "IDENTICI (durata, rate, canali)" : "DIVERSI"));
                    if (g.Horde != null) Console.WriteLine("      horde: " + g.Horde.Frames + " frame vs base " + g.Base.Frames + " -> " + (Math.Abs(g.Horde.Seconds - g.Base.Seconds) <= 0.05 ? "stessa durata" : "durata diversa (ok solo in crossfade)"));
                }
                catch (Exception e) { Console.WriteLine("[" + p + "] ERRORE: " + e.GetBaseException().Message); }
            }
            return;
        }

        // 1. loop con intro, stessa frequenza: sequenza esatta attraverso piu' giri
        {
            int N = 1000, ls = 300, le = 800;
            var s = Sess(Ramp(N, 48000), ls, le); s.Base.Target = 1f; s.Base.Current = 1f;
            var m = new MixerCore { OutRate = 48000 }; m.Start(s);
            var o = RenderAll(m, 3000, 256);
            bool ok = true; int badAt = -1;
            for (int k = 0; k < 3000; k++)
            {
                int pos = k < le ? k : ls + (k - le) % (le - ls);
                if (Math.Abs(o[k] - pos / (float)N) > 1e-6f) { ok = false; badAt = k; break; }
            }
            Check(ok, "loop con intro (start=300,end=800) sample-exact su 3000 frame" + (ok ? "" : " (errore a " + badAt + ")"));
        }

        // 2. base + horde agganciata: sincronia e volumi indipendenti
        {
            int N = 500;
            var s = Sess(Ramp(N, 48000, 2, 1f), 0, N, null, Ramp(N, 48000, 2, 0.5f));
            s.Base.Target = s.Base.Current = 0.8f; s.Horde.Target = s.Horde.Current = 0.5f;
            var m = new MixerCore { OutRate = 48000 }; m.Start(s);
            var o = RenderAll(m, 1700, 300, 2);
            bool ok = true;
            for (int k = 0; k < 1700; k++) { float p = (k % N) / (float)N; float exp = p * 0.8f + p * 0.5f * 0.5f; if (Math.Abs(o[k * 2] - exp) > 1e-5f || Math.Abs(o[k * 2 + 1] - exp) > 1e-5f) { ok = false; Console.WriteLine("  diff a " + k); break; } }
            Check(ok, "base+horde agganciata: sincronizzate dopo 3 giri di loop (stereo)");
        }

        // 3. ricampionamento 44100 -> 48000
        {
            int rateIn = 44100, N = rateIn;
            var s0 = new float[N];
            for (int i = 0; i < N; i++) s0[i] = (float)Math.Sin(2 * Math.PI * 441.0 * i / rateIn);
            var s = Sess(new PcmData { Name = "sin", Samples = s0, Channels = 1, SampleRate = rateIn }, 0, N);
            s.Base.Target = s.Base.Current = 1f;
            var m = new MixerCore { OutRate = 48000 }; m.Start(s);
            int total = 48000 * 2 + 777;
            var o = RenderAll(m, total, 1024);
            double maxErr = 0;
            for (int k = 0; k < total; k++) maxErr = Math.Max(maxErr, Math.Abs(o[k] - Math.Sin(2 * Math.PI * 441.0 * k / 48000.0)));
            Check(maxErr < 2e-3, "ricampionamento cubico 44.1k->48k su 2 s attraverso il wrap: errore max " + maxErr.ToString("E2"));
        }

        // 4. pausa
        {
            int N = 4000;
            var s = Sess(Ramp(N, 48000), 0, N); s.Base.Target = s.Base.Current = 1f;
            var m = new MixerCore { OutRate = 48000 }; m.Start(s);
            m.Render(new float[512], 512, 1);
            m.Paused = true; m.Render(new float[512], 512, 1);
            double posAfterRamp = s.Base.Pos;
            var silent = new float[512]; m.Render(silent, 512, 1);
            bool allZero = true; foreach (var v in silent) if (v != 0f) allZero = false;
            Check(allZero && s.Base.Pos == posAfterRamp, "pausa: silenzio completo e playhead fermo");
            m.Paused = false; var resume = new float[512]; m.Render(resume, 512, 1);
            Check(s.Base.Pos > posAfterRamp && resume[511] > 0f, "ripresa dopo la pausa");
        }

        // 5. rampa di volume
        {
            int N = 2000;
            var b = new PcmData { Name = "dc", Samples = new float[N], Channels = 1, SampleRate = 48000 };
            for (int i = 0; i < N; i++) b.Samples[i] = 1f;
            var s = Sess(b, 0, N); s.Base.Current = 0f; s.Base.Target = 1f;
            var m = new MixerCore { OutRate = 48000 }; m.Start(s);
            var blk = new float[1000]; m.Render(blk, 1000, 1);
            bool mono = true; for (int i = 1; i < blk.Length; i++) if (blk[i] < blk[i - 1]) mono = false;
            Check(mono && blk[0] > 0f && Math.Abs(blk[999] - 1f) < 1e-3f, "rampa di volume 0->1 lineare e monotona nel blocco");
        }

        // 6. _alt: giri alterni base / alt / base / alt (con intro: il primo giro include l'intro)
        {
            int N = 1000, ls = 300, le = 800;
            var s = Sess(Ramp(N, 48000), ls, le, Ramp(N, 48000, 1, 1f, 10f));   // alt = stessa forma + 10
            s.Base.Target = s.Base.Current = 1f; s.Alt.Target = s.Alt.Current = 1f;
            var m = new MixerCore { OutRate = 48000 }; m.Start(s);
            int total = 800 + 500 * 4;   // giro 0 (0..799), poi 4 giri completi di 500
            var o = RenderAll(m, total, 333);
            bool ok = true; int badAt = -1;
            for (int k = 0; k < total; k++)
            {
                int pass, pos;
                if (k < le) { pass = 0; pos = k; } else { int r = k - le; pass = 1 + r / (le - ls); pos = ls + r % (le - ls); }
                float exp = pos / (float)N + ((pass % 2 == 1) ? 10f : 0f);
                if (Math.Abs(o[k] - exp) > 1e-5f) { ok = false; badAt = k; break; }
            }
            Check(ok, "_alt: alternanza base/alt/base/alt a ogni giro di loop, sample-exact" + (ok ? "" : " (errore a " + badAt + ")"));
        }

        // 7. _alt con base muta: i playhead restano allineati e la parita' e' quella giusta quando la base torna udibile
        {
            int N = 400;
            var s = Sess(Ramp(N, 48000), 0, N, Ramp(N, 48000, 1, 1f, 10f));
            s.Base.Target = s.Base.Current = 0f; s.Alt.Target = s.Alt.Current = 0f;
            var m = new MixerCore { OutRate = 48000 }; m.Start(s);
            RenderAll(m, 900, 128);                   // 2 giri completi + 100 frame: ora siamo nel giro 2 (pari) -> base
            s.Base.Target = s.Base.Current = 1f; s.Alt.Target = s.Alt.Current = 1f;
            var o = RenderAll(m, 300, 128);
            // posizione attuale: 900 % 400 = 100 -> giro 2 (pari) = base, poi al frame 300 -> giro 3 (dispari) = alt
            bool ok = true;
            for (int k = 0; k < 300; k++)
            {
                int abs = 900 + k; int pass = abs / N; int pos = abs % N;
                float exp = pos / (float)N + ((pass % 2 == 1) ? 10f : 0f);
                if (Math.Abs(o[k] - exp) > 1e-5f) { ok = false; break; }
            }
            Check(ok, "_alt: i due playhead avanzano anche da muti e restano in fase");
        }

        // 8. crossfade: horde indipendente, riparte da capo ogni volta che rientra; la base continua
        {
            int NB = 1000, NH = 700;
            var horde = Ramp(NH, 48000, 1, 1f, 100f);
            var s = Sess(Ramp(NB, 48000), 0, NB, null, horde, cross: true, hls: 0, hle: NH);
            s.Base.Target = s.Base.Current = 1f; s.Horde.Target = 0f; s.Horde.Hold = true;
            var m = new MixerCore { OutRate = 48000 }; m.Start(s);
            var o1 = RenderAll(m, 450, 100);
            bool silentHorde = true; for (int k = 0; k < 450; k++) if (Math.Abs(o1[k] - k / (float)NB) > 1e-6f) silentHorde = false;
            Check(silentHorde && s.Horde.Pos == 0, "crossfade: orda ferma a 0 e muta mentre la base suona");

            // l'orda entra: parte da capo (indipendentemente dalla base, che e' a 450)
            s.Horde.Hold = false; s.Horde.Target = 1f; s.Horde.Current = 1f; s.Base.Target = s.Base.Current = 0f;
            var o2 = RenderAll(m, 800, 128);
            bool ok = true;
            for (int k = 0; k < 800; k++) { float exp = 100f + (k % NH) / (float)NH; if (Math.Abs(o2[k] - exp) > 1e-4f) { ok = false; Console.WriteLine("  diff a " + k + " " + o2[k] + " vs " + exp); break; } }
            Check(ok, "crossfade: la horde parte dal suo primo campione e cicla sulla propria lunghezza (700 vs base 1000)");
            double basePosAfter = s.Base.Pos;
            Check(Math.Abs(basePosAfter - ((450 + 800) % NB)) < 1e-6, "crossfade: la base ha continuato ad avanzare senza mai ripartire (pos " + basePosAfter + ")");

            // l'orda esce del tutto (Hold), poi rientra: deve ripartire di nuovo da capo
            s.Horde.Target = 0f; s.Horde.Hold = true; RenderAll(m, 300, 128);
            Check(s.Horde.Pos == 0, "crossfade: a orda spenta il playhead torna a 0");
            s.Horde.Hold = false; s.Horde.Target = 1f; s.Horde.Current = 1f;
            var o3 = RenderAll(m, 10, 10);
            Check(Math.Abs(o3[0] - 100f) < 1e-5f, "crossfade: al rientro successivo riparte ancora da capo");
        }

        // 12. base_var: solo nel primo giro del loop; dal secondo solo se legata all'orda; mai se non permessa (livello 1-1)
        {
            int N = 1000, ls = 300, le = 800;
            var s1 = Sess(Ramp(N, 48000), ls, le, baseVar: Ramp(N, 48000, 1, 1f, 20f));
            s1.Base.Target = s1.Base.Current = 1f;
            s1.BaseVar.FirstVol = 1f; s1.BaseVar.Target = s1.BaseVar.Current = 0f;
            var m = new MixerCore { OutRate = 48000 }; m.Start(s1);
            int total = 800 + 500 * 2;
            var o = RenderAll(m, total, 300);
            bool ok = true; int bad = -1;
            for (int k = 0; k < total; k++)
            {
                int pass, pos;
                if (k < le) { pass = 0; pos = k; } else { int r = k - le; pass = 1 + r / (le - ls); pos = ls + r % (le - ls); }
                float exp = pos / (float)N + (pass == 0 ? 20f + pos / (float)N : 0f);
                if (Math.Abs(o[k] - exp) > 1e-4f) { ok = false; bad = k; break; }
            }
            Check(ok, "base_var: suona solo nel primo giro del loop, dal secondo tace" + (ok ? "" : " (errore a " + bad + ")"));

            // con l'orda (Target legato all'orda) suona anche dal secondo giro, sincronizzata con la base
            var s2 = Sess(Ramp(N, 48000), ls, le, baseVar: Ramp(N, 48000, 1, 1f, 20f));
            s2.Base.Target = s2.Base.Current = 1f;
            s2.BaseVar.FirstVol = 1f; s2.BaseVar.Target = s2.BaseVar.Current = 0f;
            var m2 = new MixerCore { OutRate = 48000 }; m2.Start(s2);
            RenderAll(m2, 1000, 250);                        // primo giro finito: siamo nel giro 1
            s2.BaseVar.Target = s2.BaseVar.Current = 1f;      // arriva l'orda
            var o2 = RenderAll(m2, 200, 100);
            bool ok2 = true;
            for (int k = 0; k < 200; k++)
            {
                int r = 1000 - le + k; int pos = ls + r % (le - ls);
                float exp = pos / (float)N + 20f + pos / (float)N;
                if (Math.Abs(o2[k] - exp) > 1e-4f) { ok2 = false; break; }
            }
            Check(ok2, "base_var: con l'orda rientra anche dopo il primo giro, in fase con la base");

            // livello 1-1: non permessa -> mai
            var s3 = Sess(Ramp(N, 48000), ls, le, baseVar: Ramp(N, 48000, 1, 1f, 20f));
            s3.Base.Target = s3.Base.Current = 1f;
            s3.BaseVar.FirstVol = 0f; s3.BaseVar.Target = s3.BaseVar.Current = 0f;
            var m3 = new MixerCore { OutRate = 48000 }; m3.Start(s3);
            var o3 = RenderAll(m3, 1500, 300);
            bool ok3 = true; for (int k = 0; k < 1500; k++) { int pos = k < le ? k : ls + (k - le) % (le - ls); if (Math.Abs(o3[k] - pos / (float)N) > 1e-5f) { ok3 = false; break; } }
            Check(ok3, "base_var: nel livello 1-1 (non permessa) non si sente mai");
        }

        // 13. horde_alt: con _base suona _horde, con _alt suona _horde_alt; senza horde_alt la horde suona in entrambi i giri
        {
            int N = 500;
            var s1 = Sess(Ramp(N, 48000), 0, N, Ramp(N, 48000, 1, 1f, 10f), Ramp(N, 48000, 1, 1f, 100f), hordeAlt: Ramp(N, 48000, 1, 1f, 200f));
            foreach (var L in s1.All) if (L != null) L.Target = L.Current = 1f;
            var m = new MixerCore { OutRate = 48000 }; m.Start(s1);
            var o = RenderAll(m, 1500, 217);
            bool ok = true;
            for (int k = 0; k < 1500; k++)
            {
                int pass = k / N; float p = (k % N) / (float)N;
                float exp = pass % 2 == 0 ? p + (p + 100f) : (p + 10f) + (p + 200f);
                if (Math.Abs(o[k] - exp) > 1e-3f) { ok = false; break; }
            }
            Check(ok, "horde_alt: giro pari base+horde, giro dispari alt+horde_alt");

            var s2 = Sess(Ramp(N, 48000), 0, N, Ramp(N, 48000, 1, 1f, 10f), Ramp(N, 48000, 1, 1f, 100f));   // niente horde_alt
            foreach (var L in s2.All) if (L != null) L.Target = L.Current = 1f;
            var m2 = new MixerCore { OutRate = 48000 }; m2.Start(s2);
            var o2 = RenderAll(m2, 1000, 217);
            bool ok2 = true;
            for (int k = 0; k < 1000; k++)
            {
                int pass = k / N; float p = (k % N) / (float)N;
                float exp = (pass % 2 == 0 ? p : p + 10f) + (p + 100f);
                if (Math.Abs(o2[k] - exp) > 1e-3f) { ok2 = false; break; }
            }
            Check(ok2, "senza horde_alt: la horde suona in entrambi i giri di base/alt");
        }

        // 14. libreria con sottocartelle (una cartella per musica) e tutti i nuovi file
        {
            string root = Path.Combine(Path.GetTempPath(), "mixtest_lib"); if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(Path.Combine(root, "gw")); Directory.CreateDirectory(Path.Combine(root, "rm")); Directory.CreateDirectory(Path.Combine(root, "extra", "zz"));
            foreach (var n in new[] { "gw/gw_base", "gw/gw_base_var", "gw/gw_horde", "gw/gw_horde_30", "gw/gw_vic", "rm/rm_base", "rm/rm_alt", "rm/rm_horde", "rm/rm_horde_alt", "zz", "extra/zz/zz" })
                WriteWav(Path.Combine(root, n.Replace('/', Path.DirectorySeparatorChar) + ".wav"), 16, 1, 2, 48000, 2000);
            string tmp = Path.Combine(Path.GetTempPath(), "mixtest_cfglib"); if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            var cfg = MusicConfig.Load(tmp, null, null); cfg.Folder = root;
            var warns = new System.Collections.Generic.List<string>();
            var lib = new TrackLibrary(cfg, _ => { }, w => warns.Add(w));
            Check(lib.FindFile("gw", "_base_var") != null && lib.FindFile("gw", "_base_var").EndsWith(Path.Combine("gw", "gw_base_var.wav")), "libreria: trova gw_base_var.wav nella sottocartella gw");
            Check(lib.FindFile("rm", "_horde_alt") != null && lib.HasVic("gw") && !lib.HasVic("rm"), "libreria: trova rm_horde_alt e gw_vic, e sa che rm_vic non c'e'");
            var g = lib.GetAsync("gw").Result;
            Check(g.BaseVar != null && g.Horde != null && g.Horde30 != null && g.Alt == null, "libreria: gw carica base + base_var + horde + horde_30");
            var g2 = lib.GetAsync("rm").Result;
            Check(g2.Alt != null && g2.Horde != null && g2.HordeAlt != null, "libreria: rm carica base + alt + horde + horde_alt");
            Check(lib.FindFile("zz", "") != null && !lib.FindFile("zz", "").Contains("extra"), "libreria: con lo stesso nome in due posti usa il file piu' vicino alla radice (e avvisa)");
            Check(lib.ListFiles().Count == 10, "libreria: ListFiles elenca i file relativi (10 nomi unici)");
        }

        // 15. config: soglia e fade di horde_30
        {
            string dir = Path.Combine(Path.GetTempPath(), "mixtest_cfg30"); if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.ini"), "[generale]\nVersione = 3\n[fade]\nHordeFadeIn = 6\nHordeFadeOut = 9\nHorde30Zombie = 25\ngw.Horde30Zombie = 40\ngw.Horde30FadeOut = 20\n");
            var c = MusicConfig.Load(dir, null, null);
            var fg = c.GetFades("gw"); var fr = c.GetFades("rm");
            Check(c.GetHorde30Threshold("rm") == 25 && c.GetHorde30Threshold("gw") == 40, "config: soglia horde_30 globale (25) e per musica (gw = 40)");
            Check(fr.Horde30In == 6f && fr.Horde30Out == 9f && fg.Horde30In == 6f && fg.Horde30Out == 20f, "config: fade di horde_30 = quelli della horde, salvo override");
            var defaults = MusicConfig.Load(Path.Combine(Path.GetTempPath(), "mixtest_cfg_default_" + Guid.NewGuid().ToString("N")), null, null);
            Check(defaults.GetHorde30Threshold("gw") == 30, "config: soglia di default 30");
        }

        // 16. Ctrl+L: salto a N secondi dalla fine del loop, tutte le tracce nello stesso blocco e in fase
        {
            int N = 1000, ls = 300, le = 800;
            var s1 = Sess(Ramp(N, 48000), ls, le, Ramp(N, 48000, 1, 1f, 10f), Ramp(N, 48000, 1, 1f, 100f), baseVar: Ramp(N, 48000, 1, 1f, 20f));
            foreach (var L in s1.All) if (L != null) L.Target = L.Current = 1f;
            var m = new MixerCore { OutRate = 48000 }; m.Start(s1);
            RenderAll(m, 200, 100);                                   // siamo nel primo giro, posizione 200
            s1.SeekBeforeLoopEnd = 100f / 48000f;                     // 100 frame prima della fine del loop (800) -> 700
            var o = RenderAll(m, 300, 300);                           // un solo blocco: 700..799 poi il wrap a 300, giro 1 (alt)
            bool ok = true;
            for (int k = 0; k < 300; k++)
            {
                int pass, pos;
                if (k < 100) { pass = 0; pos = 700 + k; } else { pass = 1; pos = 300 + (k - 100); }
                float p0 = pos / (float)N;
                // giro 0: base + horde + base_var(FirstVol non impostata: solo Target=1 -> udibile); giro 1: alt + horde
                float exp = pass == 0 ? p0 + (p0 + 100f) + (p0 + 20f) : (p0 + 10f) + (p0 + 100f) + (p0 + 20f);
                if (Math.Abs(o[k] - exp) > 1e-3f) { ok = false; Console.WriteLine("  diff a " + k + ": " + o[k] + " vs " + exp); break; }
            }
            Check(ok, "Ctrl+L: tutte le tracce saltano insieme a 700, poi il loop passa al giro dispari (alt) in fase");
            Check(s1.SeekBeforeLoopEnd < 0f, "Ctrl+L: la richiesta viene consumata dal thread audio");

            // loop piu' corto dei secondi richiesti: si ferma all'inizio del loop
            var s2 = Sess(Ramp(N, 48000), ls, le);
            s2.Base.Target = s2.Base.Current = 1f;
            var m2 = new MixerCore { OutRate = 48000 }; m2.Start(s2);
            RenderAll(m2, 50, 50);
            s2.SeekBeforeLoopEnd = 4f;                                // 4 s = 192000 frame > lunghezza del loop (500)
            var o2 = RenderAll(m2, 10, 10);
            Check(Math.Abs(o2[0] - ls / (float)N) < 1e-6f, "Ctrl+L: con un loop piu' corto di 4 s va all'inizio del loop");
        }

        // 9b. vittoria: one-shot sovrapposto alla musica, una sola volta, poi silenzio (e non segue la pausa)
        {
            int N = 1000;
            var main = Sess(Ramp(N, 48000), 0, N); main.Base.Target = main.Base.Current = 1f;
            var vicPcm = Ramp(300, 48000, 1, 1f, 5f);   // valori 5.0 .. 6.0, lunga 300 frame
            var x = new MixSession(new TrackGroup { Prefix = "v", Base = vicPcm, LoopStart = 0, LoopEnd = 300 }, false, true);
            x.Base.Target = x.Base.Current = 1f;
            var m = new MixerCore { OutRate = 48000 }; m.Start(main); m.StartExtra(x);
            var o = RenderAll(m, 700, 128);
            bool ok = true; int bad = -1;
            for (int k = 0; k < 700; k++)
            {
                float exp = k / (float)N + (k < 300 ? 5f + k / 300f : 0f);
                if (Math.Abs(o[k] - exp) > 1e-5f) { ok = false; bad = k; break; }
            }
            Check(ok, "vittoria: suona una volta sovrapposta alla musica, poi resta muta senza loop" + (ok ? "" : " (errore a " + bad + ")"));
            Check(x.Base.Pos >= vicPcm.Frames, "vittoria: il playhead segnala la fine (serve al plugin per chiuderla)");

            // con la musica in pausa la vittoria continua a suonare
            var x2 = new MixSession(new TrackGroup { Prefix = "v", Base = Ramp(4000, 48000, 1, 1f, 5f), LoopStart = 0, LoopEnd = 4000 }, false, true);
            x2.Base.Target = x2.Base.Current = 1f;
            var m2 = new MixerCore { OutRate = 48000 };
            var main2 = Sess(Ramp(N, 48000), 0, N); main2.Base.Target = main2.Base.Current = 1f;
            m2.Start(main2); m2.StartExtra(x2); m2.Paused = true;
            RenderAll(m2, 512, 512);                       // rampa di pausa della musica
            var o2 = RenderAll(m2, 256, 128);
            bool vicOnly = true; for (int k = 0; k < 256; k++) if (o2[k] < 5f) vicOnly = false;
            Check(vicOnly && main2.Base.Pos < 1000, "vittoria: la musica in pausa tace ma la vittoria continua");
        }

        // 17. WAV senza campioni audio: rifiutato con un errore leggibile (prima mandava in loop infinito il thread audio)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mixtest_wav"); Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, "vuoto.wav");
            WriteWav(p, 16, 1, 2, 48000, 0);
            bool threw = false; string msg = "";
            try { WavLoader.Load(p); } catch (InvalidDataException e) { threw = true; msg = e.Message; }
            Check(threw && msg.Contains("0 frame"), "WAV con 0 frame: rifiutato con InvalidDataException (" + msg + ")");
        }

        // 18. [mappa]: una voce vuota = "non sostituire" e non viene mai silenziata, nemmeno con MusicaNonMappata = silence
        {
            string dir = Path.Combine(Path.GetTempPath(), "mixtest_cfgmap"); if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.ini"), "[generale]\nVersione = 3\nMusicaNonMappata = silence\n[mappa]\nZenGarden =\nDayGrasswalk = gw\n");
            var c = MusicConfig.Load(dir, null, null);
            Check(c.SilenceUnmapped && c.IsMapped("ZenGarden") && c.Candidates("ZenGarden").Count == 0 && c.IsMapped("DayGrasswalk") && !c.IsMapped("NightMoongrains"),
                  "[mappa]: voce vuota = elencata (IsMapped) e senza candidati; musica non elencata = non mappata");
        }

        // 19. fade con NaN/Infinity nel config: scartati con un avviso, valgono i valori di default
        {
            string dir = Path.Combine(Path.GetTempPath(), "mixtest_cfgnan"); if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.ini"), "[generale]\nVersione = 3\n[fade]\nHordeFadeIn = NaN\nHordeFadeOut = Infinity\nBaseFadeOut = 5\n");
            var warns = new System.Collections.Generic.List<string>();
            var c = MusicConfig.Load(dir, null, w => warns.Add(w));
            var f = c.GetFades("gw");
            bool finite = float.IsFinite(f.HordeIn) && float.IsFinite(f.HordeOut) && float.IsFinite(f.Horde30In) && float.IsFinite(f.Horde30Out);
            Check(finite && f.HordeIn == 8f && f.HordeOut == 12f && f.BaseOut == 5f && warns.Count == 2,
                  "fade NaN/Infinity: scartati con " + warns.Count + " avvisi, default 8/12 e BaseFadeOut = 5 intatto");
        }

        // 9. decoder WAV
        {
            string dir = Path.Combine(Path.GetTempPath(), "mixtest_wav"); Directory.CreateDirectory(dir);
            foreach (var t in new[] { new { bits = 16, fmt = 1, name = "w16" }, new { bits = 24, fmt = 1, name = "w24" }, new { bits = 32, fmt = 3, name = "wf32" } })
            {
                string p = Path.Combine(dir, t.name + ".wav");
                WriteWav(p, t.bits, t.fmt, 2, 44100, 1000);
                var d = WavLoader.Load(p);
                bool ok = d.Channels == 2 && d.SampleRate == 44100 && d.Frames == 1000;
                for (int i = 0; ok && i < 1000; i++) { float exp = (float)Math.Sin(i * 0.05); if (Math.Abs(d.Samples[i * 2] - exp) > 1e-3f || Math.Abs(d.Samples[i * 2 + 1] + exp) > 1e-3f) ok = false; }
                Check(ok, "decoder WAV " + t.bits + " bit fmt " + t.fmt);
            }
        }

        // 10. config: candidati, fade con override per musica, migrazione versione
        {
            string dir = Path.Combine(Path.GetTempPath(), "mixtest_cfg"); if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.ini"), "[mappa]\nZenGarden = cd\n");        // vecchia versione (senza Versione)
            var cfg = MusicConfig.Load(dir, null, null);
            Check(File.Exists(Path.Combine(dir, "config.ini.v1.bak")), "config vecchio: sostituito e copia salvata come .bak");
            var zen = cfg.Candidates("ZenGarden");
            Check(zen.Count == 2 && zen[0] == "zg" && zen[1] == "cd", "ZenGarden = zg, cd  (candidati in ordine)");
            var day = cfg.Candidates("DayGrasswalk");
            Check(day.Count == 1 && day[0] == "gw" && cfg.Candidates("MusicaCheNonEsiste").Count == 0, "Zombotany non ha regole speciali: un tune di sfondo -> il suo prefisso (gw)");
            var f = cfg.GetFades("mg");
            Check(f.HordeIn == 8f && f.HordeOut == 12f && f.BaseOut == 8f && f.BaseIn == 12f, "fade di default 8/12/8/12");
            File.WriteAllText(Path.Combine(dir, "config.ini"), "[generale]\nVersione = 3\n[fade]\nHordeFadeIn = 5\nmg.HordeFadeIn = 20\nmg.Crossfade = true\n");
            var cfg2 = MusicConfig.Load(dir, null, null);
            Check(cfg2.GetFades("gw").HordeIn == 5f && cfg2.GetFades("mg").HordeIn == 20f && cfg2.GetCrossfadeOverride("mg") == true && cfg2.GetCrossfadeOverride("gw") == null,
                  "fade globali + override per musica + Crossfade forzato");
        }

        Console.WriteLine(fails == 0 ? "\nTUTTI I TEST OK" : "\n" + fails + " TEST FALLITI");
        Environment.Exit(fails == 0 ? 0 : 1);
    }

    static void WriteWav(string path, int bits, int fmt, int ch, int rate, int frames)
    {
        using var f = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(f);
        int bytesPer = bits / 8; int dataLen = frames * ch * bytesPer;
        w.Write(new byte[] { 82, 73, 70, 70 }); w.Write(36 + dataLen); w.Write(new byte[] { 87, 65, 86, 69 });
        w.Write(new byte[] { 102, 109, 116, 32 }); w.Write(16); w.Write((short)fmt); w.Write((short)ch); w.Write(rate); w.Write(rate * ch * bytesPer); w.Write((short)(ch * bytesPer)); w.Write((short)bits);
        w.Write(new byte[] { 100, 97, 116, 97 }); w.Write(dataLen);
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < ch; c++)
            {
                double v = Math.Sin(i * 0.05) * (c == 0 ? 1 : -1);
                if (fmt == 3) w.Write((float)v);
                else if (bits == 16) w.Write((short)Math.Round(v * 32767));
                else { int iv = (int)Math.Round(v * 8388607); w.Write((byte)iv); w.Write((byte)(iv >> 8)); w.Write((byte)(iv >> 16)); }
            }
    }
}
