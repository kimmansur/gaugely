using RateTray.Localization;
using RateTray.Model;
using RateTray.Providers;

namespace RateTray.Tests;

/// <summary>
/// Fork: parse do <c>/credits</c> e do <c>/key</c> do OpenRouter. Formatos de respostas
/// reais conferidas no mesmo dia, com números sintéticos. Sem rede e sem Gerenciador de Credenciais.
/// </summary>
public class ForkOpenRouterTests
{
    public ForkOpenRouterTests() => Loc.Use("en");

    private const string Credits = """
    { "data": { "total_credits": 25, "total_usage": 7.5 } }
    """;

    private const string KeySemTeto = """
    { "data": { "limit": null, "limit_remaining": null, "limit_reset": null,
                "usage": 7.5, "usage_daily": 0.25, "usage_weekly": 1.75, "usage_monthly": 7.5 } }
    """;

    private static LimitReading Id(IEnumerable<LimitReading> leituras, string id) => leituras.Single(r => r.Id == id);

    [Fact]
    public void Saldo_e_credito_menos_uso()
    {
        var saldo = Id(OpenRouterUsageProvider.Parse(Credits, null), "openrouter.balance");

        Assert.Equal(17.5m, saldo.Amount);
        Assert.Equal("US$", saldo.AmountUnit);
        Assert.True(saldo.IsInformational);
        Assert.Equal("17", saldo.IconText);
        Assert.Equal("OpenRouter", saldo.Group);
    }

    [Fact]
    public void Saldo_nunca_fica_negativo()
    {
        const string estourado = """{ "data": { "total_credits": 10, "total_usage": 10.03 } }""";

        var saldo = Id(OpenRouterUsageProvider.Parse(estourado, null), "openrouter.balance");

        Assert.Equal(0m, saldo.Amount);
        Assert.Equal("0", saldo.IconText);
    }

    [Fact]
    public void Numeros_em_string_tambem_valem()
    {
        const string texto = """{ "data": { "total_credits": "12.50", "total_usage": "2.25" } }""";

        Assert.Equal(10.25m, Id(OpenRouterUsageProvider.Parse(texto, null), "openrouter.balance").Amount);
    }

    [Fact]
    public void Chave_sem_teto_nao_gera_key_limit()
    {
        var leituras = OpenRouterUsageProvider.Parse(Credits, KeySemTeto);

        Assert.Equal(
            ["openrouter.balance", "openrouter.spend_today", "openrouter.spend_week", "openrouter.spend_month"],
            leituras.Select(r => r.Id));
        Assert.All(leituras, r => Assert.True(r.IsInformational));
        Assert.Equal(0.25m, Id(leituras, "openrouter.spend_today").Amount);
        Assert.Equal(1.75m, Id(leituras, "openrouter.spend_week").Amount);
        Assert.Equal(7.5m, Id(leituras, "openrouter.spend_month").Amount);
    }

    [Fact]
    public void Chave_com_teto_gera_percentual_do_teto()
    {
        const string comTeto = """
        { "data": { "limit": 5, "limit_remaining": 3.75, "limit_reset": "weekly",
                    "usage": 1.25, "usage_daily": 0.5, "usage_weekly": 1.25, "usage_monthly": 1.25 } }
        """;

        var teto = Id(OpenRouterUsageProvider.Parse(Credits, comTeto), "openrouter.key_limit");

        Assert.False(teto.IsInformational);
        Assert.Null(teto.Amount);
        Assert.Equal(25, teto.Percent, 3);
        Assert.Equal(TimeSpan.FromDays(7), teto.Window);
        Assert.NotNull(teto.Note);
    }

    [Fact]
    public void Restante_acima_do_teto_ou_negativo_fica_dentro_da_faixa()
    {
        const string negativo = """{ "data": { "limit": 5, "limit_remaining": -1 } }""";
        const string acima = """{ "data": { "limit": 5, "limit_remaining": 9 } }""";

        Assert.Equal(100, Id(OpenRouterUsageProvider.Parse(null, negativo), "openrouter.key_limit").Percent, 3);
        Assert.Equal(0, Id(OpenRouterUsageProvider.Parse(null, acima), "openrouter.key_limit").Percent, 3);
    }

    [Fact]
    public void Credits_falhou_mas_key_ok_ainda_gera_gastos()
    {
        var leituras = OpenRouterUsageProvider.Parse(null, KeySemTeto);

        Assert.DoesNotContain(leituras, r => r.Id == "openrouter.balance");
        Assert.Equal(3, leituras.Count(r => r.Id.StartsWith("openrouter.spend_", StringComparison.Ordinal)));
    }

    [Fact]
    public void As_duas_nulas_dao_lista_vazia()
    {
        Assert.Empty(OpenRouterUsageProvider.Parse(null, null));
    }

    [Fact]
    public void Resposta_ilegivel_conta_como_ausente()
    {
        Assert.Empty(OpenRouterUsageProvider.Parse("<html>", "{\"error\":{\"code\":401}}"));
    }

    [Fact]
    public void Servico_so_informativo_mostra_o_saldo_no_icone()
    {
        var leituras = OpenRouterUsageProvider.Parse(Credits, KeySemTeto);

        var resumo = ServiceGroup.Summary("OpenRouter", ServiceGroup.Ordered(leituras))!;

        Assert.Equal(17.5m, resumo.Amount);
        Assert.Equal("grupo.openrouter", resumo.Id);
    }
}
