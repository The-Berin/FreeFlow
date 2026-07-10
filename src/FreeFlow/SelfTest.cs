using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using FreeFlow.Core;
using NAudio.Wave;

namespace FreeFlow;

/// <summary>
/// Headless verification: config roundtrip, the whole formatter suite, and a real
/// end-to-end transcription of Windows-TTS-generated speech through the loaded model.
/// Writes a report to %APPDATA%\FreeFlow\selftest.txt and returns a process exit code.
/// </summary>
public static class SelfTest
{
    private static readonly StringBuilder Report = new();
    private static int _failures;

    public static int Run()
    {
        Report.AppendLine($"FreeFlow self-test — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Report.AppendLine(new string('-', 60));

        TestConfig();
        TestFormatter();
        TestResampler();
        TestChunkResampler();
        TestTranscription();
        TestStreaming();

        Report.AppendLine(new string('-', 60));
        Report.AppendLine(_failures == 0 ? "ALL TESTS PASSED" : $"{_failures} FAILURE(S)");

        File.WriteAllText(Paths.SelfTestReportPath, Report.ToString());
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Spawns a real window with a textbox, injects text into it via the production
    /// clipboard-paste path, and verifies the textbox received it.
    /// </summary>
    public static int InjectTest()
    {
        string result = "FAIL — did not run";
        using var form = new Form { Text = "FreeFlow inject test", Size = new Size(420, 140), StartPosition = FormStartPosition.CenterScreen, TopMost = true };
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true };
        form.Controls.Add(box);

        var timer = new System.Windows.Forms.Timer { Interval = 600 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // launched from a background shell we must beat the foreground lock;
            // never send input unless our own window actually has focus
            for (int i = 0; i < 30 && GetForegroundWindow() != form.Handle; i++)
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    uint fgThread = GetWindowThreadProcessId(fg, out _);
                    uint ourThread = GetCurrentThreadId();
                    AttachThreadInput(ourThread, fgThread, true);
                    BringWindowToTop(form.Handle);
                    SetForegroundWindow(form.Handle);
                    AttachThreadInput(ourThread, fgThread, false);
                }
                else
                {
                    keybd_event(0x12, 0, 0, UIntPtr.Zero);
                    keybd_event(0x12, 0, 2, UIntPtr.Zero);
                    SetForegroundWindow(form.Handle);
                }
                Thread.Sleep(150);
            }
            if (GetForegroundWindow() != form.Handle)
            {
                IntPtr fg = GetForegroundWindow();
                var title = new System.Text.StringBuilder(256);
                if (fg != IntPtr.Zero) GetWindowText(fg, title, 256);
                result = $"SKIP — could not take foreground focus; injection not attempted. " +
                         $"Current foreground: {(fg == IntPtr.Zero ? "none" : $"\"{title}\" ({fg})")}";
                form.Close();
                return;
            }

            box.Focus();
            const string expected = "FreeFlow injection works!\nSecond line too.";
            TextInjector.Inject(expected, "Paste");

            var verify = new System.Windows.Forms.Timer { Interval = 900 };
            verify.Tick += (_, _) =>
            {
                verify.Stop();
                string got = box.Text.Replace("\r\n", "\n");
                result = got == expected
                    ? $"PASS — textbox received exact text ({got.Length} chars)"
                    : $"FAIL — expected \"{expected}\", got \"{got}\"";

                // now test the Type fallback path on a cleared box
                box.Clear();
                box.Focus();
                TextInjector.Inject("typed path OK", "Type");
                var verify2 = new System.Windows.Forms.Timer { Interval = 700 };
                verify2.Tick += (_, _) =>
                {
                    verify2.Stop();
                    result += box.Text == "typed path OK"
                        ? "\nPASS — Type fallback works"
                        : $"\nFAIL — Type fallback got \"{box.Text}\"";
                    form.Close();
                };
                verify2.Start();
            };
            verify.Start();
        };
        form.Shown += (_, _) => { form.Activate(); timer.Start(); };
        Application.Run(form);

        File.WriteAllText(Paths.SelfTestReportPath, result);
        return result.Contains("FAIL") ? 1 : 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    /// <summary>Animates the overlay pill with synthetic audio + words for ~14s so you can see the design without a mic.</summary>
    public static int PillPreview()
    {
        var overlay = new UI.OverlayForm();
        _ = overlay.Handle;
        overlay.SetState(UI.OverlayState.Listening);

        int tick = 0;
        var rng = new Random(7);
        var driver = new System.Windows.Forms.Timer { Interval = 66 };
        driver.Tick += (_, _) =>
        {
            tick++;
            var bands = new float[16];
            for (int i = 0; i < bands.Length; i++)
            {
                double envelope = 0.35 + 0.65 * Math.Abs(Math.Sin(tick / 9.0 + i));
                double speech = Math.Max(0, 1.2 - Math.Abs(i - (tick % 12)) * 0.22);
                bands[i] = (float)Math.Min(1.0, (0.15 + 0.85 * rng.NextDouble()) * envelope * (0.4 + speech));
            }
            overlay.PushSpectrum(bands);

            if (tick == 120)
                overlay.SetState(UI.OverlayState.Processing);
            if (tick == 145)
                overlay.SetState(UI.OverlayState.Success);
            if (tick >= 170)
                Application.Exit();
        };
        driver.Start();
        Application.Run();
        return 0;
    }

    /// <summary>
    /// Headless end-to-end dictation test on REAL audio hardware: opens the actual
    /// recorder (watchdog, AGC, link machinery and all), speaks a test phrase through
    /// the default output so a loopback/room mic can hear it, streams partials live,
    /// then runs the accurate final pass — exactly the production dictation path
    /// minus the hotkey and text injection (covered by --injecttest).
    /// Usage: FreeFlow.exe --livetest [micDeviceName]
    /// </summary>
    public static int LiveTest(string micDeviceName)
    {
        if (micDeviceName.Equals("loopback", StringComparison.OrdinalIgnoreCase))
            return LoopbackLiveTest();

        var report = new StringBuilder();
        report.AppendLine($"FreeFlow live test — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"mic override: \"{micDeviceName}\" (empty = default)");

        var cfg = AppConfig.Load();
        cfg.MicDeviceName = micDeviceName; // in-memory override only, never saved
        cfg.KeepMicWarm = false;

        using var streaming = new StreamingEngine();
        streaming.Load(cfg);
        using var engine = new SttEngine();
        engine.Load(cfg);
        report.AppendLine($"engines: streaming={streaming.IsLoaded} accurate={engine.IsLoaded}");

        var recorder = new AudioRecorder();
        int partials = 0;
        string lastPartial = "";
        streaming.Partial += t => { Interlocked.Increment(ref partials); lastPartial = t; };
        recorder.Samples16k += c => streaming.Feed(c);
        recorder.Error += m => report.AppendLine($"RECORDER ERROR: {m}");

        try
        {
            recorder.Configure(cfg);
            if (streaming.IsLoaded) streaming.StartSession();
            recorder.StartCapture();
            Thread.Sleep(400); // let the device open before sound starts

            const string phrase = "The quick brown fox jumps over the lazy dog while free flow listens carefully.";
            using (var synth = new SpeechSynthesizer())
            {
                synth.SetOutputToDefaultAudioDevice();
                synth.Rate = 0;
                synth.Speak(phrase);
            }
            Thread.Sleep(800);

            var samples = recorder.StopCapture();
            string streamFinal = streaming.IsLoaded ? streaming.FinishSession() : "";

            double rms = 0;
            for (int i = 0; i < samples.Length; i++) rms += samples[i] * samples[i];
            rms = samples.Length > 0 ? Math.Sqrt(rms / samples.Length) : 0;

            report.AppendLine($"captured: {samples.Length / 16000.0:0.0}s, rms {rms:0.0000}");
            report.AppendLine($"streaming: {partials} partials, final \"{lastPartial}\" / \"{streamFinal}\"");

            string final = "";
            if (engine.IsLoaded && samples.Length > 16000 / 2)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                final = engine.Transcribe(samples);
                report.AppendLine($"accurate pass ({sw.ElapsedMilliseconds}ms): \"{final}\"");
            }

            string norm = new string((final + " " + streamFinal).ToLowerInvariant()
                .Where(ch => char.IsLetter(ch) || ch == ' ').ToArray());
            bool pass = norm.Contains("quick brown fox") && norm.Contains("lazy dog");
            report.AppendLine(pass
                ? "RESULT: PASS — full pipeline heard and transcribed real audio end to end"
                : rms < 0.001
                    ? "RESULT: FAIL — captured silence (no audio path from output to this mic)"
                    : "RESULT: FAIL — audio captured but transcript wrong");
            File.WriteAllText(Paths.SelfTestReportPath, report.ToString());
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.AppendLine($"EXCEPTION: {ex}");
            File.WriteAllText(Paths.SelfTestReportPath, report.ToString());
            return 1;
        }
        finally
        {
            recorder.Dispose();
        }
    }

    /// <summary>
    /// End-to-end recognition test on LIVE OS audio with no microphone at all:
    /// speaks a phrase through the default render device while capturing that same
    /// endpoint via WASAPI loopback, then runs the audio through the streaming and
    /// accurate engines. Proves the recognition pipeline against real in-flight
    /// audio even on a machine with zero capture devices.
    /// Usage: FreeFlow.exe --livetest loopback
    /// </summary>
    private static int LoopbackLiveTest()
    {
        var report = new StringBuilder();
        report.AppendLine($"FreeFlow loopback live test — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var cfg = AppConfig.Load();
        using var streaming = new StreamingEngine();
        streaming.Load(cfg);
        using var engine = new SttEngine();
        engine.Load(cfg);
        report.AppendLine($"engines: streaming={streaming.IsLoaded} accurate={engine.IsLoaded}");

        int partials = 0;
        string lastPartial = "";
        streaming.Partial += t => { Interlocked.Increment(ref partials); lastPartial = t; };

        try
        {
            var mono = new List<float>();
            using var cap = new NAudio.Wave.WasapiLoopbackCapture();
            var fmt = cap.WaveFormat;
            report.AppendLine($"loopback endpoint format: {fmt.SampleRate} Hz, {fmt.Channels} ch, {fmt.Encoding}");

            cap.DataAvailable += (_, e) =>
            {
                int ch = fmt.Channels;
                if (fmt.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat)
                {
                    int frames = e.BytesRecorded / 4 / ch;
                    for (int i = 0; i < frames; i++)
                    {
                        float sum = 0;
                        for (int c = 0; c < ch; c++)
                            sum += BitConverter.ToSingle(e.Buffer, (i * ch + c) * 4);
                        mono.Add(sum / ch);
                    }
                }
                else // 16-bit PCM
                {
                    int frames = e.BytesRecorded / 2 / ch;
                    for (int i = 0; i < frames; i++)
                    {
                        float sum = 0;
                        for (int c = 0; c < ch; c++)
                            sum += BitConverter.ToInt16(e.Buffer, (i * ch + c) * 2) / 32768f;
                        mono.Add(sum / ch);
                    }
                }
            };

            cap.StartRecording();
            Thread.Sleep(300);

            const string phrase = "The quick brown fox jumps over the lazy dog while free flow listens carefully.";
            using (var synth = new SpeechSynthesizer())
            {
                synth.SetOutputToDefaultAudioDevice();
                synth.Rate = 0;
                synth.Speak(phrase);
            }
            Thread.Sleep(600);
            cap.StopRecording();
            Thread.Sleep(200);

            var samples = AudioRecorder.Resample(mono.ToArray(), fmt.SampleRate, 16000);
            double rms = 0;
            for (int i = 0; i < samples.Length; i++) rms += samples[i] * samples[i];
            rms = samples.Length > 0 ? Math.Sqrt(rms / samples.Length) : 0;
            report.AppendLine($"captured off the wire: {samples.Length / 16000.0:0.0}s, rms {rms:0.0000}");

            // feed the streaming engine exactly like the mic path does (16 kHz chunks)
            string streamFinal = "";
            if (streaming.IsLoaded)
            {
                streaming.StartSession();
                for (int off = 0; off < samples.Length; off += 1600)
                    streaming.Feed(samples.Skip(off).Take(1600).ToArray());
                streamFinal = streaming.FinishSession();
                report.AppendLine($"streaming: {partials} partials, final \"{streamFinal}\"");
            }

            string final = "";
            if (engine.IsLoaded && samples.Length > 8000)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                final = engine.Transcribe(samples);
                report.AppendLine($"accurate pass ({sw.ElapsedMilliseconds}ms): \"{final}\"");
            }

            string norm = new string((final + " " + streamFinal).ToLowerInvariant()
                .Where(ch2 => char.IsLetter(ch2) || ch2 == ' ').ToArray());
            bool pass = norm.Contains("quick brown fox") && norm.Contains("lazy dog");
            report.AppendLine(pass
                ? "RESULT: PASS — live OS audio recognized end to end (render → loopback → engines)"
                : rms < 0.001
                    ? "RESULT: FAIL — loopback captured silence (render endpoint not mixing?)"
                    : "RESULT: FAIL — audio captured but transcript wrong");
            File.WriteAllText(Paths.SelfTestReportPath, report.ToString());
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.AppendLine($"EXCEPTION: {ex}");
            File.WriteAllText(Paths.SelfTestReportPath, report.ToString());
            return 1;
        }
    }

    public static int TranscribeFile(string wavPath)
    {
        var cfg = AppConfig.Load();
        using var engine = new SttEngine();
        engine.Load(cfg);
        if (!engine.IsLoaded)
        {
            File.WriteAllText(Paths.SelfTestReportPath, "ENGINE LOAD FAILED: " + engine.LoadError);
            return 1;
        }
        var samples = LoadWavAs16kMono(wavPath);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string text = engine.Transcribe(samples);
        File.WriteAllText(Paths.SelfTestReportPath,
            $"file: {wavPath}\naudio: {samples.Length / 16000.0:0.00}s\ndecode: {sw.ElapsedMilliseconds}ms\ntext: {text}");
        return 0;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        Report.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    private static void TestConfig()
    {
        // NEVER touch the user's real settings: snapshot the file, test, put it back exactly
        string? original = File.Exists(Paths.ConfigPath) ? File.ReadAllText(Paths.ConfigPath) : null;
        try
        {
            var c = new AppConfig { HotkeyName = "F9", MicGain = 2.5 };
            c.Dictionary.Add(new DictionaryEntry { Spoken = "hey gen", Written = "HeyGen" });
            c.Save();
            var loaded = AppConfig.Load();
            Check("config roundtrip", loaded.HotkeyName == "F9" && Math.Abs(loaded.MicGain - 2.5) < 0.001
                && loaded.Dictionary.Count == 1 && loaded.Dictionary[0].Written == "HeyGen");
        }
        finally
        {
            if (original != null)
                File.WriteAllText(Paths.ConfigPath, original);
            else if (File.Exists(Paths.ConfigPath))
                File.Delete(Paths.ConfigPath);
        }
    }

    private static void TestFormatter()
    {
        var cfg = new AppConfig();
        cfg.Dictionary.Add(new DictionaryEntry { Spoken = "free flow", Written = "FreeFlow" });
        cfg.Snippets.Add(new Snippet { Trigger = "my email", Expansion = "baron@example.com" });

        FormatResult F(string raw, string tone = "Default") => TextFormatter.Format(raw, cfg, tone);

        Check("fillers removed", F("Um, hello there uh I think.").Text == "Hello there I think.",
            $"got: \"{F("Um, hello there uh I think.").Text}\"");
        Check("new line command", F("first item new line second item").Text == "First item\nSecond item",
            $"got: \"{F("first item new line second item").Text}\"");
        Check("new paragraph command", F("intro new paragraph body").Text == "Intro\n\nBody",
            $"got: \"{F("intro new paragraph body").Text}\"");
        Check("scratch that mid-utterance", F("send the report scratch that email the report").Text == "Email the report",
            $"got: \"{F("send the report scratch that email the report").Text}\"");
        Check("scratch that alone deletes", F("Scratch that.").DeleteLast);
        Check("dictionary replacement", F("i love free flow so much").Text.Contains("FreeFlow"),
            $"got: \"{F("i love free flow so much").Text}\"");
        Check("snippet expansion", F("My email.").Text == "baron@example.com",
            $"got: \"{F("My email.").Text}\"");
        Check("casual drops trailing period", F("Sounds good.", "Casual").Text == "Sounds good",
            $"got: \"{F("Sounds good.", "Casual").Text}\"");
        Check("professional adds period", F("sounds good", "Professional").Text == "Sounds good.",
            $"got: \"{F("sounds good", "Professional").Text}\"");
        Check("verbatim untouched", F("um this stays exactly scratch that as is", "Verbatim").Text
            == "um this stays exactly scratch that as is");

        var punctCfg = new AppConfig { PunctuationWords = true };
        var punct = TextFormatter.Format("hello comma world period", punctCfg, "Default");
        Check("spoken punctuation", punct.Text == "Hello, world.", $"got: \"{punct.Text}\"");

        Check("empty input", F("").Text == "" && !F("").DeleteLast);
        Check("whitespace tidy", F("too   many    spaces .").Text == "Too many spaces.",
            $"got: \"{F("too   many    spaces .").Text}\"");
    }

    private static void TestResampler()
    {
        // 48k sine resampled to 16k keeps duration and stays in range
        var src = new float[48000];
        for (int i = 0; i < src.Length; i++)
            src[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 48000.0);
        var dst = AudioRecorder.Resample(src, 48000, 16000);
        Check("resampler length", Math.Abs(dst.Length - 16000) <= 1, $"got {dst.Length}");
        Check("resampler range", dst.All(f => f is >= -1.01f and <= 1.01f));
    }

    private static void TestChunkResampler()
    {
        // chunked streaming resample must agree with the one-shot resampler
        var src = new float[48000];
        for (int i = 0; i < src.Length; i++)
            src[i] = (float)Math.Sin(2 * Math.PI * 300 * i / 48000.0);

        var streamed = new List<float>();
        var rs = new ChunkResampler(48000, 16000);
        for (int off = 0; off < src.Length; off += 480)
            streamed.AddRange(rs.Process(src.Skip(off).Take(480).ToArray()));

        Check("chunk resampler length", Math.Abs(streamed.Count - 16000) <= 2, $"got {streamed.Count}");

        var oneShot = AudioRecorder.Resample(src, 48000, 16000);
        double maxDiff = 0;
        int n = Math.Min(streamed.Count, oneShot.Length);
        for (int i = 0; i < n; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(streamed[i] - oneShot[i]));
        Check("chunk resampler seamless", maxDiff < 0.02, $"max diff {maxDiff:0.0000}");
    }

    private static void TestStreaming()
    {
        var streamModel = ModelRegistry.Get(ModelRegistry.StreamingModelId);
        if (!streamModel.IsDownloaded())
        {
            Check("streaming model downloaded", false, "not downloaded — live mode unavailable");
            return;
        }
        Check("streaming model downloaded", true);

        var cfg = AppConfig.Load();
        using var streaming = new StreamingEngine();
        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        streaming.Load(cfg);
        Check("streaming engine load", streaming.IsLoaded,
            streaming.IsLoaded ? $"{loadSw.ElapsedMilliseconds}ms" : streaming.LoadError ?? "");
        if (!streaming.IsLoaded) return;
        Check("punctuation model load", streaming.PunctLoaded, streaming.PunctLoaded ? "" : "missing — live text will be unpunctuated");

        string wav = Path.Combine(Path.GetTempPath(), "freeflow_streamtest.wav");
        try
        {
            using (var synth = new SpeechSynthesizer())
            {
                synth.SetOutputToWaveFile(wav, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                synth.Rate = 0;
                synth.Speak("The quick brown fox jumps over the lazy dog and then runs far away into the green forest.");
            }
            var samples = LoadWavAs16kMono(wav);
            double audioSec = samples.Length / 16000.0;

            var partialAtChunk = new List<int>();
            int fedChunks = 0;
            streaming.Partial += _ => { lock (partialAtChunk) partialAtChunk.Add(fedChunks); };

            streaming.StartSession();
            const int chunk = 1600; // 100 ms
            int totalChunks = (samples.Length + chunk - 1) / chunk;
            for (int off = 0; off < samples.Length; off += chunk)
            {
                fedChunks++;
                streaming.Feed(samples.Skip(off).Take(chunk).ToArray());
                Thread.Sleep(12); // give the decode thread breathing room, ~8x realtime
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string final = streaming.FinishSession();
            sw.Stop();

            int firstPartialChunk;
            int partialCount;
            lock (partialAtChunk)
            {
                firstPartialChunk = partialAtChunk.Count > 0 ? partialAtChunk[0] : int.MaxValue;
                partialCount = partialAtChunk.Count;
            }

            Check("streaming partials fired", partialCount >= 3, $"{partialCount} partial updates");
            Check("words arrived WHILE speaking", firstPartialChunk < totalChunks * 0.7,
                $"first partial after chunk {firstPartialChunk}/{totalChunks}");
            Check("streaming final text", final.Contains("quick brown fox") && final.Contains("lazy dog"),
                $"\"{Truncate(final, 90)}\"");
            Check("streaming finish is fast", sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds}ms drain");

            string punctuated = streaming.Punctuate(final);
            Check("punctuation restored", punctuated.Length > 0 &&
                (char.IsUpper(punctuated[0]) || punctuated != final),
                $"\"{Truncate(punctuated, 90)}\"");

            // second session on the same engine must work (stream reuse bug guard)
            streaming.StartSession();
            streaming.Feed(samples.Take(16000).ToArray());
            Thread.Sleep(300);
            string second = streaming.FinishSession();
            Check("second session works", second.Length > 0, $"\"{Truncate(second, 60)}\"");
        }
        catch (Exception ex)
        {
            Check("streaming pipeline", false, ex.Message);
        }
        finally
        {
            try { File.Delete(wav); } catch { }
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static void TestTranscription()
    {
        var cfg = AppConfig.Load();
        var model = ModelRegistry.Get(cfg.ModelId);
        if (!model.IsDownloaded())
        {
            Check("model downloaded", false, $"{model.Id} missing from {model.Dir}");
            return;
        }
        Check("model downloaded", true, model.Id);

        using var engine = new SttEngine();
        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        engine.Load(cfg);
        Check("engine load", engine.IsLoaded, engine.IsLoaded ? $"{loadSw.ElapsedMilliseconds}ms" : engine.LoadError ?? "");
        if (!engine.IsLoaded) return;

        string wav = Path.Combine(Path.GetTempPath(), "freeflow_selftest.wav");
        try
        {
            using (var synth = new SpeechSynthesizer())
            {
                synth.SetOutputToWaveFile(wav, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                synth.Rate = 0;
                synth.Speak("The quick brown fox jumps over the lazy dog.");
            }

            var samples = LoadWavAs16kMono(wav);
            Check("tts audio generated", samples.Length > 16000, $"{samples.Length / 16000.0:0.00}s");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string text = engine.Transcribe(samples);
            sw.Stop();
            string norm = new string(text.ToLowerInvariant().Where(ch => char.IsLetter(ch) || ch == ' ').ToArray());
            bool match = norm.Contains("quick brown fox") && norm.Contains("lazy dog");
            Check("transcription accuracy", match, $"\"{text}\" in {sw.ElapsedMilliseconds}ms");
            Check("transcription speed", sw.ElapsedMilliseconds < 30000, $"{sw.ElapsedMilliseconds}ms for {samples.Length / 16000.0:0.0}s audio");
        }
        catch (Exception ex)
        {
            Check("tts transcription", false, ex.Message);
        }
        finally
        {
            try { File.Delete(wav); } catch { }
        }
    }

    private static float[] LoadWavAs16kMono(string path)
    {
        using var reader = new AudioFileReader(path); // float samples at source rate
        var all = new List<float>();
        var buf = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i++)
                all.Add(buf[i]);

        var samples = all.ToArray();
        if (reader.WaveFormat.Channels > 1)
        {
            int ch = reader.WaveFormat.Channels;
            var mono = new float[samples.Length / ch];
            for (int i = 0; i < mono.Length; i++)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++) sum += samples[i * ch + c];
                mono[i] = sum / ch;
            }
            samples = mono;
        }
        return reader.WaveFormat.SampleRate == 16000
            ? samples
            : AudioRecorder.Resample(samples, reader.WaveFormat.SampleRate, 16000);
    }
}
