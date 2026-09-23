using Xunit;
using RateTray.Model;

namespace RateTray.Tests;

/// <summary>
/// Fork: testes para a separação de listas Icons / WidgetIcons.
/// Usa o método puro TrayApp.FilteredReadingsOf para não instanciar o aplicativo.
/// </summary>
public sealed class ForkListasSeparadasTests
{
    // ---- helpers ----

    private static LimitReading FakeReading(string id, string group, double percent = 50) =>
        new() { Id = id, Group = group, Label = $"Label {id}", Percent = percent };

    // ---- testes ----

    /// <summary>
    /// Desmarcar na bandeja (Icons) não afeta a faixa (WidgetIcons).
    /// </summary>
    [Fact]
    public void Desmarcar_na_bandeja_nao_afeta_faixa()
    {
        // Arrange: duas janelas do mesmo serviço
        var readings = new List<LimitReading>
        {
            FakeReading("claude.session", "Claude"),
            FakeReading("claude.weekly", "Claude"),
        };

        // Icons só tem session (weekly desmarcada na bandeja)
        var icons = new List<string> { "claude.session" };
        // WidgetIcons tem as duas (faixa mostra tudo)
        var widgetIcons = new List<string> { "claude.session", "claude.weekly" };
        var knownGroups = new List<string> { "Claude" };

        // Act
        var tray = TrayApp.FilteredReadingsOf(readings, icons, knownGroups, "Claude");
        var widget = TrayApp.FilteredReadingsOf(readings, widgetIcons, knownGroups, "Claude");

        // Assert: bandeja tem 1, faixa tem 2
        Assert.Single(tray);
        Assert.Equal("claude.session", tray[0].Id, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, widget.Count);
    }

    /// <summary>
    /// Faixa com lista nula (WidgetIcons == null) cai para Icons.
    /// </summary>
    [Fact]
    public void Faixa_com_lista_nula_usa_Icons()
    {
        var readings = new List<LimitReading>
        {
            FakeReading("codex.weekly", "Codex"),
            FakeReading("codex.daily", "Codex"),
        };

        var icons = new List<string> { "codex.weekly" };
        List<string>? widgetIcons = null;
        var knownGroups = new List<string> { "Codex" };

        // Quando WidgetIcons é nulo, o chamador (WidgetReadingsOf) passa Icons como lista
        var listEfetiva = widgetIcons ?? icons;
        var widget = TrayApp.FilteredReadingsOf(readings, listEfetiva, knownGroups, "Codex");

        Assert.Single(widget);
        Assert.Equal("codex.weekly", widget[0].Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Serviço examinado (em KnownGroups) com lista vazia não aparece.
    /// </summary>
    [Fact]
    public void Servico_examinado_com_lista_vazia_nao_aparece()
    {
        var readings = new List<LimitReading>
        {
            FakeReading("kimi.session", "Kimi"),
        };

        // Nenhuma janela marcada
        var icons = new List<string>();
        var knownGroups = new List<string> { "Kimi" };

        var result = TrayApp.FilteredReadingsOf(readings, icons, knownGroups, "Kimi");

        // Serviço examinado + lista vazia = 0 leituras (serviço some)
        Assert.Empty(result);
    }

    /// <summary>
    /// Serviço NÃO examinado (fora de KnownGroups) com lista vazia aparece inteiro (fallback).
    /// </summary>
    [Fact]
    public void Servico_nao_examinado_sem_marcacao_aparece_inteiro()
    {
        var readings = new List<LimitReading>
        {
            FakeReading("openrouter.balance", "OpenRouter"),
            FakeReading("openrouter.limit", "OpenRouter"),
        };

        var icons = new List<string>();
        var knownGroups = new List<string>(); // não examinado

        var result = TrayApp.FilteredReadingsOf(readings, icons, knownGroups, "OpenRouter");

        // Antes do exame, tudo aparece
        Assert.Equal(2, result.Count);
    }
}
