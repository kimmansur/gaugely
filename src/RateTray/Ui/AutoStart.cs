using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace RateTray.Ui;

/// <summary>Per-user autostart via the HKCU Run key — needs no elevation.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Gaugely";

    /// <summary>Nome usado antes da renomeação do app.</summary>
    private const string LegacyValueName = "RateTray";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    public static bool TrySet(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) { error = "Run-Key nicht schreibbar"; return false; }

            if (enabled)
            {
                var path = ExecutablePath();
                if (path is null) { error = "Programmpfad nicht ermittelbar"; return false; }
                key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            // Duas entradas iniciariam duas cópias no logon — a antiga sai em qualquer caso.
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException
                                      or IOException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Fork: quem tinha início automático sob o nome antigo continua com ele, agora apontando para
    /// este executável. Sem entrada antiga, não faz nada — não liga o que o usuário não ligou.
    /// </summary>
    public static void MigrateLegacy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(LegacyValueName) is not string { Length: > 0 }) return;
            TrySet(true, out _);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
    }

    /// <summary>
    /// Environment.ProcessPath is the real host executable, which is what a single-file
    /// publish needs; Assembly.Location is empty there.
    /// </summary>
    private static string? ExecutablePath()
    {
        if (Environment.ProcessPath is { Length: > 0 } path) return path;
        return Process.GetCurrentProcess().MainModule?.FileName;
    }
}
