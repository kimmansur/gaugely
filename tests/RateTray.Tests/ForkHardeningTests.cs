using RateTray.Configuration;

namespace RateTray.Tests;

/// <summary>
/// As três travas do fork. O upstream trata o settings.json como superfície de configuração
/// legítima; numa máquina de trabalho, esse arquivo é superfície de ataque — quem escreve nele
/// desviaria o token de acesso ou faria o app disparar outro executável. Tudo é barrado no
/// <c>Normalize()</c>, que é por onde toda configuração carregada do disco passa.
/// </summary>
public class ForkHardeningTests
{
    [Fact]
    public void FORK1_usage_url_de_outro_host_volta_ao_oficial()
    {
        var config = ConfigStore.FromJson("""
        { "claude": { "usageUrl": "https://usage.exemplo-atacante.com/api/oauth/usage" } }
        """);

        Assert.Equal(ClaudeOptions.UsageUrlDefault, config.Claude.UsageUrl);
    }

    [Fact]
    public void FORK1_token_url_de_outro_host_volta_ao_oficial()
    {
        var config = ConfigStore.FromJson("""
        { "claude": { "tokenUrl": "https://token.exemplo-atacante.com/v1/oauth/token" } }
        """);

        Assert.Equal(ClaudeOptions.TokenUrlDefault, config.Claude.TokenUrl);
    }

    [Fact]
    public void FORK1_caminho_diferente_no_host_oficial_continua_valendo()
    {
        // Versão nova do mesmo endpoint não é redirecionamento: só o host é fixado.
        var config = ConfigStore.FromJson("""
        { "claude": { "usageUrl": "https://api.anthropic.com/api/oauth/usage_v2" } }
        """);

        Assert.Equal("https://api.anthropic.com/api/oauth/usage_v2", config.Claude.UsageUrl);
    }

    [Fact]
    public void FORK2_executavel_do_codex_do_arquivo_de_ajustes_e_ignorado()
    {
        var config = ConfigStore.FromJson("""
        { "codex": { "executablePath": "C:\\Users\\Public\\payload.exe" } }
        """);

        Assert.Null(config.Codex.ExecutablePath);
    }

    [Fact]
    public void FORK3_renovacao_automatica_e_escolha_do_usuario_mas_o_destino_segue_travado()
    {
        // Revisto: a renovação pode ligar, mas o refresh token só vai ao host oficial.
        var config = ConfigStore.FromJson("""
        { "claude": { "autoRefreshToken": true, "tokenUrl": "https://token.exemplo-atacante.com/v1/oauth/token" } }
        """);

        Assert.True(config.Claude.AutoRefreshToken);
        Assert.Equal(ClaudeOptions.TokenUrlDefault, config.Claude.TokenUrl);
    }
}
