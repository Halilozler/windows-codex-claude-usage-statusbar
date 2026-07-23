using System.Diagnostics;

namespace ClaudeCodexLimits;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\ClaudeCodexLimits-8A16FCB4", out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        // A background monitor should degrade quietly, never interrupt the user
        // with a crash dialog. These handlers keep an unforeseen exception on
        // any thread from tearing the tray app down.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            Debug.WriteLine($"Unhandled UI exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Debug.WriteLine($"Unhandled exception: {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();
        var showOnStart = !Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(argument => string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayApplicationContext(showOnStart));
        GC.KeepAlive(mutex);
    }
}
