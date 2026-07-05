# FreeFlow

Free, local, private voice dictation for Windows — a full [Wispr Flow](https://wisprflow.ai/) replacement that costs **nothing, forever**. No account, no subscription, no word limits, and your voice never leaves this machine.

## How to use it

1. Run `FreeFlow.exe` — a mic icon appears in the system tray.
2. Put your cursor in any text field, in any app.
3. **Hold Right Ctrl** and speak. Release — your words appear, cleanly punctuated.
4. **Quick-tap Right Ctrl** to latch hands-free recording; tap again to stop.

That's the whole workflow. Everything else is seasoning.

### Spoken commands (while dictating)

| Say | Get |
|---|---|
| "new line" | line break |
| "new paragraph" | blank line |
| "scratch that" (mid-sentence) | wipes what you said before it |
| "scratch that" (alone) | deletes the last dictation entirely |

### Features (Settings via tray icon)

- **Auto-edits** — filler words (um, uh…) removed, punctuation and capitalization come from the model itself
- **Custom dictionary** — "hey gen" → "HeyGen"; teach it names, jargon, brands
- **Voice shortcuts** — say "my email" alone and it types the full address
- **Tone matching per app** — casual in Discord/Slack (no trailing period), professional in Outlook, verbatim in terminals
- **Command mode** — select text, hold the command key, say "make this more concise" (needs a local LLM — install [Ollama](https://ollama.com) and it works with defaults)
- **Whisper mode** — crank Input gain in Settings → General to dictate quietly
- **Multilingual** — switch to a Whisper model in Settings → Model for 100+ languages
- **History** — every dictation logged locally (can be disabled/cleared)
- **Smart spacing** — consecutive dictations into the same app get joined with a space

## Tech

| Piece | What |
|---|---|
| Speech recognition | [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) running **NVIDIA Parakeet TDT 0.6B v2** (int8, CPU) |
| Accuracy | Beats Whisper large-v3 on English benchmarks; punctuation + caps built in |
| Speed | ~0.13× real-time on an i7-4790 (3.4s of speech → 431ms) |
| App | C# / .NET 8 WinForms tray app, ~3,000 lines |
| Audio | NAudio, 16 kHz mono, warm-mic ring buffer so your first syllable is never clipped |
| Injection | Clipboard paste (with clipboard restore) or per-app character typing |

Models live in `%APPDATA%\FreeFlow\models`, config in `%APPDATA%\FreeFlow\config.json`.

## Build from source

```powershell
cd src\FreeFlow
dotnet build -c Release
# exe lands in bin\Release\net8.0-windows\FreeFlow.exe
```

## Verification

```powershell
FreeFlow.exe --selftest      # formatter suite + real TTS→STT transcription check
FreeFlow.exe --injecttest    # live text-injection check (needs unlocked desktop)
FreeFlow.exe --transcribe x.wav  # transcribe any wav file
```

Report written to `%APPDATA%\FreeFlow\selftest.txt`.
