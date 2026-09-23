using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace RateTray;

/// <summary>
/// Fork: baixa a versão publicada nas releases deste repositório, confere o SHA256 e troca o
/// executável — **só quando o usuário pede**, pelo botão do Sobre. Não existe instalação sem clique:
/// o hash vem da mesma release que o binário, então prova integridade no caminho, mas não prova
/// quem publicou. Enquanto não houver assinatura independente, a decisão final é de uma pessoa.
///
/// O que cada verificação barra:
/// <list type="bullet">
///   <item>URL de asset fora de <see cref="AppInfo.ApiAssetsPrefix"/> — resposta da API adulterada
///         ou apontando para outro repositório;</item>
///   <item>SHA256 diferente do <c>SHA256SUMS.txt</c> — arquivo corrompido ou trocado no caminho.
///         O hash é calculado sobre os bytes <b>em memória</b>, antes de qualquer coisa tocar o
///         disco, então não há janela entre conferir e usar;</item>
///   <item>versão do arquivo diferente da tag — binário velho republicado sob número novo, que é o
///         jeito de forçar um downgrade para uma versão com falha conhecida.</item>
/// </list>
/// </summary>
public static class UpdateInstaller
{
    public const string ExecutableAsset = "Gaugely.exe";
    public const string ChecksumAsset = "SHA256SUMS.txt";

    /// <summary>
    /// Prefixo dos arquivos de espera. Só o que começa com ele é tratado como resto de download
    /// interrompido — outro arquivo na pasta, mesmo terminando em <c>.new</c>, não é nosso.
    /// </summary>
    internal const string StagingPrefix = ".gaugely-";

    /// <summary>Teto de download. O binário publicado tem menos de 1 MB; 200 MB cobre até um build autocontido.</summary>
    internal const long MaxDownloadBytes = 200L * 1024 * 1024;

    public sealed record Asset(string Name, string ApiUrl, long Size);

    public sealed record Release(Version Version, string Tag, IReadOnlyList<Asset> Assets)
    {
        public Asset? Find(string name) =>
            Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resultado de uma tentativa de instalação; <see cref="Applied"/> falso traz o motivo.</summary>
    public sealed record Outcome(bool Applied, string? Error = null);

    // ------------------------------------------------------------------ leitura

    /// <summary>Lê a release mais recente do repositório; <c>null</c> se não houver ou se falhar.</summary>
    public static async Task<Release?> LatestReleaseAsync(CancellationToken token = default)
    {
        try
        {
            using var http = CreateClient();
            await using var stream = await http.GetStreamAsync(AppInfo.ApiLatestReleaseUrl, token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            return ParseRelease(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Converte o JSON de uma release em <see cref="Release"/>; <c>null</c> se a tag não for vX.Y.Z.
    /// Assets com URL fora deste repositório são descartados aqui, não só na hora de baixar.
    /// </summary>
    internal static Release? ParseRelease(JsonElement element)
    {
        if (!element.TryGetProperty("tag_name", out var tagName)) return null;
        var tag = tagName.GetString();
        if (!UpdateCheck.TryParseTag(tag, out var version)) return null;

        var assets = new List<Asset>();
        if (element.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in list.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || !IsTrustedAssetUrl(url)) continue;
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                assets.Add(new Asset(name, url!, size));
            }
        }

        return new Release(version, tag!, assets);
    }

    /// <summary>Só aceita asset servido pela API deste repositório, em HTTPS.</summary>
    internal static bool IsTrustedAssetUrl(string? url) =>
        url is not null &&
        url.StartsWith(AppInfo.ApiAssetsPrefix, StringComparison.Ordinal) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host == "api.github.com";

    /// <summary>
    /// Extrai de um <c>SHA256SUMS.txt</c> o hash do arquivo pedido. Aceita o formato do coreutils
    /// ("hash  nome", com um ou dois espaços) e ignora o caminho antes do nome.
    /// </summary>
    internal static string? HashFor(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].Length != 64) continue;

            var named = Path.GetFileName(parts[1].TrimStart('*'));
            if (string.Equals(named, fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0].ToUpperInvariant();
        }

        return null;
    }

    internal static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Versão gravada no próprio executável, normalizada como a das tags.</summary>
    internal static Version? FileVersionOf(string path)
    {
        var text = FileVersionInfo.GetVersionInfo(path).FileVersion;
        return Version.TryParse(text, out var parsed) ? AppInfo.Normalize(parsed) : null;
    }

    // ------------------------------------------------------------------ troca

    /// <summary>
    /// Baixa a release, confere hash e versão e troca o executável. Não reinicia o app: quem chama
    /// decide a hora, porque a janela pode estar em uso.
    /// </summary>
    public static async Task<Outcome> InstallAsync(Release release, string currentExecutable,
        IProgress<int>? progress = null, CancellationToken token = default)
    {
        var binary = release.Find(ExecutableAsset);
        var sums = release.Find(ChecksumAsset);
        if (binary is null) return new Outcome(false, $"release {release.Tag} sem {ExecutableAsset}");
        if (sums is null) return new Outcome(false, $"release {release.Tag} sem {ChecksumAsset}");

        string? staged = null;
        try
        {
            using var http = CreateClient();

            var expected = HashFor(await DownloadTextAsync(http, sums.ApiUrl, token).ConfigureAwait(false), ExecutableAsset);
            if (expected is null) return new Outcome(false, $"{ChecksumAsset} não traz o hash de {ExecutableAsset}");

            var bytes = await DownloadBytesAsync(http, binary.ApiUrl, binary.Size, progress, token).ConfigureAwait(false);
            var actual = HashOf(bytes);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return new Outcome(false, $"SHA256 divergente: esperado {expected[..16]}…, obtido {actual[..16]}…");

            staged = WriteStaged(bytes, currentExecutable);

            if (FileVersionOf(staged) is not { } fileVersion || fileVersion != release.Version)
                return new Outcome(false, $"o executável da release {release.Tag} declara outra versão ({FileVersionOf(staged)?.ToString(3) ?? "nenhuma"})");

            Apply(staged, currentExecutable);
            staged = null;              // já foi consumido pela troca
            return new Outcome(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new Outcome(false, ex.Message);
        }
        finally
        {
            if (staged is not null) TryDelete(staged);
        }
    }

    /// <summary>
    /// Grava o binário já conferido com nome imprevisível e <see cref="FileMode.CreateNew"/>: se
    /// alguém tiver deixado um arquivo ou link com esse nome, a gravação falha em vez de segui-lo.
    /// </summary>
    internal static string WriteStaged(byte[] bytes, string currentExecutable)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(currentExecutable)) ?? ".";
        var staged = Path.Combine(dir, StagingPrefix + Path.GetRandomFileName() + ".new");
        using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(bytes);
        return staged;
    }

    /// <summary>
    /// Troca o executável numa chamada só, pelo <c>ReplaceFile</c> do Windows, que funciona com o
    /// programa em execução (testado) e guarda o anterior como <c>.old</c>. Na falha documentada em
    /// que o substituído já saiu do lugar e o novo não entrou, o anterior volta na hora — nunca fica
    /// o caminho vazio. Queda de energia no meio da própria chamada é o único caso não coberto.
    /// Erro ao copiar os metadados do executável atual — inclusive a ACL — <b>falha</b> a troca em
    /// vez de ser ignorado: um binário novo não pode entrar com permissões mais frouxas que o velho.
    /// </summary>
    internal static void Apply(string staged, string currentExecutable)
    {
        var old = currentExecutable + ".old";
        TryDelete(old);

        if (!File.Exists(currentExecutable))
        {
            File.Move(staged, currentExecutable);
            return;
        }

        try
        {
            File.Replace(staged, currentExecutable, old, ignoreMetadataErrors: false);
        }
        catch (IOException)
        {
            if (!File.Exists(currentExecutable) && File.Exists(old)) File.Move(old, currentExecutable);
            throw;
        }
    }

    /// <summary>
    /// Remove restos de trocas anteriores: o <c>.old</c> e os arquivos de espera deste app
    /// (<see cref="StagingPrefix"/>) deixados por um download interrompido. Chamado na
    /// inicialização, quando o app já provou que sobe.
    /// </summary>
    public static void CleanupOld(string currentExecutable)
    {
        TryDelete(currentExecutable + ".old");

        var dir = Path.GetDirectoryName(Path.GetFullPath(currentExecutable));
        if (dir is null) return;
        try
        {
            foreach (var leftover in Directory.EnumerateFiles(dir, StagingPrefix + "*.new")) TryDelete(leftover);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Inicia a versão recém-instalada. Quem chama encerra a atual em seguida.</summary>
    public static bool Restart(string currentExecutable)
    {
        try
        {
            Process.Start(new ProcessStartInfo(currentExecutable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(currentExecutable) ?? "",
            });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Caminho do executável em execução — funciona também no publish de arquivo único.</summary>
    public static string CurrentExecutable() =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExecutableAsset);

    // ------------------------------------------------------------------ rede

    private static async Task<string> DownloadTextAsync(HttpClient http, string url, CancellationToken token)
    {
        using var request = AssetRequest(url);
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    private static async Task<byte[]> DownloadBytesAsync(HttpClient http, string url,
        long expectedSize, IProgress<int>? progress, CancellationToken token)
    {
        using var request = AssetRequest(url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedSize;
        if (total > MaxDownloadBytes) throw new InvalidDataException($"asset de {total} bytes passa do teto");

        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(total > 0 ? (int)total : 0);

        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxDownloadBytes) throw new InvalidDataException("asset passou do teto durante o download");
            if (total > 0) progress?.Report((int)Math.Min(100, buffer.Length * 100 / total));
        }

        return buffer.ToArray();
    }

    /// <summary>Pedido de asset: o <c>octet-stream</c> é o que faz a API devolver o binário, e não o JSON.</summary>
    private static HttpRequestMessage AssetRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return request;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Gaugely");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
