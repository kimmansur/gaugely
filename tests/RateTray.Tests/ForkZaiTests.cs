using System.Net;
using RateTray.Localization;
using RateTray.Model;
using RateTray.Providers;

namespace RateTray.Tests;

/// <summary>
/// Fork: parse do medidor do GLM Coding Plan (<c>/api/monitor/usage/quota/limit</c>). O formato é
/// o de uma resposta real do plano Lite conferida em 13/10/2026, com os números trocados. Nada aqui
/// toca o Gerenciador de Credenciais nem a rede.
/// </summary>
public class ForkZaiTests
{
    public ForkZaiTests() => Loc.Use("en");

    private const string Lite = """
    {
      "code": 200, "msg": "Operation successful", "success": true,
      "data": {
        "limits": [
          { "type": "CREDIT_LIMIT", "unit": 3, "number": 5, "usage": 2000, "currentValue": 500,
            "remaining": 1500, "percentage": 25, "nextResetTime": 1791914154068 },
          { "type": "CREDIT_LIMIT", "unit": 6, "number": 1, "usage": 10000, "currentValue": 422,
            "remaining": 9578, "percentage": 4, "nextResetTime": 1792364568983 }
        ],
        "level": "lite"
      }
    }
    """;

    [Fact]
    public void Plano_lite_vira_janela_de_5h_e_semana()
    {
        var leituras = ZaiUsageProvider.Parse(Lite, out var plano, out var recusa);

        Assert.Null(recusa);
        Assert.Equal("Lite", plano);
        Assert.Equal(["zai.5h", "zai.week"], leituras.Select(r => r.Id));

        var curta = leituras[0];
        Assert.Equal("Z.ai", curta.Group);
        Assert.Equal(25, curta.Percent, 3);
        Assert.Equal(TimeSpan.FromHours(5), curta.Window);
        Assert.True(curta.IsActive);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791914154068), curta.ResetsAt);

        var semana = leituras[1];
        Assert.Equal(4.22, semana.Percent, 3);              // do par teto/consumido, não do 4 arredondado
        Assert.Equal(TimeSpan.FromDays(7), semana.Window);
        Assert.Equal("Week", semana.Label);
        Assert.Equal((0, 2), (curta.Variant, curta.VariantCount));
        Assert.Equal((1, 2), (semana.Variant, semana.VariantCount));
    }

    [Fact]
    public void Ordem_do_array_nao_troca_os_ids()
    {
        var invertido = """
        { "success": true, "data": { "limits": [
          { "type": "CREDIT_LIMIT", "unit": 6, "number": 1, "usage": 10000, "currentValue": 1000 },
          { "type": "CREDIT_LIMIT", "unit": 3, "number": 5, "usage": 2000, "currentValue": 100 } ] } }
        """;

        var leituras = ZaiUsageProvider.Parse(invertido, out _, out _);

        Assert.Equal(["zai.5h", "zai.week"], leituras.Select(r => r.Id));
        Assert.Equal(5, leituras[0].Percent, 3);
        Assert.Equal(10, leituras[1].Percent, 3);
    }

    [Fact]
    public void Sem_consumido_usa_o_restante_e_depois_o_percentual()
    {
        var json = """
        { "success": true, "data": { "limits": [
          { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "usage": "2000", "remaining": "1000" },
          { "type": "TOKENS_LIMIT", "unit": 6, "number": 1, "percentage": 140 } ] } }
        """;

        var leituras = ZaiUsageProvider.Parse(json, out _, out _);

        Assert.Equal(50, leituras.Single(r => r.Id == "zai.5h").Percent, 3);
        Assert.Equal(100, leituras.Single(r => r.Id == "zai.week").Percent, 3);   // limitado a 100
    }

    [Fact]
    public void So_entram_5h_e_1_semana_outras_janelas_e_tipos_ficam_de_fora()
    {
        // Um id salvo na lista de ícones não pode mudar de sentido: 1 dia, 30 dias, 4 h, a cota
        // de ferramentas e unidade desconhecida não viram "zai.5h" nem "zai.week".
        var json = """
        { "success": true, "data": { "limits": [
          { "type": "TIME_LIMIT", "unit": 3, "number": 5, "usage": 100, "currentValue": 99 },
          { "type": "CREDIT_LIMIT", "unit": 9, "number": 5, "usage": 100, "currentValue": 50 },
          { "type": "CREDIT_LIMIT", "unit": 1, "number": 30, "usage": 100, "currentValue": 0 },
          { "type": "CREDIT_LIMIT", "unit": 1, "number": 1, "usage": 100, "currentValue": 0 },
          { "type": "CREDIT_LIMIT", "unit": 3, "number": 4, "usage": 100, "currentValue": 0 } ] } }
        """;

        Assert.Empty(ZaiUsageProvider.Parse(json, out _, out var recusa));
        Assert.Null(recusa);
    }

    [Fact]
    public void Numero_absurdo_nao_derruba_as_leituras_boas()
    {
        var json = """
        { "success": true, "data": { "limits": [
          { "type": "CREDIT_LIMIT", "unit": 3, "number": "1e300", "usage": 100, "currentValue": 1 },
          { "type": "CREDIT_LIMIT", "unit": 3, "number": 5, "usage": 2000, "currentValue": 200, "nextResetTime": 1e300 },
          { "type": "CREDIT_LIMIT", "unit": 6, "number": 1, "usage": 10000, "currentValue": 100, "nextResetTime": "9e18" } ] } }
        """;

        var leituras = ZaiUsageProvider.Parse(json, out _, out _);

        Assert.Equal(["zai.5h", "zai.week"], leituras.Select(r => r.Id));
        Assert.Equal(10, leituras[0].Percent, 3);
        Assert.All(leituras, r => Assert.Null(r.ResetsAt));
    }

    [Theory]
    [InlineData("""{ "code": 1000, "msg": "Authentication Failed", "success": false }""", "1000")]
    [InlineData("""{ "code": 1001, "msg": "Authentication parameter not received in Header", "success": false }""", "1001")]
    [InlineData("""{ "code": 1302, "msg": "x", "success": false }""", "1302")]
    [InlineData("""{ "code": 1000, "success": "false" }""", "1000")]
    [InlineData("""{ "msg": "x", "success": null }""", "?")]
    [InlineData("""{ "code": "abc", "data": { "limits": [] } }""", "?")]
    public void Recusa_no_corpo_volta_o_codigo(string json, string codigo)
    {
        Assert.Empty(ZaiUsageProvider.Parse(json, out var plano, out var recusa));
        Assert.Equal(codigo, recusa);
        Assert.Null(plano);
    }

    // ------------------------------------------------------------- resposta inteira (sem rede)

    private static readonly AuthStatus Valida = new() { Group = "Z.ai", IsValid = true, Detail = "GLM Coding Plan" };

    private static ProviderResult Com(HttpStatusCode status, string? corpo)
    {
        using var resposta = new HttpResponseMessage(status);
        return ZaiUsageProvider.Interpret(resposta, corpo, Valida);
    }

    [Fact]
    public void Chave_recusada_com_http_200_marca_a_chave_como_invalida()
    {
        var r = Com(HttpStatusCode.OK, """{ "code": 1000, "msg": "Authentication Failed", "success": false }""");

        Assert.False(r.Ok);
        Assert.False(r.Auth!.IsValid);
        Assert.Equal("Z.ai rejected the key", r.Error);
    }

    [Fact]
    public void Outra_recusa_no_corpo_nao_condena_a_chave()
    {
        var r = Com(HttpStatusCode.OK, """{ "code": 1302, "msg": "x", "success": false }""");

        Assert.False(r.Ok);
        Assert.True(r.Auth!.IsValid);
        Assert.Equal("Z.ai refused the request (code 1302)", r.Error);
    }

    [Fact]
    public void Http_401_marca_a_chave_e_429_pede_espera()
    {
        Assert.False(Com(HttpStatusCode.Unauthorized, null).Auth!.IsValid);

        var limitado = Com(HttpStatusCode.TooManyRequests, null);
        Assert.True(limitado.RateLimited);
        Assert.True(limitado.Auth!.IsValid);
    }

    [Fact]
    public void Erro_do_servidor_e_redirecionamento_viram_falha_http()
    {
        Assert.Equal("HTTP 502 from Z.ai", Com(HttpStatusCode.BadGateway, null).Error);
        Assert.Equal("HTTP 302 from Z.ai", Com(HttpStatusCode.Found, null).Error);
    }

    [Fact]
    public void Resposta_boa_traz_as_leituras_e_o_plano()
    {
        var r = Com(HttpStatusCode.OK, Lite);

        Assert.True(r.Ok);
        Assert.Equal(2, r.Readings.Count);
        Assert.Equal("GLM Coding Plan · Lite", r.Auth!.Detail);
    }

    [Fact]
    public void Sem_janelas_conhecidas_avisa_em_vez_de_ficar_em_branco()
    {
        var r = Com(HttpStatusCode.OK, """{ "success": true, "data": { "limits": [] } }""");

        Assert.False(r.Ok);
        Assert.Equal("Z.ai reported no limits", r.Error);
    }

    [Theory]
    [InlineData("<html>502</html>")]
    [InlineData("""{ "success": true, "data": { "limits": [] } }""")]
    [InlineData("""{ "success": true }""")]
    public void Resposta_vazia_ou_estranha_nao_inventa_leitura(string json)
    {
        Assert.Empty(ZaiUsageProvider.Parse(json, out _, out var recusa));
        Assert.Null(recusa);
    }
}
