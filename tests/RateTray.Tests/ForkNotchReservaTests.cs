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

    // ---------------------------------------------------------------- SETPOS sem ciclo

    [Fact]
    public void PrecisaSetPos_PrimeiroPedidoVaiAoShell()
    {
        Assert.True(AppBarMath.PrecisaSetPos(null, Rectangle.FromLTRB(0, 966, 1920, 1032)));
    }

    [Fact]
    public void PrecisaSetPos_RetanguloIgualAoAprovadoNaoRepete()
    {
        // ABN_POSCHANGED de outra AppBar (ou do próprio SETPOS) sem mudança nesta borda: repetir o
        // SETPOS geraria outro aviso, e assim por diante.
        var aprovado = Rectangle.FromLTRB(0, 966, 1920, 1032);
        Assert.False(AppBarMath.PrecisaSetPos(aprovado, Rectangle.FromLTRB(0, 966, 1920, 1032)));
    }

    [Fact]
    public void PrecisaSetPos_BarraDeTarefasCresceuRepete()
    {
        var aprovado = Rectangle.FromLTRB(0, 966, 1920, 1032);
        var depois = AppBarMath.Fit(Rectangle.FromLTRB(0, 1000, 1920, 1008), BordaTela.Base, 66);
        Assert.True(AppBarMath.PrecisaSetPos(aprovado, depois));
    }

    [Fact]
    public void PrecisaSetPos_AvisosRepetidosConvergem()
    {
        // Simula a troca de avisos: cada ABN_POSCHANGED refaz QUERYPOS+Fit; com o shell devolvendo
        // o mesmo retângulo, só o primeiro aviso gera SETPOS.
        Rectangle? aprovado = null;
        var proposto = Rectangle.FromLTRB(0, 1014, 1920, 1032);
        var setpos = 0;
        for (var aviso = 0; aviso < 10; aviso++)
        {
            var ajustado = AppBarMath.Fit(proposto, BordaTela.Base, 66);
            if (AppBarMath.PrecisaSetPos(aprovado, ajustado)) { setpos++; aprovado = ajustado; }
        }
        Assert.Equal(1, setpos);
    }

    // ---------------------------------------------------------------- tela cheia

    [Fact]
    public void ZOrderTelaCheia_VaiAoFundoEVoltaParaCima()
    {
        Assert.Equal(new IntPtr(1), AppBarMath.ZOrderTelaCheia(true));      // HWND_BOTTOM
        Assert.Equal(new IntPtr(-1), AppBarMath.ZOrderTelaCheia(false));    // HWND_TOPMOST
        Assert.NotEqual(new IntPtr(-2), AppBarMath.ZOrderTelaCheia(true));  // nunca HWND_NOTOPMOST
    }

    // ---------------------------------------------------------------- remover antes do layout

    [Theory]
    [InlineData(false, BordaTela.Base, false)]   // opção desligada
    [InlineData(true, BordaTela.Topo, false)]    // outra borda
    [InlineData(true, BordaTela.Base, true)]     // mesma borda, mesmo monitor
    public void MantemReserva_SoNaMesmaBordaEMonitorComAOpcaoLigada(bool quer, BordaTela borda, bool esperado)
    {
        var atual = new AppBarReserva(BordaTela.Base, Principal, Rectangle.FromLTRB(0, 966, 1920, 1032));
        Assert.Equal(esperado, AppBarMath.MantemReserva(atual, quer, borda, Principal));
    }

    [Fact]
    public void MantemReserva_OutroMonitorOuSemReservaRemove()
    {
        var atual = new AppBarReserva(BordaTela.Base, Principal, Rectangle.FromLTRB(0, 966, 1920, 1032));
        Assert.False(AppBarMath.MantemReserva(atual, true, BordaTela.Base, Secundario));
        Assert.False(AppBarMath.MantemReserva(null, true, BordaTela.Base, Principal));
    }

    [Theory]
    [InlineData(BordaTela.Esquerda)]
    [InlineData(BordaTela.Direita)]
    [InlineData(BordaTela.Topo)]
    [InlineData(BordaTela.Base)]
    public void AreaSemReserva_DesligarDevolveAFaixaABorda(BordaTela borda)
    {
        // Barra de tarefas de 48 px na base; a faixa reservava a borda. Ao desligar, a área útil lida
        // logo depois do ABM_REMOVE pode ainda estar reduzida — a faixa tem de voltar ao mesmo lugar
        // de quem nunca reservou, e não ficar afastada da borda pela espessura da reserva.
        var semNada = Rectangle.FromLTRB(0, 0, 1920, 1032);
        var reservado = AppBarMath.Fit(AppBarMath.Request(semNada, borda, 66), borda, 66);
        var reduzida = Descontar(semNada, borda, reservado);
        var removida = new AppBarReserva(borda, Principal, reservado);

        Assert.Equal(semNada, AppBarMath.AreaSemReserva(reduzida, Principal, removida));   // Windows atrasado
        Assert.Equal(semNada, AppBarMath.AreaSemReserva(semNada, Principal, removida));    // já atualizado

        var esperado = WidgetForm.CalculateNotchLayout(borda, 1.0, 0.5, 3, semNada, 96).WidgetBounds;
        var faixa = WidgetForm.CalculateNotchLayout(
            borda, 1.0, 0.5, 3, AppBarMath.AreaSemReserva(reduzida, Principal, removida), 96).WidgetBounds;
        Assert.Equal(esperado, faixa);
    }

    [Fact]
    public void AreaSemReserva_ReservaDeOutroMonitorNaoMexeNaArea()
    {
        var area = Rectangle.FromLTRB(1920, -200, 4480, 1240);
        var removida = new AppBarReserva(BordaTela.Base, Principal, Rectangle.FromLTRB(0, 966, 1920, 1032));
        Assert.Equal(area, AppBarMath.AreaSemReserva(area, Secundario, removida));
        Assert.Equal(area, AppBarMath.AreaSemReserva(area, Secundario, null));
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
