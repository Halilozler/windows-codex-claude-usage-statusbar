using Microsoft.Win32;

namespace ClaudeCodexLimits;

internal static class StartupManager
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowsAIStatusbar";
    private const string LegacyValueName = "ClaudeCodexLimits";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            var executable = Environment.ProcessPath;
            if (executable is null)
            {
                return false;
            }

            var currentValue = key?.GetValue(ValueName) as string;
            var legacyValue = key?.GetValue(LegacyValueName) as string;
            return
                currentValue?.Contains(executable, StringComparison.OrdinalIgnoreCase) == true ||
                legacyValue?.Contains(executable, StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        if (enabled)
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Application path was not found.");
            key.SetValue(ValueName, $"\"{executable}\" --startup");
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
    }
}
