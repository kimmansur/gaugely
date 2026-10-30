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

    [Theory]
    [InlineData("", 1000L)]
    [InlineData("a", 0L)]
    public void Gravacao_recusa_credencial_incompleta_e_nao_toca_no_arquivo(string access, long expira)
    {
        var path = Path.GetTempFileName();
        try
        {
            const string original = """{ "claudeAiOauth": { "accessToken": "bom", "refreshToken": "r", "expiresAt": 5 } }""";
            File.WriteAllText(path, original);

            Assert.Throws<InvalidOperationException>(() =>
                ClaudeUsageProvider.WriteCredentials(path, new ClaudeUsageProvider.Credentials(access, "r2", expira)));

            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
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
