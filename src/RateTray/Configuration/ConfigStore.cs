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
            else LastWritten = json;

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

    /// <summary>
    /// Fork: o último JSON que o próprio app gravou. A recarga automática compara o arquivo com
    /// isto para não tratar a própria gravação como edição de fora.
    /// </summary>
    internal static string? LastWritten { get; private set; }

    /// <summary>
    /// Fork: lê o settings.json editado por fora (editor de texto). Devolve nulo quando o conteúdo
    /// é o que o próprio app gravou por último, ou quando o JSON está inválido — um arquivo salvo
    /// pela metade não pode derrubar os ajustes em uso; a próxima gravação válida é aplicada.
    /// <paramref name="busy"/> indica arquivo preso por outro programa: vale tentar de novo.
    /// Não regrava o arquivo, para não brigar com o editor que está com ele aberto.
    /// </summary>
    internal static AppConfig? ReadExternalEdit(out bool busy)
    {
        busy = false;
        try
        {
            var json = File.ReadAllText(Path_);
            if (json == LastWritten) return null;

            var config = FromJson(json);
            LastWritten = json;
            return config;
        }
        catch (FileNotFoundException)
        {
            return null;                                // apagado: a próxima gravação o recria
        }
        catch (IOException)
        {
            busy = true;
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException
                                      or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public static string ToJson(AppConfig config) => JsonSerializer.Serialize(config, Options);

    /// <summary>Fork: cópia independente, para a janela de ajustes editar sem mexer no que está valendo.</summary>
    public static AppConfig Clone(AppConfig config) => FromJson(ToJson(config));

    /// <summary>
    /// Fork: copia os <b>valores</b> de <paramref name="from"/> para dentro dos objetos de
    /// <paramref name="to"/>, sem trocar os objetos. Os provedores guardam referência às próprias
    /// opções (<c>_config.Kimi</c>, <c>_config.OpenAIApi</c>...); trocar o objeto os deixaria lendo
    /// o antigo para sempre. Objetos de opções do próprio app são percorridos; valores, listas e
    /// textos são atribuídos inteiros.
    /// </summary>
    public static void CopyInto(object from, object to)
    {
        foreach (var property in to.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0) continue;

            var value = property.GetValue(from);
            var type = property.PropertyType;
            var isOptionsObject = type.IsClass && type != typeof(string) && type.Namespace == typeof(AppConfig).Namespace;

            if (isOptionsObject && value is not null && property.GetValue(to) is { } target)
                CopyInto(value, target);
            else
                property.SetValue(to, value);
        }
    }

    /// <summary>
    /// Fork: o arquivo em disco difere do que o app gravou por último — alguém o editou e a recarga
    /// ainda não o absorveu (ou ele está inválido, ou preso por um editor no meio da gravação).
    /// </summary>
    /// <param name="countDeletion">
    /// Arquivo apagado conta como edição? Para a janela de ajustes, sim (pergunta antes de
    /// recriar). Para a gravação automática, não: o app recria o arquivo, como faz ao iniciar.
    /// </param>
    internal static bool HasExternalEdit(bool countDeletion = false)
    {
        if (LastWritten is null) return false;
        try
        {
            if (!File.Exists(Path_)) return countDeletion;
            return File.ReadAllText(Path_) != LastWritten;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return true;                                // preso por outro programa: tratar como em edição
        }
    }

    /// <summary>
    /// Fork: uma gravação foi recusada porque o arquivo tem edição externa ainda não aplicada. A
    /// bandeja avisa o usuário — a mudança feita (no menu, arrastando a faixa) vale só até a
    /// recarga aplicar o arquivo, e perdê-la em silêncio não é aceitável.
    /// </summary>
    internal static event Action? SaveBlocked;

    /// <summary>
    /// Grava a configuração. Fork: <b>não</b> grava por cima de uma edição externa ainda não
    /// absorvida — a gravação automática do app (posição da faixa, limites descobertos) perderia
    /// para o que o usuário escreveu à mão. Só a janela de ajustes passa
    /// <paramref name="overwriteExternalEdit"/>, depois de perguntar. Devolve se gravou.
    /// </summary>
    public static bool Save(AppConfig config, bool overwriteExternalEdit = false)
    {
        if (!overwriteExternalEdit && HasExternalEdit())
        {
            SaveBlocked?.Invoke();
            return false;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var json = JsonSerializer.Serialize(config, Options);
            var temp = Path_ + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, Path_, overwrite: true);
            LastWritten = json;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or SecurityException or NotSupportedException)
        {
            // A read-only profile shouldn't take the tray down; the in-memory config still applies.
            return false;
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
