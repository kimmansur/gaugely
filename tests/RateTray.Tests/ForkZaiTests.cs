using RateTray.Localization;
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
    public void Cota_de_ferramentas_e_unidade_desconhecida_ficam_de_fora()
    {
        var json = """
        { "success": true, "data": { "limits": [
          { "type": "TIME_LIMIT", "unit": 5, "number": 1, "usage": 100, "currentValue": 99 },
          { "type": "CREDIT_LIMIT", "unit": 9, "number": 1, "usage": 100, "currentValue": 50 },
          { "type": "CREDIT_LIMIT", "unit": 1, "number": 30, "usage": 100, "currentValue": 0 } ] } }
        """;

        var leituras = ZaiUsageProvider.Parse(json, out _, out _);

        var mes = Assert.Single(leituras);                    // 30 dias entra como a janela longa
        Assert.Equal("zai.week", mes.Id);
        Assert.Equal(TimeSpan.FromDays(30), mes.Window);
        Assert.NotEqual("Week", mes.Label);
    }

    [Theory]
    [InlineData("""{ "code": 1000, "msg": "Authentication Failed", "success": false }""", 1000)]
    [InlineData("""{ "code": 1001, "msg": "Authentication parameter not received in Header", "success": false }""", 1001)]
    [InlineData("""{ "code": 1302, "msg": "x", "success": false }""", 1302)]
    public void Recusa_no_corpo_volta_o_codigo(string json, int codigo)
    {
        Assert.Empty(ZaiUsageProvider.Parse(json, out var plano, out var recusa));
        Assert.Equal(codigo, recusa);
        Assert.Null(plano);
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
