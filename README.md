# FreeFlow

![FreeFlow](src/FreeFlow/Assets/banner.png)

Free, local, private voice dictation for Windows — a full [Wispr Flow](https://wisprflow.ai/) replacement that costs **nothing, forever**. No account, no subscription, no word limits, and your voice never leaves this machine.

## How it works

1. Run `FreeFlow.exe` — the app window opens and a mic icon lands in the tray.
2. Put your cursor in any text field, in any app.
3. **Hold Right Ctrl** and speak — **words appear while you're still talking.**
4. Release — FreeFlow re-checks the whole utterance with its accurate model and polishes the text in place (punctuation, capitalization, filler removal).
5. **Quick-tap** Right Ctrl to latch hands-free; tap again (or click the pill) to stop.

While you talk, the floating pill at the bottom of the screen shows a live equalizer and the words streaming in.

### Spoken commands

| Say | Get |
|---|---|
| "new line" / "new paragraph" | line break / blank line |
| "scratch that" (mid-sentence) | wipes what you said before it |
| "scratch that" (alone) | deletes the last dictation |

### The app

- **Home** — status, hotkey (click to change — press any key to rebind), lifetime stats (words, words today, speaking speed), and a test area
- **Dictation** — live typing toggle, final-pass mode, tone, auto-edits
- **Dictionary** — "hey gen" → "HeyGen"; teach it names and jargon
- **Shortcuts** — say "my email" alone, get the full address typed
- **App Profiles** — tone matching per app: casual in Discord, professional in Outlook, verbatim in terminals
- **History** — every dictation, locally; **Model** — model manager; **Settings** — mic, hotkeys, autostart, AI endpoint

Settings apply instantly — no save button.

## Engine architecture (all local, all CPU)

| Stage | Model | Job |
|---|---|---|
| Live words | Streaming Zipformer transducer (int8, 71 MB) | partial transcripts ~every 100 ms while you speak |
| Punctuation | CT-Transformer online punct (int8, 7 MB) | caps + punctuation for live text |
| Final polish | **NVIDIA Parakeet TDT 0.6B v2** (int8, 652 MB) | re-transcribes the utterance on release; beats Whisper large-v3 on English |
| Multilingual | Whisper small/base (optional) | 100+ languages |

Measured on the i7-4790: streaming partials arrive while audio is still being fed (first words < 1 s in), Parakeet polishes 3.4 s of speech in ~500 ms.

Command mode (select text, hold command key, say "make this more concise") works with any OpenAI-compatible endpoint — install [Ollama](https://ollama.com) for free local AI.

## Build & verify

```powershell
cd src\FreeFlow
dotnet build -c Release          # exe → bin\Release\net8.0-windows\FreeFlow.exe

FreeFlow.exe --selftest          # 33 checks: formatter, resamplers, TTS→STT, streaming partials, punctuation
FreeFlow.exe --injecttest        # live text-injection proof
FreeFlow.exe --pillpreview       # animated demo of the overlay pill
FreeFlow.exe --transcribe x.wav  # transcribe any wav
FreeFlow.exe --makeassets <dir>  # regenerate logo/icon/banner
```

Models live in `%APPDATA%\FreeFlow\models`, config/history/stats in `%APPDATA%\FreeFlow`.
