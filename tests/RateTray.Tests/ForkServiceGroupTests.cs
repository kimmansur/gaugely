using RateTray.Model;

namespace RateTray.Tests;

/// <summary>
/// Fork: um ícone por serviço. O número do serviço é o do limite que trava primeiro, e o cartão
/// lista as janelas começando pela que está correndo.
/// </summary>
public class ForkServiceGroupTests
{
    private static LimitReading L(string id, double pct, bool ativo = false, int? horas = null) => new()
    {
        Id = id,
        Label = id,
        Group = "Codex",
        Percent = pct,
        IsActive = ativo,
        Window = horas is { } h ? TimeSpan.FromHours(h) : null,
    };

    [Fact]
    public void Numero_do_servico_e_o_limite_que_trava_primeiro()
    {
        var sessao = L("codex.primary", 10, ativo: true, horas: 5);
        var semana = L("codex.secondary", 95, horas: 168);

        var resumo = ServiceGroup.Summary("Codex", [sessao, semana])!;

        Assert.Equal(95, resumo.Percent);
        Assert.Equal("grupo.codex", resumo.Id);
        Assert.Equal("Codex", resumo.Label);
        Assert.Equal(0, resumo.Variant);
        Assert.Equal(1, resumo.VariantCount);
    }

    [Fact]
    public void Empate_vai_para_a_janela_ativa()
    {
        var sessao = L("codex.primary", 40, ativo: true, horas: 5);
        var semana = L("codex.secondary", 40, horas: 168);

        Assert.Equal("codex.primary", ServiceGroup.Binding([semana, sessao])!.Id);
    }

    [Fact]
    public void Cartao_lista_a_ativa_primeiro_e_depois_das_curtas_as_longas()
    {
        var modelo = L("codex.base.primary", 0, horas: 168);
        var semana = L("codex.secondary", 8, horas: 168);
        var sessao = L("codex.primary", 0, ativo: true, horas: 5);

        var ordem = ServiceGroup.Ordered([modelo, semana, sessao]).Select(r => r.Id).ToList();

        Assert.Equal("codex.primary", ordem[0]);
    }

    [Fact]
    public void Servico_sem_leitura_nao_tem_resumo()
    {
        Assert.Null(ServiceGroup.Summary("Claude", []));
    }

    [Theory]
    [InlineData("grupo.codex", "Codex")]
    [InlineData("grupo.claude", "Claude")]
    [InlineData("grupo.kimi", "Kimi")]
    [InlineData("grupo.openrouter", "OpenRouter")]
    public void Id_do_grupo_volta_ao_nome_do_servico(string id, string grupo)
    {
        Assert.True(ServiceGroup.IsGroupId(id));
        Assert.Equal(grupo, ServiceGroup.GroupOfId(id));
        Assert.Equal(id, ServiceGroup.IdFor(grupo));
    }

    // Fork: leituras informativas (saldo, gasto em US$) não travam nada.

    private static LimitReading Valor(string id, decimal valor) => new()
    {
        Id = id,
        Label = id,
        Group = "OpenRouter",
        Amount = valor,
        AmountUnit = "US$",
    };

    [Fact]
    public void Informativa_nao_disputa_o_limite_que_trava()
    {
        var teto = L("openrouter.key_limit", 12, ativo: true);
        var saldo = Valor("openrouter.balance", 250m);

        var binding = ServiceGroup.Binding([saldo, teto])!;
        var resumo = ServiceGroup.Summary("OpenRouter", [saldo, teto])!;

        Assert.Equal("openrouter.key_limit", binding.Id);
        Assert.Equal(12, resumo.Percent);
        Assert.Null(resumo.Amount);
    }

    [Fact]
    public void Grupo_so_com_informativas_resume_pela_primeira()
    {
        var saldo = Valor("openrouter.balance", 17.9m);
        var hoje = Valor("openrouter.spend_today", 0.4m);

        Assert.Null(ServiceGroup.Binding([saldo, hoje]));

        var resumo = ServiceGroup.Summary("OpenRouter", [saldo, hoje])!;

        Assert.Equal(17.9m, resumo.Amount);
        Assert.Equal("17", resumo.IconText);
        Assert.Equal("grupo.openrouter", resumo.Id);
        Assert.Equal("OpenRouter", resumo.Label);
        Assert.Equal(0, resumo.Variant);
        Assert.Equal(1, resumo.VariantCount);
    }

    [Fact]
    public void Informativas_vao_para_o_fim_na_ordem_do_provedor()
    {
        var saldo = Valor("openrouter.balance", 10m) with { Window = null };
        var mes = Valor("openrouter.spend_month", 3m) with { Window = TimeSpan.FromDays(30) };
        var hoje = Valor("openrouter.spend_today", 1m) with { Window = TimeSpan.FromDays(1) };
        var teto = L("openrouter.key_limit", 50, horas: 168);

        var ordem = ServiceGroup.Ordered([saldo, hoje, mes, teto]).Select(r => r.Id).ToList();

        Assert.Equal(["openrouter.key_limit", "openrouter.balance", "openrouter.spend_today", "openrouter.spend_month"], ordem);
    }

    [Theory]
    [InlineData("kimi.5h", "Kimi")]
    [InlineData("kimi.week", "Kimi")]
    [InlineData("openrouter.balance", "OpenRouter")]
    [InlineData("codex.primary", "Codex")]
    [InlineData("claude.session", "Claude")]
    public void Id_de_leitura_aponta_o_servico_certo(string id, string grupo)
    {
        Assert.Equal(grupo, TrayApp.GroupOf(id));
    }
}
