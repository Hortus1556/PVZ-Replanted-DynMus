# PvZ DynMus

Musica dinamica e completamente configurabile per **Plants vs. Zombies: Replanted**, come mod per [MelonLoader](https://github.com/LavaGang/MelonLoader).

DynMus silenzia la musica del gioco e suona al suo posto **i tuoi file `.wav`**, mantenendo il comportamento dinamico originale:
quando ci sono abbastanza zombie a schermo entra in dissolvenza un livello "horde", e ne esce quando il prato si calma.
Durata delle dissolvenze, punti di loop, giri alternati, musiche di vittoria per ogni livello e altro sono configurabili.

> 🇬🇧 English documentation: [README.md](README.md)

**Questo repository non contiene musica né file del gioco.** I file audio li porti tu; la mod suona solo quello che metti nella sua
cartella, quindi usa solo audio di cui hai i diritti. È un progetto amatoriale non ufficiale, non affiliato né approvato da PopCap Games o
Electronic Arts; *Plants vs. Zombies* e i nomi collegati sono marchi dei rispettivi proprietari.

## Requisiti

- Windows
- Plants vs. Zombies: Replanted (Steam). Sviluppata e provata sulla versione `1.5.1469_Steam`
- [MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.3 (Il2Cpp), avviato almeno una volta così genera `MelonLoader\Il2CppAssemblies`
- .NET SDK 6 o superiore per compilare (il progetto è `net6.0`; provato con SDK 8)

La mod dipende dai nomi interni delle classi del gioco generati da MelonLoader: un aggiornamento che li rinomina può romperla.

## Installazione

Scarica l'ultimo `PvZ-Replanted-DynMus-<versione>.zip` dalla pagina [Releases](https://github.com/Hortus1556/PVZ-Replanted-DynMus/releases)
ed estrailo nella cartella del gioco (quella con `Replanted.exe`). Contiene la mod, `Mods\PvZDynamicMusic.dll`, e una cartella **vuota** per ogni
musica dentro `Mods\DynMus\` (`cd`, `cys`, `gw`, `mg`, `wg`, `rm`, `gtr`, `lb`, `cb`, `ub`, `bm`, `zg`); non c'è nessun audio.
Preferisci compilarla tu? Vedi [Compilazione](#compilazione).

1. Metti i tuoi `.wav` nelle cartelle corrispondenti di `<cartella del gioco>\Mods\DynMus\`. Le sottocartelle vanno bene, in qualsiasi organizzazione:
   i file si trovano per nome in tutta `DynMus` (se lo stesso nome compare due volte vince quello più vicino alla radice).
2. Avvia il gioco. Al primo avvio la mod scrive `Mods\DynMus\config.ini` ed elenca nella console di MelonLoader i file trovati.

## Nomi dei file

Nomi in minuscolo, senza spazi. Per ogni musica serve solo `<nome>_base.wav` (oppure `<nome>.wav`); tutto il resto è facoltativo.
Se per una musica non c'è nessun file, per quella resta la musica **originale** del gioco.

| File | Usato per |
|---|---|
| `cd.wav` | Menu principale / Crazy Dave (anche ripiego per lo Zen Garden) |
| `cys.wav` | Choose Your Seeds (scelta semi) |
| `gw_base.wav`, `gw_horde.wav` | Grasswalk (giorno) |
| `mg_base.wav`, `mg_horde.wav` | Moongrains (notte): la horde è una traccia *diversa*, con crossfade |
| `wg_base.wav`, `wg_horde.wav` | Watery Graves (piscina) |
| `rm_base.wav`, `rm_horde.wav` | Rigor Mormist (nebbia) |
| `gtr_base.wav`, `gtr_horde.wav` | Graze the Roof (tetto) |
| `lb.wav` | Loonboon (mini-giochi come Wall-nut Bowling, Whack a Zombie) |
| `cb.wav` | Cerebrawl (Vasebreaker, I, Zombie) |
| `ub.wav` | Ultimate Battle (livelli 1-10, 2-10, 3-10 e Column Like You See 'Em) |
| `bm.wav` | Brainiac Maniac (boss finale) |
| `zg.wav` | Zen Garden e Tree of Wisdom (ripiego: `cd.wav`) |
| `credits.wav` | Crediti |

Gli altri livelli e mini-giochi (Zombotany, survival, …) suonano la musica dello sfondo su cui si trovano.

### Livelli extra facoltativi

`_alt`, `_horde_alt`, `_base_var`, `_horde_30` (e una `_horde` a livelli, che è il caso normale) restano in sincrono con `<nome>_base.wav`: usano
i suoi loop point, quindi devono avere la **stessa durata** della base (e dovrebbero avere gli stessi canali). Una differenza non viene rifiutata:
viene solo segnalata come avviso nel log quando la musica si carica o parte. `_vic`, e la `_horde` in modalità crossfade, sono indipendenti e
possono avere qualsiasi durata.

| File | Comportamento |
|---|---|
| `<nome>_alt.wav` | Suona in sincrono sotto la base ma si sente a giri alterni: base, poi alt, poi base, … `_horde` si sovrappone a entrambe. |
| `<nome>_horde_alt.wav` | La horde da usare mentre si sente `_alt` (con `_base` suona la `_horde` normale). Richiede `_alt` e `_horde`; viene ignorata (con un avviso nel log) nel modo crossfade, che è il caso di Moongrains (`mg`) o di una musica con `<prefisso>.Crossfade = true`. |
| `<nome>_base_var.wav` | Livello extra mixato **sopra** la base (non al suo posto): si sente solo nel **primo** giro del loop, e mai nel livello 1-1 dell'avventura. Quando parte una horde rientra insieme a `_horde`, con le stesse dissolvenze. Masterizzalo come una traccia di sovrapposizione. |
| `<nome>_horde_30.wav` | Si aggiunge sopra `_horde` solo con una horde in corso **e** più di 30 zombie a schermo (soglia configurabile). |
| `<nome>_vic.wav` | Musica di vittoria per quella musica. Suona una volta sola, senza loop, quando finisci il livello raccogliendo il premio, al posto del jingle del gioco. Usa il volume musica delle opzioni. Si usa solo se anche quella musica è sostituita (esiste `<nome>_base.wav` o `<nome>.wav`); altrimenti, o se il file manca, suona il jingle del gioco. |

## Come si comporta

- **Quando parte una horde.** La mod non lo indovina: il gioco continua a far girare la sua macchina a stati della musica (le sue
  sorgenti audio sono silenziate, non fermate) e la mod ne legge lo stato. Nel gioco originale il burst parte con 10 o più zombie a
  schermo e finisce quando ne restano meno di 4, dopo una durata minima. I livelli horde sono pensati per le cinque musiche di
  livello che nel gioco hanno una versione dinamica (gw, mg, wg, rm, gtr).
- **Dissolvenze.** Le curve sono della mod e si impostano in **secondi** in `config.ini`. Valori predefiniti: horde entra in 8 s / esce
  in 12 s, base (solo crossfade) esce in 8 s / rientra in 12 s.
- **A livelli o crossfade.** Di default la horde si sovrappone alla base. Per le musiche che il gioco segna come "sostituisci"
  (Moongrains) la horde è una traccia a parte: la base scende, la horde sale **dal proprio inizio** ogni volta che rientra dal
  silenzio completo, e la base continua ad andare. Si può forzare con `<prefisso>.Crossfade = true|false` in `[fade]`.
- **Loop.** Riproduzione sample-accurate, con interpolazione cubica se la frequenza di un file è diversa da quella d'uscita. Vedi `[loop]`.
- **Pausa.** La pausa del gioco mette in pausa la musica (e le dissolvenze); le musiche di vittoria continuano.

## Configurazione (`Mods\DynMus\config.ini`)

Tieni la riga `Versione = 3`: se è inferiore alla versione corrente della mod, `config.ini` viene sostituito con quello nuovo e il tuo
vecchio file resta accanto come `config.ini.vN.bak` (N = la vecchia versione). Dopo ogni modifica riavvia il gioco. I commenti devono stare su una
riga a parte (che inizia con `#` o `;`), mai dopo un valore.

**`[generale]`**

| Chiave | Significato |
|---|---|
| `Versione` | Versione del formato. Non modificare. |
| `MusicaNonMappata` | `original` (predefinito) oppure `silence`. Vale per le musiche del gioco **non elencate in `[mappa]`**: restano originali oppure tacciono. Una musica elencata in `[mappa]` (anche con valore vuoto) mantiene sempre quella originale se mancano i suoi file. Con la `[mappa]` predefinita ogni musica è elencata, quindi conta solo se cancelli delle righe. |
| `Debug` | `true` attiva i log dettagliati e i tasti di prova qui sotto. |

**`[mappa]`** associa il nome di una musica del gioco a un prefisso di file. Si possono dare più prefissi separati da virgola: si usa il
primo che ha i file. Lascia il valore vuoto per non sostituire quella musica. Predefiniti:

```
TitleCrazyDaveMainTheme = cd         ChooseYourSeeds = cys
DayGrasswalk = gw                    NightMoongrains = mg
PoolWaterygraves = wg                FogRigormormist = rm
RoofGrazetheroof = gtr               PuzzleCerebrawl = cb
MinigameLoonboon = lb                Conveyer = ub
FinalBossBrainiacManiac = bm         ZenGarden = zg, cd
CreditsZombiesOnYourLawn = credits   DayGrasswalkCredits = credits
```

**`[fade]`** (secondi, il decimale può essere punto o virgola; i valori sotto 0,05 vengono portati a 0,05, quindi `0` non è un taglio netto). Ogni chiave si può limitare a una sola musica con `prefisso.` davanti, ad esempio `mg.HordeFadeIn = 6`.

| Chiave | Predefinito | Significato |
|---|---|---|
| `HordeFadeIn` / `HordeFadeOut` | 8 / 12 | Horde (e `_horde_alt`, `_base_var`) entra / esce |
| `BaseFadeOut` / `BaseFadeIn` | 8 / 12 | Base esce / rientra (solo musiche con crossfade) |
| `Horde30Zombie` | 30 | `_horde_30` si aggiunge con **più** zombie di questo numero |
| `Horde30FadeIn` / `Horde30FadeOut` | come la horde | Dissolvenze di `_horde_30` |
| `<prefisso>.Crossfade` | lo decide il gioco | Forza o toglie il crossfade per una musica |

**`[loop]`** una riga per musica: `prefisso = inizio, fine`, in **frame di campioni** (campioni per canale, come li mostrano gli editor
audio tipo OpenMPT), senza separatori delle migliaia. `fine = 0` vuol dire fino alla fine del file. L'audio prima di `inizio` (un'intro)
suona una volta, poi il loop si ripete da `inizio` a `fine`. `_base`, `_alt`, `_horde`, … usano i loop point della base. Nel crossfade la
horde, che è una traccia a parte, ha il suo loop con la chiave `<prefisso>_horde` (ad esempio `mg_horde = 0, 0`).

Audio supportato: WAV, PCM 8/16/24/32 bit o float 32/64 bit, mono o stereo, qualsiasi frequenza.

## Tasti di prova (debug)

Con `Debug = true`, con la finestra del gioco attiva (solo Windows). `Ctrl+H/S/D/V` funzionano solo dentro un livello, `Ctrl+L` richiede una tua traccia in riproduzione:

| Tasti | Azione |
|---|---|
| `Ctrl+H` | Fa arrivare una horde: messaggio di huge wave più 25 zombie |
| `Ctrl+S` | Spawna 10 zombie |
| `Ctrl+D` | Rimuove tutti gli zombie |
| `Ctrl+V` | Fa cadere la pala del premio: raccoglila per finire il livello e sentire `_vic` |
| `Ctrl+L` | Porta ogni livello a 4 secondi dalla fine del loop, per controllare il punto di loop |

## Compilazione

Clona il repository dentro la cartella del gioco (con qualsiasi nome, ad esempio `<gioco>\PvZ-Replanted-DynMus`: `GameDir` vale allora di default la cartella del gioco) oppure altrove, e indica la cartella del gioco con `GameDir`:

```
dotnet build PvZMusic.csproj -c Release -p:GameDir="D:\Giochi\Plants vs. Zombies - Replanted"
```

La cartella del gioco deve contenere `MelonLoader\Il2CppAssemblies` (avvia il gioco una volta con MelonLoader installato). Senza backslash finale in `GameDir`.
La DLL viene scritta in `bin\Release\net6.0\PvZDynamicMusic.dll` e copiata in `<gioco>\Mods` se la cartella esiste (aggiungi `-p:CopyToMods=false` per non copiarla).

## Test

Test offline di mixer, decoder WAV, parser del config e libreria dei file (non serve il gioco avviato, solo la cartella del gioco per
una DLL di MelonLoader; SDK .NET 8):

```
dotnet run --project tests/DynMus.Tests -c Release
dotnet run --project tests/DynMus.Tests -c Release -- check "D:\Giochi\Plants vs. Zombies - Replanted\Mods\DynMus"
```

La seconda forma (`check`) carica in sola lettura i `.wav` di quella cartella, usando il `config.ini` della stessa cartella (solo i suoi loop point; `[mappa]` viene ignorato, controlla sempre i prefissi standard), e per
ogni musica standard stampa i livelli trovati. Avvisa se `_alt`, `_base_var`, `_horde_alt` o `_horde_30` hanno durata o canali diversi dalla base, e
mostra se `_horde` ha la stessa durata della base (obbligatoria, tranne nel crossfade). Non controlla i `_vic`.

Se il repository non è dentro la cartella del gioco, metti `-p:GameDir="..."` **prima** del `--` (tutto ciò che segue `--` va al programma, non a MSBuild):

```
dotnet run --project tests/DynMus.Tests -c Release -p:GameDir="D:\Giochi\Plants vs. Zombies - Replanted" -- check "D:\Giochi\Plants vs. Zombies - Replanted\Mods\DynMus"
```

## Come funziona

- Hook Harmony su `AudioService.PlayFromOffset` (è partita una musica), `StopAllMusic` e `PlayFoley` (i jingle di vittoria).
- Le sorgenti audio del gioco per la musica corrente sono silenziate, non fermate: la sua logica del burst continua a girare e viene letta a ogni frame.
- Un piccolo mixer gira in `OnAudioFilterRead` di un componente iniettato su una `AudioSource` silenziosa; le tracce vengono decodificate in float in un thread in background.
  I livelli agganciati alla base (tutti tranne una `_horde` in crossfade, che ha un playhead suo) avanzano in lockstep (anche da muti),
  quindi restano in sincrono attraverso loop, salti e pause.

## Limiti noti

- **Memoria.** Le tracce sono decodificate in float a 32 bit in RAM: circa 23 MB per minuto di audio stereo a 48 kHz per ogni file, e
  fino a tre musiche restano in cache. Una musica con quattro livelli e tracce lunghe può usare diverse centinaia di MB; le musiche di vittoria hanno una cache a parte (fino a tre in più).
- Solo WAV. Solo Windows (i tasti di prova usano `user32`).
- I commenti del config e i messaggi di log sono in italiano.
- Un aggiornamento del gioco può rompere gli hook. All'avvio la mod scrive un errore se non riesce a installare l'hook `PlayFromOffset` o
  `StopAllMusic` (la mod non può funzionare), o un avviso se non riesce con `PlayFoley` (si disattivano solo i `_vic`).

## Licenza

[MIT](LICENSE), solo per il codice sorgente di questa mod. Il gioco e la sua musica appartengono ai rispettivi proprietari.
