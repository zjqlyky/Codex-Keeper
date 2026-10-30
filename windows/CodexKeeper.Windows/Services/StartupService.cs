using Microsoft.Win32;

namespace CodexKeeper.Windows.Services;

internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexKeeper";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Windows startup settings could not be opened.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("The application path could not be determined.");
        key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
    }
}
