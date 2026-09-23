using RateTray.Localization;
using RateTray.Providers;

namespace RateTray.Tests;

/// <summary>
/// Fork: parse do <c>/coding/v1/usages</c> do Kimi Code. O formato vem de uma resposta
/// real conferida no mesmo dia, com números sintéticos. Nada aqui toca o Gerenciador de
/// Credenciais nem a rede.
/// </summary>
public class ForkKimiTests
{
    public ForkKimiTests() => Loc.Use("en");

    private const string ComoString = """
    {
      "usage": { "limit": "1000", "used": "250", "remaining": "750", "resetTime": "2026-09-20T22:32:47.770388Z" },
      "limits": [
        { "window": { "duration": 300, "timeUnit": "TIME_UNIT_MINUTE" },
          "detail": { "limit": "200", "used": "50", "remaining": "150", "resetTime": "2026-09-16T15:32:47.770388Z" } }
      ],
      "user": { "membership": { "level": "LEVEL_INTERMEDIATE" } }
    }
    """;

    [Fact]
    public void Numeros_em_string_viram_percentual()
    {
        var leituras = KimiUsageProvider.Parse(ComoString, out _);

        Assert.Equal(["kimi.5h", "kimi.week"], leituras.Select(r => r.Id));

        var sessao = leituras[0];
        Assert.Equal(25, sessao.Percent, 3);
        Assert.True(sessao.IsActive);
        Assert.Equal(TimeSpan.FromHours(5), sessao.Window);
        Assert.Equal("5 h", sessao.Label);
        // Microssegundos da API não interessam; o reset tem que cair no mesmo segundo.
        var esperado = new DateTimeOffset(2026, 9, 16, 15, 32, 47, TimeSpan.Zero);
        Assert.NotNull(sessao.ResetsAt);
        Assert.InRange((sessao.ResetsAt!.Value - esperado).TotalSeconds, 0, 1);

        var semana = leituras[1];
        Assert.Equal(25, semana.Percent, 3);
        Assert.Equal(TimeSpan.FromDays(7), semana.Window);
        Assert.False(semana.IsInformational);
        Assert.Equal("Kimi", semana.Group);
    }

    [Fact]
    public void Numeros_como_numero_tambem_valem()
    {
        const string json = """
        {
          "usage": { "limit": 1000, "used": 900, "remaining": 100, "resetTime": "2026-09-20T22:32:47Z" },
          "limits": [ { "window": { "duration": 5, "timeUnit": "TIME_UNIT_HOUR" },
                        "detail": { "limit": 200, "used": 10.5, "remaining": 189.5 } } ]
        }
        """;

        var leituras = KimiUsageProvider.Parse(json, out _);

        Assert.Equal(5.25, leituras.Single(r => r.Id == "kimi.5h").Percent, 3);
        Assert.Equal(90, leituras.Single(r => r.Id == "kimi.week").Percent, 3);
    }

    [Fact]
    public void Used_ausente_sai_de_limit_menos_remaining()
    {
        const string json = """
        { "usage": { "limit": "400", "remaining": "100" } }
        """;

        var semana = KimiUsageProvider.Parse(json, out _).Single();

        Assert.Equal("kimi.week", semana.Id);
        Assert.Equal(75, semana.Percent, 3);
    }

    [Fact]
    public void Percentual_fica_entre_0_e_100()
    {
        const string json = """
        { "usage": { "limit": "100", "used": "130" } }
        """;

        Assert.Equal(100, KimiUsageProvider.Parse(json, out _).Single().Percent);
    }

    [Theory]
    [InlineData("resetAt")]
    [InlineData("reset_time")]
    [InlineData("reset_at")]
    public void Reset_com_grafia_alternativa_e_lido(string campo)
    {
        var json = $$"""
        { "usage": { "limit": "10", "used": "1", "{{campo}}": "2026-09-20T22:32:47Z" } }
        """;

        var semana = KimiUsageProvider.Parse(json, out _).Single();

        Assert.Equal(new DateTimeOffset(2026, 9, 20, 22, 32, 47, TimeSpan.Zero), semana.ResetsAt);
    }

    [Theory]
    [InlineData("LEVEL_FREE", "Adagio")]
    [InlineData("LEVEL_TRIAL", "Andante")]
    [InlineData("LEVEL_BASIC", "Moderato")]
    [InlineData("LEVEL_INTERMEDIATE", "Allegretto")]
    [InlineData("LEVEL_ADVANCED", "Allegro")]
    public void Nivel_vira_nome_do_plano(string nivel, string plano)
    {
        var json = $$"""
        { "usage": { "limit": "10", "used": "1" }, "user": { "membership": { "level": "{{nivel}}" } } }
        """;

        KimiUsageProvider.Parse(json, out var lido);

        Assert.Equal(plano, lido);
    }

    [Fact]
    public void Plano_da_resposta_real_e_Allegretto()
    {
        KimiUsageProvider.Parse(ComoString, out var plano);

        Assert.Equal("Allegretto", plano);
    }

    [Fact]
    public void Sem_membership_nao_ha_plano()
    {
        KimiUsageProvider.Parse("""{ "usage": { "limit": "10", "used": "1" } }""", out var plano);

        Assert.Null(plano);
    }

    [Fact]
    public void Sem_limits_sai_so_a_semana()
    {
        const string json = """
        { "usage": { "limit": "1000", "used": "10", "resetTime": "2026-09-20T22:32:47Z" },
          "user": { "membership": { "level": "LEVEL_BASIC" } } }
        """;

        var leituras = KimiUsageProvider.Parse(json, out var plano);

        var semana = Assert.Single(leituras);
        Assert.Equal("kimi.week", semana.Id);
        Assert.Equal(1, semana.Percent, 3);
        Assert.Equal(1, semana.VariantCount);
        Assert.Equal("Moderato", plano);
    }

    [Fact]
    public void Json_invalido_nao_derruba()
    {
        Assert.Empty(KimiUsageProvider.Parse("<html>502</html>", out var plano));
        Assert.Null(plano);
    }
}
