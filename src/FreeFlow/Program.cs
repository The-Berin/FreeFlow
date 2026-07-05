using FreeFlow.Core;
using FreeFlow.UI;

namespace FreeFlow;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Paths.EnsureDirs();

        if (args.Contains("--selftest"))
            return SelfTest.Run();
        if (args.Contains("--injecttest"))
        {
            ApplicationConfiguration.Initialize();
            return SelfTest.InjectTest();
        }
        if (args.Length >= 2 && args[0] == "--transcribe")
            return SelfTest.TranscribeFile(args[1]);
        if (args.Contains("--pillpreview"))
        {
            ApplicationConfiguration.Initialize();
            return SelfTest.PillPreview();
        }

        using var mutex = new Mutex(initiallyOwned: true, "FreeFlow_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("FreeFlow is already running — look for the mic icon in the system tray.",
                "FreeFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) =>
        {
            Logger.Log(e.Exception, "unhandled UI exception");
            MessageBox.Show($"FreeFlow hit an unexpected error (logged):\n{e.Exception.Message}",
                "FreeFlow", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Log($"FATAL: {e.ExceptionObject}");

        var cfg = AppConfig.Load();
        cfg.Save(); // materialize defaults on first run

        Logger.Log($"FreeFlow starting — model {cfg.ModelId}, hotkey {cfg.HotkeyName}, live={cfg.LiveTyping}");
        Application.Run(new TrayContext(cfg, showMainWindow: !args.Contains("--minimized")));
        return 0;
    }
}
