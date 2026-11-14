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
                string.IsNullOrWhiteSpace(command))
                return;

            // Split "path" args (or path args) so arguments such as --tray survive the repoint.
            var trimmed = command.Trim();
            string path, arguments;
            if (trimmed.StartsWith('"'))
            {
                var close = trimmed.IndexOf('"', 1);
                path = close > 0 ? trimmed[1..close] : trimmed.Trim('"');
                arguments = close > 0 ? trimmed[(close + 1)..].Trim() : string.Empty;
            }
            else
            {
                var space = trimmed.IndexOf(" -", StringComparison.Ordinal);
                path = space > 0 ? trimmed[..space] : trimmed;
                arguments = space > 0 ? trimmed[(space + 1)..].Trim() : string.Empty;
            }

            if (string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase))
                return;
            var repointed = arguments.Length == 0 ? $"\"{executablePath}\"" : $"\"{executablePath}\" {arguments}";
            key.SetValue(RunValueName, repointed, RegistryValueKind.String);
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