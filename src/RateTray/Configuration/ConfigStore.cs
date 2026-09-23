using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RateTray.Configuration;

/// <summary>Loads and persists settings.json under %APPDATA%\Gaugely.</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gaugely");

    /// <summary>Pasta usada antes da renomeação do app — só lida, para migrar.</summary>
    internal static string LegacyDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RateTray");

    /// <summary>
    /// Fork: na primeira execução depois da renomeação, traz o settings.json da pasta antiga. Copia,
    /// não move — a versão anterior continua achando o seu. Nunca sobrescreve um arquivo novo.
    /// </summary>
    internal static void MigrateLegacy(string legacyDirectory, string directory)
    {
        try
        {
            var from = System.IO.Path.Combine(legacyDirectory, "settings.json");
            var to = System.IO.Path.Combine(directory, "settings.json");
            if (File.Exists(to) || !File.Exists(from)) return;

            System.IO.Directory.CreateDirectory(directory);
            File.Copy(from, to, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
    }

    public static string Path_ => System.IO.Path.Combine(Directory, "settings.json");

    /// <summary>
    /// Never throws: a corrupt or unreadable file falls back to defaults so the tray
    /// still comes up. The broken file is kept as settings.json.bad for inspection.
    /// </summary>
    public static AppConfig Load()
    {
        MigrateLegacy(LegacyDirectory, Directory);

        try
        {
            if (!File.Exists(Path_))
            {
                var fresh = new AppConfig();
                Save(fresh);
                return fresh;
            }

            var json = File.ReadAllText(Path_);
            var config = FromJson(json);

            // A file written by an older version is missing whatever options were added since,
            // and a hand-edited one may just have had nulls or out-of-range numbers repaired.
            // Writing it back leaves the file complete, self-documenting and no longer broken
            // instead of silently short.
            if (JsonSerializer.Serialize(config, Options) != json) Save(config);

            return config;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or SecurityException or JsonException or NotSupportedException)
        {
            TryPreserveBrokenFile();
            return new AppConfig();
        }
    }

    /// <summary>
    /// Deserialises and normalises: the pure half of <see cref="Load"/>, so the hardening
    /// against a hand-edited file can be tested without touching %APPDATA%. Throws on malformed
    /// JSON — that is <see cref="Load"/>'s job to catch, together with the unreadable file.
    /// </summary>
    public static AppConfig FromJson(string json) =>
        (JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig()).Normalize();

    public static void Save(AppConfig config)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var json = JsonSerializer.Serialize(config, Options);
            var temp = Path_ + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, Path_, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or SecurityException or NotSupportedException)
        {
            // A read-only profile shouldn't take the tray down; the in-memory config still applies.
        }
    }

    private static void TryPreserveBrokenFile()
    {
        try
        {
            if (File.Exists(Path_)) File.Copy(Path_, Path_ + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or SecurityException or NotSupportedException)
        {
            // best effort only
        }
    }
}
