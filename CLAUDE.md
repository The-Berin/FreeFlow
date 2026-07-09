# FreeFlow — Baron's free-forever Wispr Flow clone

.NET 8 WinForms tray app + sherpa-onnx, 100% local/offline. Repo: github.com/The-Berin/FreeFlow (repo-local git identity The-Berin / barongartner@gmail.com). Source in `src/FreeFlow`, exe in `bin\Release\net8.0-windows`. Shipped v2.0.0 (Inno Setup per-user installer → %LOCALAPPDATA%\Programs\FreeFlow + portable zip).

## Pipeline (three models)
Streaming Zipformer int8 (live word-by-word partials ~100ms, outputs UPPERCASE unpunctuated → normalized lowercase) → LiveTyper types as Baron speaks → on release, Parakeet TDT 0.6B v2 int8 re-transcribes and replaces the live text (diff/backspace/paste); CT-Transformer online punctuation as instant fallback. Hold RightCtrl = PTT, tap = latch; press-any-key rebind in-app. Baron's own hotkey = backslash (stored "OemBackslash").

## Audio reality on this PC (do not re-derive — this cost a full day)
- **The AirPods Pro HFP mic (over the TP-Link BT dongle) is the ONLY real microphone on the PC.** Config pins MicDeviceName="AirPods Pro".
- **"Microphone (mvsilicon B1 usb audio)" is FAKE** — the soundbar has no mic; it's a loopback/AEC-reference that hears only the soundbar's own playback. Never use it for dictation.
- **BT verdict (2026-07-06, tone-injection tested): AirPods HFP over this dongle is unfixable in software.** The link either comes up half-open (faint dither ~rms 0.0003, never exact zeros) or drops 200-300ms chunks mid-stream. Keep the resilience machinery but don't chase it further: silence-stream link holder (WasapiOut on the hands-free RENDER endpoint forces SCO up), watchdog (no real signal in 1.5s → cycle link; 2nd strike → session fallback; disarms after first real audio), HFP endpoint volume pinned to 100% (Windows parks it at 37%), dropout auto-recovery (4 attempts ~2.5s → auto-finish take), AGC ceiling 10x + default gain 2x (Baron mumbles).
- HFP link mutes A2DP playback — that's why the mic-test Play button must ReleaseDevice + ~1.2s delay first. Ready-beep is gated on real signal, not first buffer (dead SCO delivers exact zeros).
- Endpoints stuck in PnP "Error": elevated Disable/Enable-PnpDevice + Restart-Service Audiosrv.
- Win10 + this dongle = classic HFP or nothing (no LE Audio). Dongle driver 1.9.1038.3023 is current.

## UI/brand law
Full app window (Home/stats/test, Dictation, Dictionary, Shortcuts, Profiles, History, Settings, Model, About) + MINI pill (172x30 flat dark lozenge: red dot, slim white level bars, timer). Baron rejected the big purple pill as "too AI looking" — no transcript in the pill. Brand = violet tile + white wave, baked PNG/ICO in Assets (AssetGenerator.cs was deleted on request — images are canonical). Palette matches FreeVoice Studio but accent differs; FreeVoice is the CIRCLE mark, FreeFlow the square tile. No em dashes in user-facing strings.

## Verification
`--selftest` (33 checks incl. streaming-partials-arrive-while-feeding), `--injecttest`, `--pillpreview`, `--transcribe <wav>`. The keyboard hook ignores injected keys (LLKHF_INJECTED) so the hotkey can't be triggered programmatically — remember that when testing.

## Editing rules
Never regex-rewrite .cs files via PowerShell Get/Set-Content (mojibake) — use the Edit tool or python.
