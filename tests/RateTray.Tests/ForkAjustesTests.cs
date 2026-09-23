using System.Globalization;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Providers;

namespace RateTray.Tests;

/// <summary>Fork: janela de ajustes nova, recarga do settings.json e idiomas novos.</summary>
public class ForkAjustesTests
{
    [Fact]
    public void CopyInto_copia_os_valores_sem_trocar_os_objetos_que_os_provedores_seguram()
    {
        var live = new AppConfig();
        var kimi = live.Kimi;                 // o provedor do Kimi guarda esta referência
        var openAi = live.OpenAIApi;
        var widget = live.Widget;

        var edited = ConfigStore.Clone(live);
        edited.Kimi.Enabled = false;
        edited.OpenAIApi.MinIntervalSeconds = 1234;
        edited.Widget.Corner = "topLeft";
        edited.Icons = ["claude.session", "kimi.session"];
        edited.RefreshSeconds = 300;

        ConfigStore.CopyInto(edited, live);

        Assert.Same(kimi, live.Kimi);
        Assert.Same(openAi, live.OpenAIApi);
        Assert.Same(widget, live.Widget);
        Assert.False(kimi.Enabled);
        Assert.Equal(1234, openAi.MinIntervalSeconds);
        Assert.Equal("topLeft", widget.Corner);
        Assert.Equal(["claude.session", "kimi.session"], live.Icons);
        Assert.Equal(300, live.RefreshSeconds);
    }

    [Fact]
    public void Clone_e_independente_do_original()
    {
        var live = new AppConfig();
        var draft = ConfigStore.Clone(live);
        draft.Claude.Enabled = false;
        draft.Icons.Add("x");

        Assert.True(live.Claude.Enabled);
        Assert.Empty(live.Icons);
    }

    [Fact]
    public void CopyInto_com_nulo_na_origem_zera_o_campo_opcional()
    {
        var live = new AppConfig { WidgetIcons = ["a"] };
        var edited = ConfigStore.Clone(live);
        edited.WidgetIcons = null;

        ConfigStore.CopyInto(edited, live);

        Assert.Null(live.WidgetIcons);
    }

    [Fact]
    public void Arabe_formata_datas_no_calendario_gregoriano()
    {
        var culture = Loc.Gregorian(CultureInfo.GetCultureInfo("ar-SA"));

        Assert.IsType<GregorianCalendar>(culture.DateTimeFormat.Calendar);
        Assert.Contains("2026", new DateTime(2026, 9, 23).ToString("yyyy", culture));
        Assert.True(culture.TextInfo.IsRightToLeft);
    }

    [Fact]
    public void Cultura_ja_gregoriana_passa_intacta()
    {
        var pt = CultureInfo.GetCultureInfo("pt");
        Assert.Same(pt, Loc.Gregorian(pt));
    }

    [Fact]
    public void Idiomas_publicados_incluem_os_onze()
    {
        foreach (var code in new[] { "ar", "de", "en", "es", "fr", "it", "ja", "ko", "pt", "ru", "zh" })
            Assert.Contains(code, Loc.Available);
    }

    [Fact]
    public void Teto_de_espera_muda_com_o_app_rodando()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = new PollScheduler(maxBackoffMinutes: 60);
        schedule.MaxBackoffMinutes = 1;

        DateTimeOffset until = now;
        for (var i = 0; i < 8; i++) until = schedule.RecordFailure("Claude", null, now, 90);

        Assert.True(until - now <= TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Arquivo_de_ajustes_antigo_com_executablePath_ainda_carrega()
    {
        var config = ConfigStore.FromJson("""{ "codex": { "enabled": false, "executablePath": "C:\\x\\codex.exe" }, "refreshSeconds": 120 }""");

        Assert.False(config.Codex.Enabled);
        Assert.Equal(120, config.RefreshSeconds);
    }
}
