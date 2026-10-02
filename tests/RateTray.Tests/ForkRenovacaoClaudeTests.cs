using System.Text.Json.Nodes;
using RateTray.Configuration;
using RateTray.Providers;

namespace RateTray.Tests;

/// <summary>
/// Renovação do token do Claude. Três furos medidos no PC em 02/10/2026: a validade do refresh
/// token novo não era gravada, a renovação ignorava a trava do Claude Code e o endereço de token
/// era o antigo. Os testes não vão à rede: cobrem a leitura da resposta, a gravação do arquivo e
/// a trava.
/// </summary>
public class ForkRenovacaoClaudeTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 2, 16, 0, 0, TimeSpan.Zero);

    private static ClaudeUsageProvider.Credentials Anterior() =>
        new("velho", "refresh-velho", Agora.AddHours(-1).ToUnixTimeMilliseconds())
        {
            RefreshExpiresAtUnixMs = Agora.AddDays(12).ToUnixTimeMilliseconds(),
            SubscriptionType = "max",
        };

    [Fact]
    public void Validade_do_refresh_token_novo_vem_da_resposta()
    {
        var novo = ClaudeUsageProvider.FromTokenResponse(
            """{ "access_token": "a2", "refresh_token": "r2", "expires_in": 28800, "refresh_token_expires_in": 2592000 }""",
            Anterior(), Agora);

        Assert.NotNull(novo);
        Assert.Equal("a2", novo.AccessToken);
        Assert.Equal("r2", novo.RefreshToken);
        Assert.Equal(Agora.AddHours(8).ToUnixTimeMilliseconds(), novo.ExpiresAtUnixMs);
        Assert.Equal(Agora.AddDays(30).ToUnixTimeMilliseconds(), novo.RefreshExpiresAtUnixMs);
        Assert.Equal("max", novo.SubscriptionType);
    }

    [Fact]
    public void Sem_validade_na_resposta_mantem_a_anterior_como_o_claude_code()
    {
        var novo = ClaudeUsageProvider.FromTokenResponse(
            """{ "access_token": "a2", "refresh_token": "r2", "expires_in": 3600 }""", Anterior(), Agora);

        Assert.Equal(Anterior().RefreshExpiresAtUnixMs, novo!.RefreshExpiresAtUnixMs);
    }

    [Fact]
    public void Resposta_sem_refresh_token_mantem_o_anterior()
    {
        var novo = ClaudeUsageProvider.FromTokenResponse("""{ "access_token": "a2" }""", Anterior(), Agora);

        Assert.Equal("refresh-velho", novo!.RefreshToken);
        Assert.Equal(Agora.AddHours(1).ToUnixTimeMilliseconds(), novo.ExpiresAtUnixMs);
    }

    [Theory]
    [InlineData("""{ "refresh_token": "r2" }""")]
    [InlineData("""{ "access_token": "" }""")]
    public void Resposta_sem_access_token_nao_renova(string json)
    {
        Assert.Null(ClaudeUsageProvider.FromTokenResponse(json, Anterior(), Agora));
    }

    [Fact]
    public void Gravacao_preserva_os_outros_campos_e_grava_a_validade_nova()
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"gaugely-creds-{Guid.NewGuid():N}.json");
        File.WriteAllText(arquivo, """
            { "claudeAiOauth": { "accessToken": "velho", "refreshToken": "refresh-velho", "expiresAt": 1,
                                 "refreshTokenExpiresAt": 2, "scopes": ["user:inference"], "subscriptionType": "max",
                                 "rateLimitTier": "default_claude_max_20x" },
              "mcpOAuth": { "x": 1 } }
            """);

        try
        {
            var novo = ClaudeUsageProvider.FromTokenResponse(
                """{ "access_token": "a2", "refresh_token": "r2", "expires_in": 60, "refresh_token_expires_in": 120 }""",
                Anterior(), Agora)!;
            ClaudeUsageProvider.WriteCredentials(arquivo, novo);

            var raiz = JsonNode.Parse(File.ReadAllText(arquivo))!.AsObject();
            var oauth = raiz["claudeAiOauth"]!.AsObject();
            Assert.Equal("a2", oauth["accessToken"]!.GetValue<string>());
            Assert.Equal("r2", oauth["refreshToken"]!.GetValue<string>());
            Assert.Equal(Agora.AddSeconds(120).ToUnixTimeMilliseconds(), oauth["refreshTokenExpiresAt"]!.GetValue<long>());
            Assert.Equal("default_claude_max_20x", oauth["rateLimitTier"]!.GetValue<string>());
            Assert.Equal("user:inference", oauth["scopes"]![0]!.GetValue<string>());
            Assert.Equal(1, raiz["mcpOAuth"]!["x"]!.GetValue<int>());
            Assert.False(File.Exists(arquivo + ".tmp"));
        }
        finally
        {
            File.Delete(arquivo);
        }
    }

    [Fact]
    public void Endereco_de_token_e_o_do_claude_code_e_o_antigo_salvo_migra()
    {
        Assert.Equal("https://platform.claude.com/v1/oauth/token", ClaudeOptions.TokenUrlDefault);

        // O settings.json do PC guarda o endereço antigo por extenso; a FORK-1 o leva ao novo.
        var config = ConfigStore.FromJson("""
        { "claude": { "autoRefreshToken": true, "tokenUrl": "https://console.anthropic.com/v1/oauth/token" } }
        """);
        Assert.Equal(ClaudeOptions.TokenUrlDefault, config.Claude.TokenUrl);
    }

    [Fact]
    public async Task Trava_e_o_diretorio_que_o_claude_code_usa_e_e_exclusiva()
    {
        var pasta = Path.Combine(Path.GetTempPath(), $"gaugely-claude-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pasta);
        var trava = pasta + ".lock";

        try
        {
            using (var primeira = await RefreshLock.AcquireAsync(pasta, CancellationToken.None))
            {
                Assert.NotNull(primeira);
                Assert.True(Directory.Exists(trava));

                var segunda = await RefreshLock.AcquireAsync(pasta, CancellationToken.None, TimeSpan.FromMilliseconds(300));
                Assert.Null(segunda);
            }

            Assert.False(Directory.Exists(trava));
        }
        finally
        {
            if (Directory.Exists(trava)) Directory.Delete(trava);
            Directory.Delete(pasta);
        }
    }

    [Fact]
    public async Task Trava_abandonada_e_tomada()
    {
        var pasta = Path.Combine(Path.GetTempPath(), $"gaugely-claude-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pasta);
        var trava = pasta + ".lock";
        Directory.CreateDirectory(trava);
        Directory.SetLastWriteTimeUtc(trava, DateTime.UtcNow - TimeSpan.FromMinutes(1));

        try
        {
            using var tomada = await RefreshLock.AcquireAsync(pasta, CancellationToken.None, TimeSpan.FromMilliseconds(300));
            Assert.NotNull(tomada);
        }
        finally
        {
            if (Directory.Exists(trava)) Directory.Delete(trava);
            Directory.Delete(pasta);
        }
    }

    [Fact]
    public async Task Trava_recente_de_outro_processo_e_respeitada()
    {
        var pasta = Path.Combine(Path.GetTempPath(), $"gaugely-claude-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pasta);
        var trava = pasta + ".lock";
        Directory.CreateDirectory(trava);

        try
        {
            Assert.Null(await RefreshLock.AcquireAsync(pasta, CancellationToken.None, TimeSpan.FromMilliseconds(300)));
            Assert.True(Directory.Exists(trava));   // não é nossa: fica onde está
        }
        finally
        {
            Directory.Delete(trava);
            Directory.Delete(pasta);
        }
    }

    [Fact]
    public async Task Trava_segura_tem_a_data_renovada_e_nao_parece_abandonada()
    {
        var pasta = Path.Combine(Path.GetTempPath(), $"gaugely-claude-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pasta);
        var trava = pasta + ".lock";

        try
        {
            using var nossa = await RefreshLock.AcquireAsync(pasta, CancellationToken.None, touch: TimeSpan.FromMilliseconds(100));
            Assert.NotNull(nossa);
            var antes = Directory.GetLastWriteTimeUtc(trava);

            await Task.Delay(600);

            Assert.True(Directory.GetLastWriteTimeUtc(trava) > antes);
            Assert.True(nossa.Held);
        }
        finally
        {
            if (Directory.Exists(trava)) Directory.Delete(trava);
            Directory.Delete(pasta);
        }
    }

    [Fact]
    public async Task Trava_tomada_por_outro_nao_e_renovada_nem_apagada()
    {
        var pasta = Path.Combine(Path.GetTempPath(), $"gaugely-claude-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pasta);
        var trava = pasta + ".lock";

        try
        {
            var nossa = await RefreshLock.AcquireAsync(pasta, CancellationToken.None, touch: TimeSpan.FromMilliseconds(100));
            Assert.NotNull(nossa);

            // O Claude Code julgou a nossa abandonada, apagou e criou a dele.
            Directory.Delete(trava);
            Directory.CreateDirectory(trava);
            var deles = DateTime.UtcNow - TimeSpan.FromSeconds(3);
            Directory.SetLastWriteTimeUtc(trava, deles);

            await Task.Delay(400);
            Assert.False(nossa.Held);
            Assert.Equal(deles, Directory.GetLastWriteTimeUtc(trava));   // não tocamos na data dele

            nossa.Dispose();
            Assert.True(Directory.Exists(trava));                          // nem apagamos a trava dele
        }
        finally
        {
            if (Directory.Exists(trava)) Directory.Delete(trava);
            Directory.Delete(pasta);
        }
    }

    [Fact]
    public async Task Trava_abandonada_com_conteudo_tambem_e_tomada()
    {
        var pasta = Path.Combine(Path.GetTempPath(), $"gaugely-claude-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pasta);
        var trava = pasta + ".lock";
        Directory.CreateDirectory(trava);
        File.WriteAllText(Path.Combine(trava, "pid"), "123");
        Directory.SetLastWriteTimeUtc(trava, DateTime.UtcNow - TimeSpan.FromMinutes(1));

        try
        {
            using var tomada = await RefreshLock.AcquireAsync(pasta, CancellationToken.None, TimeSpan.FromMilliseconds(300));
            Assert.NotNull(tomada);
        }
        finally
        {
            if (Directory.Exists(trava)) Directory.Delete(trava, recursive: true);
            Directory.Delete(pasta);
        }
    }
}
