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

        ApplicationConfiguration.Initialize();
        var showOnStart = !Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(argument => string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayApplicationContext(showOnStart));
        GC.KeepAlive(mutex);
    }
}
