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
        TestTranscription();

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
        var c = new AppConfig { HotkeyName = "F9", MicGain = 2.5 };
        c.Dictionary.Add(new DictionaryEntry { Spoken = "hey gen", Written = "HeyGen" });
        c.Save();
        var loaded = AppConfig.Load();
        Check("config roundtrip", loaded.HotkeyName == "F9" && Math.Abs(loaded.MicGain - 2.5) < 0.001
            && loaded.Dictionary.Count == 1 && loaded.Dictionary[0].Written == "HeyGen");
        // restore defaults so the test never bricks the user's real config
        new AppConfig().Save();
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
