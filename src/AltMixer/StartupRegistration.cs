using Microsoft.Win32;

namespace AltMixer;

/// <summary>Start at login via the per-user Run key. No elevation is needed for anything AltMixer does.</summary>
static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "AltMixer";

    static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string s && s.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(Name, Command);
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }
}
