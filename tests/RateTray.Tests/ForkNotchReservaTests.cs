using System.Drawing;
using RateTray.Configuration;
using RateTray.Ui;
using Xunit;

namespace RateTray.Tests;

/// <summary>
/// Reserva da borda no modo Notch (AppBar): o retângulo pedido ao shell, o ajuste depois do
/// ABM_QUERYPOS e a área em que a faixa se posiciona. As chamadas ao shell ficam fora — só a conta.
/// </summary>
public class ForkNotchReservaTests
{
    private static readonly Rectangle Principal = new(0, 0, 1920, 1080);
    private static readonly Rectangle Secundario = new(1920, -200, 2560, 1440);   // à direita, mais alto

    // ---------------------------------------------------------------- Request

    [Theory]
    [InlineData(BordaTela.Esquerda, 0, 0, 66, 1080)]
    [InlineData(BordaTela.Direita, 1854, 0, 66, 1080)]
    [InlineData(BordaTela.Topo, 0, 0, 1920, 66)]
    [InlineData(BordaTela.Base, 0, 1014, 1920, 66)]
    public void Request_OcupaABordaInteiraDoMonitor(BordaTela borda, int x, int y, int w, int h)
    {
        Assert.Equal(new Rectangle(x, y, w, h), AppBarMath.Request(Principal, borda, 66));
    }

    [Theory]
    [InlineData(BordaTela.Esquerda, 1920, -200, 99, 1440)]
    [InlineData(BordaTela.Direita, 4381, -200, 99, 1440)]
    [InlineData(BordaTela.Topo, 1920, -200, 2560, 99)]
    [InlineData(BordaTela.Base, 1920, 1141, 2560, 99)]
    public void Request_MonitorSecundarioComDeslocamento(BordaTela borda, int x, int y, int w, int h)
    {
        Assert.Equal(new Rectangle(x, y, w, h), AppBarMath.Request(Secundario, borda, 99));
    }

    [Fact]
    public void Request_EspessuraNegativaViraZero()
    {
        var r = AppBarMath.Request(Principal, BordaTela.Topo, -5);
        Assert.Equal(0, r.Height);
        Assert.Equal(Principal.Top, r.Top);
    }

    // ---------------------------------------------------------------- Fit (depois do QUERYPOS)

    [Fact]
    public void Fit_BaseAcimaDaBarraDeTarefas()
    {
        // Barra de tarefas de 48 px na base: o shell empurra o pedido para cima dela e o deixa
        // com altura qualquer; a espessura é refeita a partir do lado encostado.
        var proposto = Rectangle.FromLTRB(0, 1014, 1920, 1032);
        Assert.Equal(Rectangle.FromLTRB(0, 966, 1920, 1032), AppBarMath.Fit(proposto, BordaTela.Base, 66));
    }

    [Fact]
    public void Fit_EsquerdaAoLadoDaBarraNaEsquerda()
    {
        var proposto = Rectangle.FromLTRB(48, 0, 66, 1080);
        Assert.Equal(Rectangle.FromLTRB(48, 0, 114, 1080), AppBarMath.Fit(proposto, BordaTela.Esquerda, 66));
    }

    [Fact]
    public void Fit_TopoEncurtadoPelasLaterais()
    {
        // Outra AppBar na esquerda: o shell encurta a largura; a largura devolvida é mantida.
        var proposto = Rectangle.FromLTRB(100, 0, 1920, 66);
        Assert.Equal(Rectangle.FromLTRB(100, 0, 1920, 66), AppBarMath.Fit(proposto, BordaTela.Topo, 66));
    }

    [Fact]
    public void Fit_DireitaMantemOLadoDeFora()
    {
        var proposto = Rectangle.FromLTRB(1800, 0, 1872, 1080);
        Assert.Equal(Rectangle.FromLTRB(1806, 0, 1872, 1080), AppBarMath.Fit(proposto, BordaTela.Direita, 66));
    }

    // ---------------------------------------------------------------- LayoutArea

    [Theory]
    [InlineData(BordaTela.Esquerda)]
    [InlineData(BordaTela.Direita)]
    [InlineData(BordaTela.Topo)]
    [InlineData(BordaTela.Base)]
    public void LayoutArea_MesmoResultadoAntesEDepoisDeAAreaUtilEncolher(BordaTela borda)
    {
        // A área útil é lida do Windows, que pode ainda não ter descontado a reserva.
        var areaAntes = Rectangle.FromLTRB(0, 0, 1920, 1032);    // só a barra de tarefas na base
        var reservado = AppBarMath.Fit(AppBarMath.Request(areaAntes, borda, 66), borda, 66);
        var areaDepois = Descontar(areaAntes, borda, reservado);

        Assert.Equal(
            AppBarMath.LayoutArea(areaAntes, borda, reservado),
            AppBarMath.LayoutArea(areaDepois, borda, reservado));
    }

    [Theory]
    [InlineData(BordaTela.Esquerda, 96)]
    [InlineData(BordaTela.Direita, 144)]
    [InlineData(BordaTela.Topo, 120)]
    [InlineData(BordaTela.Base, 192)]
    public void Faixa_FicaDentroDaReservaEForaDaAreaUtil(BordaTela borda, int dpi)
    {
        var monitor = Secundario;
        var areaUtil = monitor;
        foreach (var fracao in new[] { 0.0, 0.5, 1.0 })
        {
            var semReserva = WidgetForm.CalculateNotchLayout(borda, 1.0, fracao, 3, areaUtil, dpi).WidgetBounds;
            var espessura = AppBarMath.Thickness(semReserva, borda);
            var reservado = AppBarMath.Fit(AppBarMath.Request(monitor, borda, espessura), borda, espessura);
            var areaDepois = Descontar(areaUtil, borda, reservado);

            var faixa = WidgetForm.CalculateNotchLayout(
                borda, 1.0, fracao, 3, AppBarMath.LayoutArea(areaDepois, borda, reservado), dpi).WidgetBounds;

            Assert.True(reservado.Contains(faixa), $"{borda} {fracao}: {faixa} fora de {reservado}");
            Assert.False(areaDepois.IntersectsWith(faixa), $"{borda} {fracao}: {faixa} invade {areaDepois}");
            Assert.Equal(semReserva.Size, faixa.Size);
        }
    }

    [Fact]
    public void Faixa_ComBarraDeTarefasNaMesmaBordaFicaAcimaDela()
    {
        var areaUtil = Rectangle.FromLTRB(0, 0, 1920, 1032);       // barra de 48 px na base
        var semReserva = WidgetForm.CalculateNotchLayout(BordaTela.Base, 1.0, 0.5, 2, areaUtil, 96).WidgetBounds;
        var espessura = AppBarMath.Thickness(semReserva, BordaTela.Base);
        // O shell devolve o pedido já acima da barra de tarefas.
        var reservado = AppBarMath.Fit(Rectangle.FromLTRB(0, 1014, 1920, 1032), BordaTela.Base, espessura);
        var areaDepois = Descontar(areaUtil, BordaTela.Base, reservado);

        var faixa = WidgetForm.CalculateNotchLayout(
            BordaTela.Base, 1.0, 0.5, 2, AppBarMath.LayoutArea(areaDepois, BordaTela.Base, reservado), 96).WidgetBounds;

        Assert.Equal(1032, faixa.Bottom);
        Assert.Equal(semReserva, faixa);                      // mesma posição de sem reserva
        Assert.Equal(faixa.Top, areaDepois.Bottom);           // janelas maximizadas param na faixa
    }

    [Fact]
    public void LayoutArea_ReservaVaziaDevolveAAreaUtil()
    {
        var area = Rectangle.FromLTRB(0, 0, 1920, 1032);
        Assert.Equal(area, AppBarMath.LayoutArea(area, BordaTela.Topo, Rectangle.Empty));
    }

    // ---------------------------------------------------------------- Thickness

    [Theory]
    [InlineData(BordaTela.Esquerda, 96, 66)]
    [InlineData(BordaTela.Direita, 144, 99)]
    [InlineData(BordaTela.Topo, 96, 66)]
    [InlineData(BordaTela.Base, 192, 132)]
    public void Thickness_ELarguraDaFaixaPerpendicularABorda(BordaTela borda, int dpi, int esperado)
    {
        var faixa = WidgetForm.CalculateNotchLayout(borda, 1.0, 0.5, 4, Principal, dpi).WidgetBounds;
        Assert.Equal(esperado, AppBarMath.Thickness(faixa, borda));
    }

    // ---------------------------------------------------------------- configuração

    [Fact]
    public void ReservarEspaco_DesligadoPorPadrao()
    {
        Assert.False(new WidgetOptions().ReservarEspaco);
        Assert.False(ConfigStore.FromJson("""{ "widget": { "enabled": true } }""").Widget.ReservarEspaco);
    }

    [Fact]
    public void ReservarEspaco_SobreviveAIdaEVoltaDoJson()
    {
        var config = new AppConfig();
        config.Widget.ReservarEspaco = true;
        Assert.True(ConfigStore.FromJson(ConfigStore.ToJson(config)).Widget.ReservarEspaco);
        Assert.True(ConfigStore.Clone(config).Widget.ReservarEspaco);
    }

    /// <summary>O que o Windows faz com a área útil depois do ABM_SETPOS.</summary>
    private static Rectangle Descontar(Rectangle area, BordaTela borda, Rectangle reservado) => borda switch
    {
        BordaTela.Esquerda => Rectangle.FromLTRB(Math.Max(area.Left, reservado.Right), area.Top, area.Right, area.Bottom),
        BordaTela.Direita => Rectangle.FromLTRB(area.Left, area.Top, Math.Min(area.Right, reservado.Left), area.Bottom),
        BordaTela.Topo => Rectangle.FromLTRB(area.Left, Math.Max(area.Top, reservado.Bottom), area.Right, area.Bottom),
        _ => Rectangle.FromLTRB(area.Left, area.Top, area.Right, Math.Min(area.Bottom, reservado.Top)),
    };
}
