using System.Text.Json;
using RateTray.Configuration;

namespace RateTray.Tests;

/// <summary>
/// Fork: atualização a partir das releases deste repositório — leitura da release, conferência do
/// SHA256 e da versão, troca do executável e migração do nome antigo. A parte de rede não entra
/// aqui; o que se testa é o que decide se um binário baixado pode ou não substituir o atual.
/// </summary>
public class ForkAutoUpdateTests
{
    private const string Asset = AppInfo.ApiAssetsPrefix;

    // ------------------------------------------------------------------ origem

    [Fact]
    public void As_versoes_saem_deste_repositorio_e_nao_do_upstream()
    {
        Assert.Contains("kimmansur/gaugely", AppInfo.ApiTagsUrl);
        Assert.Contains("kimmansur/gaugely", AppInfo.ApiLatestReleaseUrl);
        Assert.Contains("kimmansur/gaugely", AppInfo.ReleasesUrl);
        Assert.StartsWith("https://api.github.com/repos/kimmansur/gaugely/releases/assets/", AppInfo.ApiAssetsPrefix);
        Assert.Equal("https://github.com/nowrap/rate-tray", AppInfo.UpstreamUrl);
        Assert.Equal("Gaugely.exe", UpdateInstaller.ExecutableAsset);
    }

    // ------------------------------------------------------------------ release

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Release_traz_versao_tag_e_assets()
    {
        var release = UpdateInstaller.ParseRelease(Json($$"""
        {
          "tag_name": "v0.4.1",
          "assets": [
            { "name": "Gaugely.exe",     "url": "{{Asset}}1", "size": 5242880 },
            { "name": "SHA256SUMS.txt",  "url": "{{Asset}}2", "size": 78 }
          ]
        }
        """));

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 4, 1), release!.Version);
        Assert.Equal("v0.4.1", release.Tag);
        Assert.Equal(5242880, release.Find("Gaugely.exe")!.Size);
        Assert.Equal(Asset + "2", release.Find("sha256sums.txt")!.ApiUrl);
        Assert.Null(release.Find("Gaugely.zip"));
    }

    [Theory]
    [InlineData("""{ "assets": [] }""")]                                   // sem tag
    [InlineData("""{ "tag_name": "nightly", "assets": [] }""")]            // tag que não é versão
    public void Release_sem_tag_de_versao_nao_vira_candidata(string body) =>
        Assert.Null(UpdateInstaller.ParseRelease(Json(body)));

    [Fact]
    public void Asset_sem_url_ou_fora_do_repositorio_e_descartado()
    {
        var release = UpdateInstaller.ParseRelease(Json($$"""
        {
          "tag_name": "v1.0.0",
          "assets": [
            { "name": "Gaugely.exe" },
            { "name": "SHA256SUMS.txt", "url": "https://api.github.com/repos/outro/fork/releases/assets/9" },
            { "name": "LICENSE", "url": "{{Asset}}3" }
          ]
        }
        """));

        Assert.NotNull(release);
        Assert.Null(release!.Find("Gaugely.exe"));
        Assert.Null(release.Find("SHA256SUMS.txt"));   // outro repositório: nem entra na lista
        Assert.NotNull(release.Find("LICENSE"));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/outro/fork/releases/assets/1")]
    [InlineData("http://api.github.com/repos/kimmansur/gaugely/releases/assets/1")]
    [InlineData("https://api.github.com.evil.example/repos/kimmansur/gaugely/releases/assets/1")]
    [InlineData("https://objects.githubusercontent.com/qualquer")]
    [InlineData("")]
    [InlineData(null)]
    public void Url_fora_do_prefixo_do_repositorio_nao_e_confiavel(string? url) =>
        Assert.False(UpdateInstaller.IsTrustedAssetUrl(url));

    [Fact]
    public void Url_do_proprio_repositorio_e_confiavel() =>
        Assert.True(UpdateInstaller.IsTrustedAssetUrl(Asset + "582296435"));

    // ------------------------------------------------------------------ hash

    [Fact]
    public void Hash_e_lido_no_formato_do_coreutils()
    {
        const string hash = "32EEF35C4AD06F97FFFE535D208BBC70B5287B99473962C1C39D1C738CE6908F";
        Assert.Equal(hash, UpdateInstaller.HashFor($"{hash}  Gaugely.exe\n", "Gaugely.exe"));
        Assert.Equal(hash, UpdateInstaller.HashFor($"{hash} *Gaugely.exe", "Gaugely.exe"));
        Assert.Equal(hash, UpdateInstaller.HashFor($"{hash}  publish/Gaugely.exe\n", "Gaugely.exe"));
    }

    [Fact]
    public void Hash_de_outro_arquivo_nao_serve_para_o_executavel()
    {
        const string sums = "0000000000000000000000000000000000000000000000000000000000000000  outro.exe";
        Assert.Null(UpdateInstaller.HashFor(sums, "Gaugely.exe"));
    }

    [Fact]
    public void Linha_torta_nao_vira_hash()
    {
        Assert.Null(UpdateInstaller.HashFor("abc  Gaugely.exe", "Gaugely.exe"));   // curto demais
        Assert.Null(UpdateInstaller.HashFor("", "Gaugely.exe"));
    }

    [Fact]
    public void Hash_em_memoria_e_no_disco_concordam()
    {
        var bytes = "gaugely"u8.ToArray();
        var caminho = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllBytes(caminho, bytes);
        try { Assert.Equal(UpdateInstaller.HashOf(bytes), UpdateInstaller.HashOf(caminho)); }
        finally { File.Delete(caminho); }
    }

    // ------------------------------------------------------------------ troca

    private static string PastaTemporaria()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gaugely-update-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Arquivo_de_espera_tem_nome_imprevisivel_e_fica_na_pasta_do_executavel()
    {
        var dir = PastaTemporaria();
        try
        {
            var atual = Path.Combine(dir, "Gaugely.exe");
            var a = UpdateInstaller.WriteStaged([1, 2, 3], atual);
            var b = UpdateInstaller.WriteStaged([1, 2, 3], atual);

            Assert.NotEqual(a, b);
            Assert.Equal(dir, Path.GetDirectoryName(a));
            Assert.EndsWith(".new", a);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(a));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Troca_guarda_o_anterior_como_old_e_poe_o_novo_no_lugar()
    {
        var dir = PastaTemporaria();
        try
        {
            var atual = Path.Combine(dir, "Gaugely.exe");
            File.WriteAllText(atual, "versao-antiga");
            var novo = UpdateInstaller.WriteStaged("versao-nova"u8.ToArray(), atual);

            UpdateInstaller.Apply(novo, atual);

            Assert.Equal("versao-nova", File.ReadAllText(atual));
            Assert.Equal("versao-antiga", File.ReadAllText(atual + ".old"));
            Assert.False(File.Exists(novo));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Troca_sem_executavel_anterior_so_move_o_novo()
    {
        var dir = PastaTemporaria();
        try
        {
            var atual = Path.Combine(dir, "Gaugely.exe");
            var novo = UpdateInstaller.WriteStaged("v1"u8.ToArray(), atual);

            UpdateInstaller.Apply(novo, atual);

            Assert.Equal("v1", File.ReadAllText(atual));
            Assert.False(File.Exists(atual + ".old"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Troca_seguida_de_outra_nao_acumula_old()
    {
        var dir = PastaTemporaria();
        try
        {
            var atual = Path.Combine(dir, "Gaugely.exe");
            File.WriteAllText(atual, "v1");

            UpdateInstaller.Apply(UpdateInstaller.WriteStaged("v2"u8.ToArray(), atual), atual);
            UpdateInstaller.Apply(UpdateInstaller.WriteStaged("v3"u8.ToArray(), atual), atual);

            Assert.Equal("v3", File.ReadAllText(atual));
            Assert.Equal("v2", File.ReadAllText(atual + ".old"));   // só a imediatamente anterior
            Assert.Single(Directory.GetFiles(dir, "*.old"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Limpeza_apaga_old_e_downloads_interrompidos_sem_tocar_no_executavel()
    {
        var dir = PastaTemporaria();
        try
        {
            var atual = Path.Combine(dir, "Gaugely.exe");
            File.WriteAllText(atual, "v2");
            File.WriteAllText(atual + ".old", "v1");
            var resto = UpdateInstaller.WriteStaged([9], atual);

            UpdateInstaller.CleanupOld(atual);

            Assert.False(File.Exists(atual + ".old"));
            Assert.False(File.Exists(resto));
            Assert.True(File.Exists(atual));

            UpdateInstaller.CleanupOld(atual);          // idempotente
            Assert.True(File.Exists(atual));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Versao_do_arquivo_e_lida_do_proprio_executavel()
    {
        // O próprio assembly de teste tem versão; um texto qualquer não tem nenhuma.
        Assert.NotNull(UpdateInstaller.FileVersionOf(typeof(ForkAutoUpdateTests).Assembly.Location));

        var caminho = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(caminho, "não é executável");
        try { Assert.Null(UpdateInstaller.FileVersionOf(caminho)); }
        finally { File.Delete(caminho); }
    }

    [Fact]
    public void Caminho_do_executavel_em_execucao_e_absoluto()
    {
        var caminho = UpdateInstaller.CurrentExecutable();
        Assert.False(string.IsNullOrWhiteSpace(caminho));
        Assert.True(Path.IsPathRooted(caminho));
    }

    // ------------------------------------------------------------------ nome antigo

    [Fact]
    public void Ajustes_da_pasta_antiga_sao_copiados_uma_vez_sem_sobrescrever()
    {
        var raiz = PastaTemporaria();
        try
        {
            var antiga = Path.Combine(raiz, "RateTray");
            var nova = Path.Combine(raiz, "Gaugely");
            Directory.CreateDirectory(antiga);
            File.WriteAllText(Path.Combine(antiga, "settings.json"), "{ \"theme\": \"dark\" }");

            ConfigStore.MigrateLegacy(antiga, nova);
            Assert.Equal("{ \"theme\": \"dark\" }", File.ReadAllText(Path.Combine(nova, "settings.json")));
            Assert.True(File.Exists(Path.Combine(antiga, "settings.json")));   // copiado, não movido

            File.WriteAllText(Path.Combine(nova, "settings.json"), "{ \"theme\": \"light\" }");
            ConfigStore.MigrateLegacy(antiga, nova);
            Assert.Equal("{ \"theme\": \"light\" }", File.ReadAllText(Path.Combine(nova, "settings.json")));
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public void Sem_pasta_antiga_a_migracao_nao_cria_nada()
    {
        var raiz = PastaTemporaria();
        try
        {
            ConfigStore.MigrateLegacy(Path.Combine(raiz, "RateTray"), Path.Combine(raiz, "Gaugely"));
            Assert.False(Directory.Exists(Path.Combine(raiz, "Gaugely")));
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public void Chaves_tem_alvo_novo_e_o_antigo_so_para_migrar()
    {
        Assert.Equal("Gaugely/kimi", CredentialVault.TargetFor(CredentialVault.Kimi));
        Assert.Equal("Gaugely/openrouter", CredentialVault.TargetFor(CredentialVault.OpenRouter));
        Assert.Equal("RateTray-Nox/kimi", CredentialVault.LegacyTargetFor(CredentialVault.Kimi));
        Assert.Equal("RateTray-Nox/openrouter", CredentialVault.LegacyTargetFor(CredentialVault.OpenRouter));
    }
}
