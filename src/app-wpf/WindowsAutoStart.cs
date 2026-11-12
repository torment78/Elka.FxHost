using Microsoft.Win32;

namespace Elka.VoiceMeeterFxHost.App;

internal static class WindowsAutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ElkaFxHost";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) is string command && !string.IsNullOrWhiteSpace(command);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>If autostart is on but points at another file (e.g. the old program name), repoint it.</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (string.IsNullOrWhiteSpace(executablePath) || key?.GetValue(RunValueName) is not string command ||
                string.IsNullOrWhiteSpace(command) ||
                string.Equals(command.Trim('"'), executablePath, StringComparison.OrdinalIgnoreCase))
                return;
            key.SetValue(RunValueName, $"\"{executablePath}\"", RegistryValueKind.String);
        }
        catch
        {
            // Startup registration is a convenience; never block the app over it.
        }
    }

    public static bool TrySetEnabled(bool enabled, out string error)
    {
        error = string.Empty;

        try
        {
            if (!enabled)
            {
                using var existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                existingKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
                return true;
            }

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                error = "The current FX Host executable path could not be determined.";
                return false;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "The Windows startup registry key could not be opened.";
                return false;
            }

            key.SetValue(RunValueName, $"\"{executablePath}\"", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}