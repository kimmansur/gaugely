using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;
using RateTray.Providers;

namespace RateTray.Tests;

/// <summary>
/// Fork: <c>/credits</c> e <c>/key</c> da Experiential Labs. Formato de respostas reais conferidas
/// em 2026-10-31, com números sintéticos. Sem rede e sem Gerenciador de Credenciais.
/// </summary>
public class ForkExperientialTests
{
    public ForkExperientialTests() => Loc.Use("en");

    private const string Credits = """{ "data": { "total_credits": 25.0, "total_usage": 7.5 } }""";

    private const string Key = """
    { "data": { "label": "teste", "usage": 7.5, "limit": null, "limit_remaining": null, "is_free_tier": false,
                "usage_daily": 0.25, "usage_weekly": 1.75, "usage_monthly": 7.5 } }
    """;

    private static LimitReading Id(IEnumerable<LimitReading> leituras, string id) => leituras.Single(r => r.Id == id);

    [Fact]
    public void Saldo_e_credito_menos_uso_e_so_informa()
    {
        var saldo = Id(ExperientialUsageProvider.ParseForTest(Credits, null), "experiential.balance");

        Assert.Equal(17.5m, saldo.Amount);
        Assert.Equal("US$", saldo.AmountUnit);
        Assert.Equal("Experiential", saldo.Group);
        Assert.True(saldo.IsInformational);
    }

    [Fact]
    public void Saldo_nunca_fica_negativo()
    {
        const string estourado = """{ "data": { "total_credits": 10, "total_usage": 10.03 } }""";

        Assert.Equal(0m, Id(ExperientialUsageProvider.ParseForTest(estourado, null), "experiential.balance").Amount);
    }

    [Fact]
    public void Gasto_sai_por_periodo()
    {
        var leituras = ExperientialUsageProvider.ParseForTest(Credits, Key);

        Assert.Equal(0.25m, Id(leituras, "experiential.spend_today").Amount);
        Assert.Equal(1.75m, Id(leituras, "experiential.spend_week").Amount);
        Assert.Equal(7.5m, Id(leituras, "experiential.spend_month").Amount);
    }

    [Fact]
    public void As_duas_respostas_somam_quatro_leituras()
    {
        Assert.Equal(4, ExperientialUsageProvider.ParseForTest(Credits, Key).Count);
    }

    [Theory]
    [InlineData("{ nao e json")]
    [InlineData("""{ "error": "Unauthorized" }""")]
    [InlineData("""{ "data": { "total_credits": 25 } }""")]
    public void Resposta_sem_os_dois_numeros_nao_vira_saldo(string credits)
    {
        Assert.Empty(ExperientialUsageProvider.ParseForTest(credits, null));
    }

    [Fact]
    public void Sem_saldo_nao_mostra_gasto_no_lugar_dele()
    {
        Assert.Empty(ExperientialUsageProvider.ParseForTest("{ nao e json", Key));
    }

    [Fact]
    public void Saldo_sem_o_gasto_ainda_vale()
    {
        Assert.Single(ExperientialUsageProvider.ParseForTest(Credits, null));
    }

    [Fact]
    public void Catalogo_e_cofre_reconhecem_o_servico()
    {
        Assert.Equal("Experiential", ServiceCatalog.GetByPrefix("experiential.balance")?.Group);
        Assert.Equal("Gaugely/experiential", CredentialVault.TargetFor(CredentialVault.Experiential));
        Assert.Null(CredentialVault.LegacyTargetFor(CredentialVault.Experiential));
        Assert.Contains(CredentialVault.Experiential, CredentialVault.Services);
    }
}
