using RateTray.Configuration;
using RateTray.Providers;

namespace RateTray.Tests;

/// <summary>
/// Quando o servidor recusa a renovação (invalid_grant), o próprio Claude Code grava no arquivo
/// refreshToken:"", accessToken:"" e expiresAt:0. Os testes usam só dados falsos e não vão à rede.
/// </summary>
public class ForkAuthSempreConectadoTests
{
    private static long Daqui(TimeSpan t) => DateTimeOffset.UtcNow.Add(t).ToUnixTimeMilliseconds();

    [Fact]
    public void Arquivo_limpo_pelo_Claude_Code_nao_tem_login_nem_como_renovar()
    {
        var c = new ClaudeUsageProvider.Credentials("", "", 0);

        Assert.False(c.IsExpired);
        Assert.False(c.HasAccessToken);
        Assert.True(c.NeedsRenew);
        Assert.False(c.CanRenew);
    }

    [Fact]
    public void Renova_antes_de_vencer_e_nao_antes_da_margem()
    {
        Assert.True(new ClaudeUsageProvider.Credentials("a", "r", Daqui(TimeSpan.FromMinutes(9))).NeedsRenew);
        Assert.False(new ClaudeUsageProvider.Credentials("a", "r", Daqui(TimeSpan.FromMinutes(11))).NeedsRenew);
        Assert.False(new ClaudeUsageProvider.Credentials("a", "r", 0).NeedsRenew);
    }

    [Fact]
    public async Task Arquivo_limpo_vira_nao_conectado_sem_ir_a_rede()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{ "claudeAiOauth": { "accessToken": "", "refreshToken": "", "expiresAt": 0 } }""");
            var provider = new ClaudeUsageProvider(new ClaudeOptions
            {
                CredentialsPath = path,
                UsageUrl = "http://usage.example.com/nunca",
                AutoRefreshToken = true,
            });

            var result = await provider.ReadAsync(CancellationToken.None);

            Assert.False(result.Auth!.IsValid);
            Assert.DoesNotContain("http://", result.Error ?? "");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Renovacao_do_Codex_ignora_outras_linhas_e_le_o_erro()
    {
        const string ok = """
            {"id":1,"result":{}}
            aviso que nao e JSON

            {"id":3,"result":{}}
            """;
        const string erro = """{"id":3,"error":{"message":"refresh token morto"}}""";

        Assert.Equal((true, null), await CodexUsageProvider.EsperarRenovacaoAsync(new StringReader(ok), CancellationToken.None));
        Assert.Equal((true, "refresh token morto"), await CodexUsageProvider.EsperarRenovacaoAsync(new StringReader(erro), CancellationToken.None));
        Assert.Equal((false, null), await CodexUsageProvider.EsperarRenovacaoAsync(new StringReader("""{"id":1}"""), CancellationToken.None));
    }

    [Fact]
    public void Codex_tenta_no_maximo_uma_vez_por_hora()
    {
        var agora = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        Assert.True(CodexUsageProvider.PodeTentar(null, agora));
        Assert.False(CodexUsageProvider.PodeTentar(agora.AddMinutes(-59), agora));
        Assert.True(CodexUsageProvider.PodeTentar(agora.AddHours(-1), agora));
    }

    [Fact]
    public void Codex_pede_renovacao_em_ate_48_horas_ou_vencido()
    {
        var agora = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        Assert.True(CodexUsageProvider.PrecisaRenovar(agora.AddHours(-1), agora));
        Assert.True(CodexUsageProvider.PrecisaRenovar(agora.AddHours(48), agora));
        Assert.False(CodexUsageProvider.PrecisaRenovar(agora.AddHours(49), agora));
        Assert.False(CodexUsageProvider.PrecisaRenovar(null, agora));
    }
}
