using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using AudioService = Il2CppReloaded.Services.AudioService;
using FoleyType = Il2CppReloaded.Services.FoleyType;
using MusicTune = Il2CppReloaded.Services.MusicTune;
using Board = Il2CppReloaded.Gameplay.Board;
using ZombieType = Il2CppReloaded.Gameplay.ZombieType;
using CoinType = Il2CppReloaded.Gameplay.CoinType;
using CoinMotion = Il2CppReloaded.Gameplay.CoinMotion;
using MusicBurstState = Il2CppReloaded.Services.MusicBurstState;
using MusicDrumsState = Il2CppReloaded.Services.MusicDrumsState;
using AudioSourceWrapper = Il2Cpp.AudioSourceWrapper;

[assembly: MelonInfo(typeof(PvZDynamicMusic.DynamicMusicMod), "PvZ Dynamic Music", "1.3.0", "Hortus1556")]
[assembly: MelonGame("PopCap Games", "PvZ Replanted")]

namespace PvZDynamicMusic
{
    /// <summary>
    /// Sostituisce la musica di PvZ Replanted con file .wav esterni mantenendo la musica dinamica ORIGINALE:
    /// il gioco continua a far girare la sua macchina a stati (zombie in campo -> burst) con le sue sorgenti audio silenziate;
    /// il mod legge lo stato del burst (quando arriva/finisce l'orda) e il volume generale, e gestisce lui i fade delle tue tracce.
    /// </summary>
    public class DynamicMusicMod : MelonMod
    {
        internal static DynamicMusicMod Instance;
        internal static readonly MixerCore Mixer = new MixerCore();
        internal static MusicConfig Config;
        internal static TrackLibrary Library;

        private GameObject _host;
        private AudioSource _hostSource;
        private bool _hostFailed;
        private bool _preloaded;
        private float _nextHostCheck;

        private AudioService _svc;                 // ultimo AudioService visto dagli hook
        private AudioSourceWrapper _muted;         // wrapper della musica del gioco che teniamo silenziato
        private string _activePrefix;
        private Task<TrackGroup> _pending;
        private string _pendingPrefix;
        private FadeSettings _fades;
        private int _horde30Threshold = 30;
        private int _lastZombies;
        private float _master = 1f;                // volume generale del gioco (slider musica x fade di fine livello)

        // vittoria (xx_vic.wav)
        private string _lastPrefix;                // prefisso della musica del livello in corso (resta anche dopo StopAllMusic)
        private Task<TrackGroup> _vicTask;
        private string _vicPrefix;
        private float _lastVicTime = -100f;
        private bool _lastVicHandled;

        // tasti di debug
        private bool _keysBroken;

        private float _sessionStart;
        private long _callbacksAtStart;
        private bool _callbackWarned;
        private bool _callbackConfirmed;
        private float _nextDebug;
        private float _lastErrorLog;
        private readonly HashSet<string> _warnedMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ avvio

        public override void OnInitializeMelon()
        {
            Instance = this;
            try
            {
                string folder = Path.Combine(MelonEnvironment.ModsDirectory, "DynMus");
                Config = MusicConfig.Load(folder, Log, Warn);
                Library = new TrackLibrary(Config, Log, Warn);

                ClassInjector.RegisterTypeInIl2Cpp<DynamicMusicBehaviour>();

                var t = typeof(AudioService);
                PatchPostfix(t, "PlayFromOffset", nameof(Hooks.PlayFromOffsetPost));
                PatchPostfix(t, "StopAllMusic", nameof(Hooks.StopAllMusicPost));
                PatchPrefix(t.GetMethod("PlayFoley", new[] { typeof(FoleyType) }), "PlayFoley(FoleyType)", nameof(Hooks.PlayFoleyPre));
                PatchPrefix(t.GetMethod("PlayFoley", new[] { typeof(FoleyType), typeof(bool) }), "PlayFoley(FoleyType,bool)", nameof(Hooks.PlayFoleyPre));

                Log("Attivo. Cartella musica: " + folder);
                Log("File trovati: " + DescribeFiles());
            }
            catch (Exception e)
            {
                Error("Errore in avvio: " + e);
            }
        }

        private void PatchPostfix(Type type, string method, string hook)
        {
            var original = AccessTools.Method(type, method);
            if (original == null) { Error($"metodo {type.Name}.{method} non trovato: il mod non puo' funzionare"); return; }
            HarmonyInstance.Patch(original, postfix: new HarmonyMethod(typeof(Hooks), hook));
            Log($"hook installato su {type.Name}.{method}");
        }

        private void PatchPrefix(System.Reflection.MethodBase original, string label, string hook)
        {
            if (original == null) { Warn($"metodo AudioService.{label} non trovato: le musiche di vittoria (_vic) non funzioneranno"); return; }
            HarmonyInstance.Patch(original, prefix: new HarmonyMethod(typeof(Hooks), hook));
            Log($"hook installato su AudioService.{label}");
        }

        private string DescribeFiles()
        {
            var found = Library.ListFiles();
            return found.Count == 0 ? "nessuno (metti i .wav nella cartella, anche in sottocartelle, e riavvia il gioco)" : string.Join(", ", found);
        }

        // ------------------------------------------------------------------ eventi dal gioco (thread principale)

        /// <summary>Il gioco ha appena fatto partire una musica (PlayMusic / MakeSureMusicIsPlaying / StartGameMusic).</summary>
        internal void OnGameMusicStarted(AudioService svc)
        {
            _svc = svc;
            MusicTune tune = svc.m_currentMusicTune;
            var wrapper = svc.m_currentMusicSource;
            if (tune == MusicTune.None || wrapper == null)
            {
                ReleaseCurrent(null);
                return;
            }

            // una nuova musica sostituisce l'eventuale musica di vittoria ancora in corso
            if (Mixer.Extra != null) Log("nuova musica del gioco: la musica di vittoria si interrompe");
            Mixer.StopExtra();
            _vicTask = null;

            string tuneName = tune.ToString();
            var candidates = Config.Candidates(tuneName);
            string prefix = Library.FirstAvailable(candidates);
            bool haveFiles = prefix != null;
            bool silence = Config.SilenceUnmapped && !Config.IsMapped(tuneName);   // solo le musiche NON elencate in [mappa]
            bool handle = haveFiles || silence;
            _lastPrefix = prefix;   // serve per scegliere xx_vic.wav quando finisce il livello
            if (haveFiles) Library.PreloadVic(prefix);

            Log($"musica del gioco: {tuneName} -> "
                + (haveFiles ? $"file '{prefix}'"
                   : candidates.Count > 0 ? $"file '{string.Join("' / '", candidates)}' NON trovato, resta l'originale"
                   : silence ? "silenzio" : "originale"));

            if (!haveFiles && candidates.Count > 0 && _warnedMissing.Add(tuneName))
                Warn($"per {tuneName} manca {string.Join(" / ", candidates.ConvertAll(c => c + "_base.wav (o " + c + ".wav)"))} in {Config.Folder}: resta la musica originale");

            ReleaseCurrent(handle ? wrapper : null);
            if (!handle) return;

            SetMute(wrapper, true);
            _muted = wrapper;

            if (haveFiles)
            {
                _activePrefix = prefix;
                _pendingPrefix = prefix;
                _pending = Library.GetAsync(prefix);
            }

            if (Config.Debug)
                Log($"  wrapper: BPM={wrapper.m_BPM}, hihatsReplacesRegular={wrapper.m_hihatsReplacesRegular}, introOffset={wrapper.m_introOffset}, "
                    + $"sorgenti: main={Has(wrapper.m_audioSource)} hihats={Has(wrapper.m_hihatsAudioSource)} drums={Has(wrapper.m_drumsAudioSource)}");
        }

        /// <summary>Il gioco ha fermato la musica (StopAllMusic).</summary>
        internal void OnGameMusicStopped(AudioService svc)
        {
            _svc = svc;
            ReleaseCurrent(null);

            // Come nel PvZ originale, fermare la musica azzera la macchina a stati del burst:
            // senza questo, l'inizio del livello successivo ripartirebbe con l'orda "rimasta accesa".
            try
            {
                svc.mMusicBurstState = MusicBurstState.Off;
                svc.mMusicDrumsState = MusicDrumsState.Off;
                svc.mBurstStateCounter = 0f;
                svc.mDrumsStateCounter = 0f;
                svc.mWasFromFadein = false;
            }
            catch (Exception e) { Warn("reset burst fallito: " + e.Message); }
        }

        /// <summary>
        /// Il gioco sta per suonare un "foley". Alla fine del livello (Board.FadeOutLevel, quando raccogli il premio) suona
        /// WinMusic / FinalFanfare / RipWin: se esiste xx_vic.wav per la musica del livello lo suoniamo noi al posto del jingle.
        /// Restituisce true = salta il suono originale.
        /// </summary>
        internal bool OnFoley(AudioService svc, FoleyType type)
        {
            if (type != FoleyType.WinMusic && type != FoleyType.FinalFanfare && type != FoleyType.RipWin) return false;

            _svc = svc;
            float now = Time.realtimeSinceStartup;
            if (now - _lastVicTime < 2f) return _lastVicHandled;   // stessa vittoria chiamata due volte (overload)

            string prefix = _lastPrefix;
            bool handled = prefix != null && Library.HasVic(prefix);
            _lastVicTime = now;
            _lastVicHandled = handled;

            Log($"fine livello ({type}): "
                + (handled ? $"suono '{prefix}_vic' al posto del jingle del gioco"
                   : prefix == null ? "musica del livello non sostituita, resta il jingle originale"
                   : $"manca '{prefix}_vic.wav', resta il jingle originale"));
            if (!handled) return false;

            Mixer.StopExtra();
            _vicPrefix = prefix;
            _vicTask = Library.GetVicAsync(prefix);
            return true;
        }

        // ------------------------------------------------------------------ ciclo per frame

        public override void OnUpdate()
        {
            if (Config == null || Library == null) return;   // avvio fallito: errore gia' loggato, niente da fare
            try { Tick(); }
            catch (Exception e)
            {
                if (Time.realtimeSinceStartup - _lastErrorLog > 5f)
                {
                    _lastErrorLog = Time.realtimeSinceStartup;
                    Error("errore nel ciclo: " + e);
                }
            }
        }

        private void Tick()
        {
            if (!EnsureHost()) return;

            if (!_preloaded)
            {
                _preloaded = true;
                foreach (var tuneName in new[] { "TitleCrazyDaveMainTheme", "ChooseYourSeeds" })
                {
                    string p = Library.FirstAvailable(Config.Candidates(tuneName));
                    if (p != null) Library.Preload(p);
                }
            }

            if (Time.realtimeSinceStartup >= _nextHostCheck)
            {
                _nextHostCheck = Time.realtimeSinceStartup + 1f;
                if (!_hostSource.isPlaying) _hostSource.Play();
            }

            // caricamento terminato -> parte la riproduzione
            if (_pending != null && _pending.IsCompleted)
            {
                var task = _pending;
                string prefix = _pendingPrefix;
                _pending = null;
                _pendingPrefix = null;

                if (task.IsFaulted || task.IsCanceled || task.Result == null)
                {
                    Error($"[{prefix}] caricamento fallito: {task.Exception?.GetBaseException().Message}. Ripristino la musica originale.");
                    var m = _muted;
                    _muted = null;
                    _activePrefix = null;
                    if (m != null) SetMute(m, false);
                }
                else
                {
                    StartSession(task.Result, prefix);
                }
            }

            UpdateVictory();
            if (Config.Debug && !_keysBroken) HandleDebugKeys();

            if (_muted == null) return;

            // il gioco ha cambiato/fermato la musica senza passare dagli hook: rilascia tutto
            var w = _svc?.m_currentMusicSource;
            if (w == null || w.Pointer != _muted.Pointer)
            {
                ReleaseCurrent(null);
                return;
            }

            SetMute(w, true);   // il gioco non tocca 'mute', ma lo riapplichiamo per sicurezza

            var sess = Mixer.Session;
            if (sess == null) return;

            UpdateTargets(sess, w, Math.Min(Time.unscaledDeltaTime, 0.1f));
            CheckAudioCallback();

            if (Config.Debug && Time.unscaledTime >= _nextDebug)
            {
                _nextDebug = Time.unscaledTime + 0.5f;
                Log($"[dbg] {_activePrefix} burst={_svc.mMusicBurstState}({_svc.mBurstStateCounter:F0}) "
                    + $"env horde={sess.HordeEnv:F2} h30={sess.Horde30Env:F2} base={sess.BaseEnv:F2} master={_master:F2} zombie={_lastZombies} var={(sess.BaseVar == null ? "-" : sess.VarAllowed ? "si" : "no")} "
                    + $"pos base={sess.Base.Pos / sess.Base.Pcm.SampleRate:F1}s giro={sess.Base.Wraps}"
                    + (sess.Horde != null ? $" horde={sess.Horde.Pos / sess.Horde.Pcm.SampleRate:F1}s" : "")
                    + $" pausa={Mixer.Paused} picco={Mixer.LastPeak:F2}");
            }
        }

        private void StartSession(TrackGroup g, string prefix)
        {
            var w0 = _svc?.m_currentMusicSource;
            bool crossfade = Config.GetCrossfadeOverride(prefix) ?? (w0 != null && w0.m_hihatsReplacesRegular);
            var session = new MixSession(g, crossfade);
            _fades = Config.GetFades(prefix);
            _horde30Threshold = Config.GetHorde30Threshold(prefix);
            _master = 1f;
            _sessionStart = Time.realtimeSinceStartup;

            if (g.Horde != null && !session.Crossfade && Math.Abs(g.Horde.Seconds - g.Base.Seconds) > 0.05)
                Warn($"[{prefix}] {g.Horde.Name} dura {g.Horde.Seconds:F2}s ma la base {g.Base.Seconds:F2}s: nel modo a livelli devono avere la stessa lunghezza. "
                     + $"Se la horde e' una traccia a se' aggiungi '{prefix}.Crossfade = true' in [fade] di config.ini");

            if (g.HordeAlt != null && (g.Alt == null || g.Horde == null || session.Crossfade))
                Warn($"[{prefix}] horde_alt ignorata: serve avere anche _alt e _horde (e non essere in modo crossfade)");

            if (w0 != null) UpdateTargets(session, w0, 0f);
            Mixer.Start(session);
            _callbacksAtStart = Mixer.Callbacks;
            _callbackWarned = false;

            string mode = g.Horde == null ? "solo base"
                : session.Crossfade ? $"crossfade, la horde riparte da capo (horde +{_fades.HordeIn:0.#}s/-{_fades.HordeOut:0.#}s, base -{_fades.BaseOut:0.#}s/+{_fades.BaseIn:0.#}s)"
                : $"a livelli (horde +{_fades.HordeIn:0.#}s/-{_fades.HordeOut:0.#}s)";
            var extra = new List<string>();
            if (g.Alt != null) extra.Add("base/alt alternati a ogni giro" + (session.HordeAlt != null ? " (con horde_alt)" : ""));
            if (g.BaseVar != null) extra.Add("base_var: primo giro e con l'orda, mai nel 1-1");
            if (g.Horde30 != null) extra.Add($"horde_30: con piu' di {_horde30Threshold} zombie (+{_fades.Horde30In:0.#}s/-{_fades.Horde30Out:0.#}s)");
            Log($"[{prefix}] in riproduzione: {mode}{(extra.Count > 0 ? "; " + string.Join("; ", extra) : "")}");
        }

        /// <summary>
        /// Volume generale dal gioco (slider musica x fade di fine livello, gia' calcolato dal gioco per le sue sorgenti)
        /// + inviluppi orda calcolati qui con le durate configurate. Quando arriva/finisce l'orda lo decide la macchina a stati del gioco.
        /// </summary>
        private void UpdateTargets(MixSession s, AudioSourceWrapper w, float dt)
        {
            _master = ComputeMaster(w);
            bool paused = w.m_isPaused;
            if (paused) dt = 0f;   // in pausa anche i fade si fermano

            var burst = _svc.mMusicBurstState;
            bool want = burst == MusicBurstState.Starting || burst == MusicBurstState.On;

            // orda: un solo inviluppo per horde, horde_alt e base_var (partono e finiscono insieme)
            float hordeRate = want ? 1f / _fades.HordeIn : -1f / _fades.HordeOut;
            s.HordeEnv = Clamp01(s.HordeEnv + hordeRate * dt);
            if (s.Horde != null)
            {
                s.Horde.Target = _master * s.HordeEnv;
                // crossfade: la horde e' una traccia a se' e riparte da capo ogni volta che rientra da silenzio completo
                if (s.Horde.Independent) s.Horde.Hold = !want && s.HordeEnv <= 0f;
            }
            if (s.HordeAlt != null) s.HordeAlt.Target = _master * s.HordeEnv;

            // horde_30: si aggiunge solo con l'orda in corso E piu' zombie della soglia a schermo
            if (s.Horde30 != null)
            {
                _lastZombies = ZombieCount();
                bool want30 = want && _lastZombies > _horde30Threshold;
                float r30 = want30 ? 1f / _fades.Horde30In : -1f / _fades.Horde30Out;
                s.Horde30Env = Clamp01(s.Horde30Env + r30 * dt);
                s.Horde30.Target = _master * s.Horde30Env;
            }

            // base_var: primo giro del loop (tranne nel livello 1-1) + insieme all'orda
            if (s.BaseVar != null)
            {
                if (!s.VarDecided) DecideVar(s);
                s.BaseVar.Target = s.VarAllowed ? _master * s.HordeEnv : 0f;
                s.BaseVar.FirstVol = s.VarAllowed ? _master : 0f;
            }

            if (s.Crossfade)
            {
                float rate = want ? -1f / _fades.BaseOut : 1f / _fades.BaseIn;
                s.BaseEnv = Clamp01(s.BaseEnv + rate * dt);
            }
            else
            {
                s.BaseEnv = 1f;
            }
            s.Base.Target = _master * s.BaseEnv;
            if (s.Alt != null) s.Alt.Target = _master * s.BaseEnv;

            Mixer.Paused = paused;
        }

        private int ZombieCount()
        {
            try
            {
                var b = _svc?.m_app?.Board;
                return b != null ? b.CountZombiesOnScreen() : 0;
            }
            catch { return 0; }
        }

        /// <summary>_base_var non suona nel livello 1-1 dell'avventura (Board.mLevel == 1); in tutto il resto si'.</summary>
        private void DecideVar(MixSession s)
        {
            try
            {
                var app = _svc?.m_app;
                var b = app?.Board;
                if (app != null && b != null)
                {
                    s.VarAllowed = !(app.IsAdventureMode() && b.mLevel == 1);
                    s.VarDecided = true;
                    Log($"[{s.Prefix}] base_var: {(s.VarAllowed ? "attiva (primo giro e con l'orda)" : "NON suona in questo livello (1-1)")}");
                    return;
                }
            }
            catch (Exception e)
            {
                Warn("riconoscimento del livello per base_var fallito: " + e.Message);
            }

            // niente livello (o non riconoscibile): dopo 3 s la variazione resta attiva
            if (Time.realtimeSinceStartup - _sessionStart > 3f)
            {
                s.VarAllowed = true;
                s.VarDecided = true;
                Log($"[{s.Prefix}] base_var: livello non riconosciuto, resta attiva");
            }
        }

        /// <summary>
        /// Il gioco assegna a ogni sorgente: volume = master x inviluppo (main/burst/drums). Ricavo il master dividendo
        /// il volume reale di una sorgente per il suo inviluppo (scelgo quella con l'inviluppo piu' alto).
        /// </summary>
        private float ComputeMaster(AudioSourceWrapper w)
        {
            bool replaces = w.m_hihatsReplacesRegular;
            float hv = Math.Max(Vol(w.m_hihatsAudioSource, 0f), Vol(w.m_hihatsIntroAudioSource, 0f));
            float dv = Math.Max(Vol(w.m_drumsAudioSource, 0f), Vol(w.m_drumsIntroAudioSource, 0f));

            float bestEnv = 0.05f;
            float master = -1f;
            void Try(float env, float vol)
            {
                if (env > bestEnv) { bestEnv = env; master = vol / env; }
            }
            Try(w.m_mainVolume, Vol(w.m_audioSource, 1f));
            // crossfade: la traccia alternativa sta nella sorgente hihats oppure drums a seconda dell'intro
            Try(w.m_burstVolume, replaces ? Math.Max(hv, dv) : hv);
            if (!replaces) Try(w.m_drumsVolume, dv);

            return master >= 0f ? master : _master;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        private void CheckAudioCallback()
        {
            if (_callbackConfirmed) return;
            if (Mixer.Callbacks > _callbacksAtStart)
            {
                _callbackConfirmed = true;
                Log("thread audio OK: il mixer sta ricevendo i blocchi audio.");
            }
            else if (!_callbackWarned && Time.realtimeSinceStartup - _sessionStart > 3f)
            {
                _callbackWarned = true;
                Error("il mixer NON riceve blocchi audio (OnAudioFilterRead non viene chiamato): non si sentira' nulla. Segnalalo.");
            }
        }

        // ------------------------------------------------------------------ vittoria

        private void UpdateVictory()
        {
            if (_vicTask != null && _vicTask.IsCompleted)
            {
                var t = _vicTask;
                string prefix = _vicPrefix;
                _vicTask = null;
                if (t.IsFaulted || t.IsCanceled || t.Result == null)
                {
                    Error($"[{prefix}] musica di vittoria non caricabile: {t.Exception?.GetBaseException().Message}");
                }
                else
                {
                    var s = new MixSession(t.Result, false, true);
                    s.Base.Target = MusicVolume();
                    Mixer.StartExtra(s);
                    Log($"[{prefix}] musica di vittoria in riproduzione ({t.Result.Base.Seconds:F1}s)");
                }
            }

            var x = Mixer.Extra;
            if (x != null)
            {
                x.Base.Target = MusicVolume();
                if (x.Base.Pos >= x.Base.Pcm.Frames)
                {
                    Mixer.StopExtra();
                    Log("musica di vittoria terminata");
                }
            }
        }

        /// <summary>Volume musica impostato nelle opzioni del gioco (0..1).</summary>
        private float MusicVolume()
        {
            try
            {
                var s = _svc?.m_settingsService;
                if (s != null) return Clamp01(s.MusicVolume);
            }
            catch { }
            return _master;
        }

        // ------------------------------------------------------------------ tasti di debug (solo con Debug = true)

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private bool _prevH, _prevS, _prevD, _prevV, _prevL;
        private bool _lastFocused, _lastCtrl;

        /// <summary>
        /// Ctrl+H orda, Ctrl+S 10 zombie, Ctrl+D rimuove gli zombie, Ctrl+V fa cadere la pala (fine livello),
        /// Ctrl+L porta la musica a 4 secondi dalla fine del loop.
        /// Usa GetAsyncKeyState (indipendente dall'Input System del gioco) e agisce solo se la finestra del gioco e' attiva.
        /// </summary>
        private void HandleDebugKeys()
        {
            bool h, s, d, v, l;
            try
            {
                bool focused = Application.isFocused;
                if (focused != _lastFocused) { _lastFocused = focused; Log($"[debug] finestra del gioco {(focused ? "attiva (tasti di prova abilitati)" : "non attiva (tasti di prova disabilitati)")}"); }
                if (!focused)
                {
                    _prevH = _prevS = _prevD = _prevV = _prevL = false;
                    return;
                }
                bool ctrl = KeyDown(0x11);
                if (ctrl != _lastCtrl) { _lastCtrl = ctrl; Log($"[debug] Ctrl {(ctrl ? "premuto" : "rilasciato")}"); }
                h = ctrl && KeyDown(0x48);   // H
                s = ctrl && KeyDown(0x53);   // S
                d = ctrl && KeyDown(0x44);   // D
                v = ctrl && KeyDown(0x56);   // V
                l = ctrl && KeyDown(0x4C);   // L
            }
            catch (Exception e)
            {
                _keysBroken = true;
                Error("tasti di debug non disponibili: " + e.Message);
                return;
            }

            // scatta una sola volta per pressione (fronte di salita)
            bool fireH = h && !_prevH, fireS = s && !_prevS, fireD = d && !_prevD, fireV = v && !_prevV, fireL = l && !_prevL;
            _prevH = h; _prevS = s; _prevD = d; _prevV = v; _prevL = l;

            try
            {
                if (fireH) DebugHorde();
                else if (fireS) DebugSpawn(10, "Ctrl+S");
                else if (fireD) DebugClear();
                else if (fireV) DebugShovel();
                else if (fireL) DebugSeekToLoop();
            }
            catch (Exception e)
            {
                Error("[debug] azione fallita: " + e);
            }
        }

        private void DebugSeekToLoop()
        {
            var sess = Mixer.Session;
            if (sess == null)
            {
                Warn("[debug] Ctrl+L: nessuna musica sostituita in riproduzione");
                return;
            }
            const float seconds = 4f;
            sess.SeekBeforeLoopEnd = seconds;
            double loopSec = (double)sess.Base.LoopEnd / sess.Base.Pcm.SampleRate;
            Log($"[debug] Ctrl+L: [{sess.Prefix}] musica portata a {seconds:0.#} s dalla fine del loop (fine loop a {loopSec:F2} s)");
        }

        private Board GetBoard(string what)
        {
            Board b = null;
            try { b = _svc?.m_app?.Board; } catch { }
            if (b == null) Warn($"[debug] {what}: nessun livello in corso");
            return b;
        }

        private void DebugSpawn(int count, string label)
        {
            var b = GetBoard(label);
            if (b == null) return;
            int ok = 0;
            for (int i = 0; i < count; i++)
                if (b.AddZombie(ZombieType.Normal, -1, false) != null) ok++;
            Log($"[debug] {label}: spawnati {ok}/{count} zombie (in campo ora: {b.CountZombiesOnScreen()})");
        }

        private void DebugHorde()
        {
            var b = GetBoard("Ctrl+H");
            if (b == null) return;
            try { b.ShowHugeWaveAdvice(); } catch (Exception e) { Warn("[debug] messaggio orda non mostrato: " + e.Message); }

            // una bandiera e poi un'ondata mista, come un flag wave (25 zombie)
            var types = new[] { ZombieType.Normal, ZombieType.TrafficCone, ZombieType.Pail };
            int ok = 0;
            if (b.AddZombie(ZombieType.Flag, -1, false) != null) ok++;
            for (int i = 0; i < 24; i++)
                if (b.AddZombie(types[i % types.Length], -1, false) != null) ok++;
            Log($"[debug] Ctrl+H: orda! spawnati {ok} zombie (in campo ora: {b.CountZombiesOnScreen()})");
        }

        private void DebugClear()
        {
            var b = GetBoard("Ctrl+D");
            if (b == null) return;
            int before = b.CountZombiesOnScreen();
            b.RemoveAllZombies();
            Log($"[debug] Ctrl+D: zombie rimossi (erano {before}, ora {b.CountZombiesOnScreen()})");
        }

        private void DebugShovel()
        {
            var b = GetBoard("Ctrl+V");
            if (b == null) return;
            // come quando l'ultimo zombie lascia il premio del livello: da qui raccogliendolo parte la fine livello
            float x = b.GridToPixelX(5, 2);
            float y = b.GridToPixelY(5, 2);
            b.mLevelAwardSpawned = true;
            var coin = b.AddCoin(x, y, CoinType.Shovel, CoinMotion.Coin);
            Log($"[debug] Ctrl+V: pala del premio creata in ({x:F0},{y:F0}){(coin == null ? " (AddCoin ha restituito null)" : "")}: raccoglila per finire il livello");
        }

        // ------------------------------------------------------------------ utilita'

        private void ReleaseCurrent(AudioSourceWrapper keepMuted)
        {
            var old = _muted;
            Mixer.Stop();
            _pending = null;
            _pendingPrefix = null;
            _activePrefix = null;
            if (old != null && (keepMuted == null || old.Pointer != keepMuted.Pointer))
                SetMute(old, false);
            _muted = null;
        }

        private bool EnsureHost()
        {
            if (_hostSource != null) return true;
            if (_hostFailed) return false;
            try
            {
                int rate = AudioSettings.outputSampleRate;
                if (rate <= 0) rate = 48000;
                Mixer.OutRate = rate;

                _host = new GameObject("PvZDynamicMusic");
                UnityEngine.Object.DontDestroyOnLoad(_host);
                _hostSource = _host.AddComponent<AudioSource>();
                _hostSource.clip = AudioClip.Create("PvZDynamicMusic_silence", 2048, 2, rate, false);   // silenzio: serve solo a far girare il filtro
                _hostSource.loop = true;
                _hostSource.playOnAwake = false;
                _hostSource.spatialBlend = 0f;
                _hostSource.volume = 1f;
                _hostSource.priority = 0;
                _host.AddComponent<DynamicMusicBehaviour>();
                _hostSource.Play();
                Log($"host audio creato ({rate} Hz)");
                return true;
            }
            catch (Exception e)
            {
                _hostFailed = true;
                Error("impossibile creare l'host audio: " + e);
                return false;
            }
        }

        private static float Vol(AudioSource s, float fallback) => s != null ? s.volume : fallback;
        private static bool Has(AudioSource s) => s != null;

        private static void SetMute(AudioSourceWrapper w, bool mute)
        {
            Mute(w.m_audioSource, mute);
            Mute(w.m_introAudioSource, mute);
            Mute(w.m_hihatsAudioSource, mute);
            Mute(w.m_hihatsIntroAudioSource, mute);
            Mute(w.m_drumsAudioSource, mute);
            Mute(w.m_drumsIntroAudioSource, mute);
        }

        private static void Mute(AudioSource s, bool mute)
        {
            if (s != null && s.mute != mute) s.mute = mute;
        }

        internal void Log(string m) => LoggerInstance.Msg(m);
        internal void Warn(string m) => LoggerInstance.Warning(m);
        internal void Error(string m) => LoggerInstance.Error(m);
    }

    /// <summary>Hook Harmony sui metodi del gioco.</summary>
    internal static class Hooks
    {
        // PlayMusic, MakeSureMusicIsPlaying e StartGameMusic passano tutti da PlayFromOffset (dopo aver impostato il tune).
        public static void PlayFromOffsetPost(AudioService __instance)
        {
            try { DynamicMusicMod.Instance?.OnGameMusicStarted(__instance); }
            catch (Exception e) { DynamicMusicMod.Instance?.Error("hook PlayFromOffset: " + e); }
        }

        // Vittoria: prefix, restituisce false per NON far suonare il jingle originale quando lo sostituiamo con xx_vic.wav.
        public static bool PlayFoleyPre(AudioService __instance, FoleyType theFoleyType)
        {
            try { return !(DynamicMusicMod.Instance?.OnFoley(__instance, theFoleyType) ?? false); }
            catch (Exception e)
            {
                DynamicMusicMod.Instance?.Error("hook PlayFoley: " + e);
                return true;
            }
        }

        public static void StopAllMusicPost(AudioService __instance)
        {
            try { DynamicMusicMod.Instance?.OnGameMusicStopped(__instance); }
            catch (Exception e) { DynamicMusicMod.Instance?.Error("hook StopAllMusic: " + e); }
        }
    }

    /// <summary>
    /// Componente iniettato in Il2Cpp: Unity chiama OnAudioFilterRead sul thread audio; qui il buffer viene riempito dal mixer.
    /// </summary>
    public class DynamicMusicBehaviour : MonoBehaviour
    {
        public DynamicMusicBehaviour(IntPtr ptr) : base(ptr) { }

        public void OnAudioFilterRead(Il2CppStructArray<float> data, int channels)
        {
            try { DynamicMusicMod.Mixer.Process(data, channels); }
            catch { /* mai far uscire eccezioni nel thread audio */ }
        }
    }
}
