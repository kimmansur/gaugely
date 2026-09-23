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
    /// este executável. Só migra uma entrada que aponte de fato para o app anterior
    /// (<c>RateTray.exe</c>) — entrada com o mesmo nome apontando para outro programa não é nossa e
    /// fica onde está. E nunca sobrescreve uma entrada nova já existente.
    /// </summary>
    public static void MigrateLegacy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null || !IsLegacyEntry(key.GetValue(LegacyValueName) as string)) return;

            if (key.GetValue(ValueName) is string { Length: > 0 })
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            else
                TrySet(true, out _);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
    }

    /// <summary>
    /// Entrada Run que aponta para o executável do app anterior: o programa iniciado — entre aspas,
    /// ou até o primeiro ".exe" seguido de espaço ou do fim — tem de ser um caminho absoluto cujo
    /// arquivo é <c>RateTray.exe</c>. "cmd.exe /c …\\RateTray.exe" ou "…\\RateTray.exe.bat" não servem.
    /// </summary>
    internal static bool IsLegacyEntry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var path = LaunchedProgram(value.Trim());
        return Path.IsPathRooted(path) &&
               string.Equals(Path.GetFileName(path), "RateTray.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string LaunchedProgram(string text)
    {
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 0 ? text[1..end] : text[1..];
        }

        for (var at = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); at >= 0;
             at = text.IndexOf(".exe", at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var after = at + 4;
            if (after == text.Length || text[after] == ' ') return text[..after];
        }

        return text;
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
