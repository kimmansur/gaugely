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

    private static readonly Rectangle Aprovado = Rectangle.FromLTRB(0, 966, 1920, 1032);

    [Fact]
    public void Guarda_PrimeiroPedidoVaiAoShell()
    {
        Assert.Equal(SetPosDecisao.Enviar, new SetPosGuarda().Decidir(Aprovado, 0));
    }

    [Fact]
    public void Guarda_PedidoIgualAoAnteriorNaoRepete()
    {
        var g = new SetPosGuarda();
        g.Registrar(Aprovado, Aprovado, 0);
        Assert.Equal(SetPosDecisao.Reusar, g.Decidir(Aprovado, 10));
    }

    [Fact]
    public void Guarda_ShellQueAjustaOConcedidoNaoGeraCiclo()
    {
        // O shell devolve no SETPOS um retângulo diferente do pedido, sempre. Comparar com o
        // concedido mandaria SETPOS a cada ABN_POSCHANGED; comparando com o pedido, só o primeiro.
        var g = new SetPosGuarda();
        var pedido = AppBarMath.Fit(Rectangle.FromLTRB(0, 1014, 1920, 1032), BordaTela.Base, 66);
        var concedido = Rectangle.FromLTRB(0, 970, 1920, 1032);
        var setpos = 0;
        for (var aviso = 0; aviso < 50; aviso++)
        {
            if (g.Decidir(pedido, aviso * 5) == SetPosDecisao.Enviar) { setpos++; g.Registrar(pedido, concedido, aviso * 5); }
        }
        Assert.Equal(1, setpos);
        Assert.Equal(concedido, g.UltimoConcedido);
    }

    [Fact]
    public void Guarda_QueryPosQueDevolveOConcedidoTambemReusa()
    {
        var g = new SetPosGuarda();
        var concedido = Rectangle.FromLTRB(0, 970, 1920, 1032);
        g.Registrar(Aprovado, concedido, 0);
        Assert.Equal(SetPosDecisao.Reusar, g.Decidir(concedido, 10));
    }

    [Fact]
    public void Guarda_BarraDeTarefasCresceuRepete()
    {
        var g = new SetPosGuarda();
        g.Registrar(Aprovado, Aprovado, 0);
        var depois = AppBarMath.Fit(Rectangle.FromLTRB(0, 1000, 1920, 1008), BordaTela.Base, 66);
        Assert.Equal(SetPosDecisao.Enviar, g.Decidir(depois, 5000));
    }

    [Fact]
    public void Guarda_OscilacaoParaNoLimiteEResfria()
    {
        // Outra AppBar disputando a borda: o QUERYPOS alterna entre dois retângulos. No máximo
        // Maximo SETPOS na janela; depois, resfriamento sem SETPOS (antes a trava era permanente
        // até Liberar; agora acaba sozinha — ver Guarda_QuatroMudancasReaisReconciliamNoFim).
        var g = new SetPosGuarda();
        var a = Aprovado;
        var b = Rectangle.FromLTRB(0, 900, 1920, 966);
        var setpos = 0;
        for (var aviso = 0; aviso < 20; aviso++)
        {
            var t = aviso * 100L;   // 2 s de avisos
            var r = aviso % 2 == 0 ? a : b;
            var d = g.Decidir(r, t);
            if (d == SetPosDecisao.Enviar) { setpos++; g.Registrar(r, r, t); }
        }
        Assert.Equal(SetPosGuarda.Maximo, setpos);
        Assert.True(g.Pendente);
        Assert.NotNull(g.ResfriamentoAte);

        g.Liberar();   // mudança de tela, de borda ou Explorer reiniciado: zera na hora
        Assert.False(g.Pendente);
        Assert.Null(g.ResfriamentoAte);
        Assert.Equal(SetPosDecisao.Enviar, g.Decidir(a == g.UltimoPedido ? b : a, 2_100));
    }

    [Fact]
    public void Guarda_QuatroMudancasReaisReconciliamNoFim()
    {
        // Outra AppBar sendo redimensionada: quatro posições legítimas em menos de 2 s, depois para.
        // A quarta é adiada (não trava de vez) e, no fim do resfriamento, uma reconciliação leva a
        // reserva para a posição final — sem esperar mudança de tela nem novo registro.
        var g = new SetPosGuarda();
        Rectangle Pos(int i) => Rectangle.FromLTRB(0, 966 - 10 * i, 1920, 1032 - 10 * i);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(SetPosDecisao.Enviar, g.Decidir(Pos(i), i * 100));
            g.Registrar(Pos(i), Pos(i), i * 100);
        }
        Assert.Equal(SetPosDecisao.Adiado, g.Decidir(Pos(3), 300));
        Assert.True(g.Pendente);
        Assert.Equal(Pos(2), g.UltimoConcedido);   // a concessão anterior continua valendo

        var fim = g.ResfriamentoAte!.Value;
        Assert.Equal(300 + SetPosGuarda.JanelaMs, fim);
        Assert.Equal(SetPosDecisao.Adiado, g.Decidir(Pos(3), fim - 1));   // aviso no meio: segue adiado

        // Timer do fim do resfriamento: QUERYPOS devolve a posição estável e ela vai ao shell.
        Assert.Equal(SetPosDecisao.Enviar, g.Decidir(Pos(3), fim));
        g.Registrar(Pos(3), Pos(3), fim);
        Assert.False(g.Pendente);
        Assert.Null(g.ResfriamentoAte);
        Assert.Equal(Pos(3), g.UltimoConcedido);

        // Estável: os avisos seguintes não repetem o pedido.
        Assert.Equal(SetPosDecisao.Reusar, g.Decidir(Pos(3), fim + 10));
        Assert.Equal(SetPosDecisao.Reusar, g.Decidir(Pos(3), fim + 60_000));
    }

    [Fact]
    public void Guarda_ReconciliacaoSemMudancaNaoManda()
    {
        // Adiado e, no fim do resfriamento, o shell voltou ao que já estava pedido: nada vai ao shell.
        var g = new SetPosGuarda();
        var b = Rectangle.FromLTRB(0, 900, 1920, 966);
        for (var i = 0; i < 3; i++) { var r = i % 2 == 0 ? Aprovado : b; g.Decidir(r, i); g.Registrar(r, r, i); }
        Assert.Equal(SetPosDecisao.Adiado, g.Decidir(b, 5));
        var fim = g.ResfriamentoAte!.Value;
        Assert.Equal(SetPosDecisao.Reusar, g.Decidir(Aprovado, fim));
        Assert.False(g.Pendente);
    }

    [Fact]
    public void Guarda_ShellQueNuncaEstabilizaTemSetPosLimitadoPorJanela()
    {
        // Shell que devolve um retângulo diferente a cada QUERYPOS (e concede outro ainda), com aviso
        // a cada 10 ms por 60 s — o timer de reconciliação cai num desses avisos. Os SETPOS ficam em
        // no máximo Maximo por janela, reconciliação incluída; o total segue o tempo, não os avisos.
        var g = new SetPosGuarda();
        var envios = new List<long>();
        var n = 0;
        Rectangle Proximo() { n++; return Rectangle.FromLTRB(0, 966 - n % 500, 1920, 1032 - n % 500); }

        for (long t = 0; t < 60_000; t += 10)
        {
            var r = Proximo();
            if (g.Decidir(r, t) == SetPosDecisao.Enviar)
            {
                envios.Add(t);
                g.Registrar(r, Rectangle.FromLTRB(r.Left, r.Top + 1, r.Right, r.Bottom), t);   // concede diferente
            }
        }

        // Nenhuma janela deslizante de JanelaMs tem mais que Maximo SETPOS.
        for (var i = 0; i < envios.Count; i++)
            Assert.True(envios.Count(e => e >= envios[i] && e < envios[i] + SetPosGuarda.JanelaMs) <= SetPosGuarda.Maximo);

        // 6.000 avisos, no máximo Maximo SETPOS a cada JanelaMs: ~90, não 6.000.
        var janelas = 60_000 / SetPosGuarda.JanelaMs + 1;
        Assert.InRange(envios.Count, SetPosGuarda.Maximo, SetPosGuarda.Maximo * janelas);
    }

    [Fact]
    public void Guarda_MudancasEspacadasNaoTravam()
    {
        // Mudanças reais mais lentas que Maximo por janela nunca travam.
        var g = new SetPosGuarda();
        var passo = SetPosGuarda.JanelaMs / SetPosGuarda.Maximo + 1;
        for (var i = 0; i < 20; i++)
        {
            var r = Rectangle.FromLTRB(0, 966 - i, 1920, 1032);
            var t = i * passo;
            Assert.Equal(SetPosDecisao.Enviar, g.Decidir(r, t));
            g.Registrar(r, r, t);
        }
        Assert.False(g.Pendente);
    }

    [Fact]
    public void Guarda_SemConcessaoNuncaTrava()
    {
        // Sem nada concedido não há reserva para manter: o pedido segue (o limite só segura reenvio).
        var g = new SetPosGuarda();
        for (var i = 0; i < 10; i++) Assert.Equal(SetPosDecisao.Enviar, g.Decidir(Aprovado, i));
    }

    // ---------------------------------------------------------------- tela cheia

    [Fact]
    public void ZOrderTelaCheia_VaiAoFundoEVoltaParaCima()
    {
        Assert.Equal(new IntPtr(1), AppBarMath.ZOrderTelaCheia(true));      // HWND_BOTTOM
        Assert.Equal(new IntPtr(-1), AppBarMath.ZOrderTelaCheia(false));    // HWND_TOPMOST
        Assert.NotEqual(new IntPtr(-2), AppBarMath.ZOrderTelaCheia(true));  // nunca HWND_NOTOPMOST
    }

    [Fact]
    public void TelaCheia_SobreviveAoHandleRecriado()
    {
        // ABN_FULLSCREENAPP só chega na transição. O handle recriado nasce topmost; o novo registro
        // tem de mandá-lo de volta para o fundo.
        var estado = new EstadoTelaCheia();
        Assert.Null(estado.AoRegistrar());
        Assert.Equal(AppBarMath.HwndBottom, estado.Avisar(true));
        // RecreateHandle: Remove(manterTelaCheia: true) não toca no estado.
        Assert.True(estado.Aberto);
        Assert.Equal(AppBarMath.HwndBottom, estado.AoRegistrar());
        Assert.Equal(AppBarMath.HwndBottom, estado.AoRegistrar());   // Explorer reiniciado de novo
        Assert.Equal(AppBarMath.HwndTopMost, estado.Avisar(false));
        Assert.Null(estado.AoRegistrar());
    }

    [Fact]
    public void TelaCheia_DesligarAReservaVoltaParaCimaEEsquece()
    {
        var estado = new EstadoTelaCheia();
        estado.Avisar(true);
        Assert.Equal(AppBarMath.HwndTopMost, estado.AoDesligar());
        Assert.False(estado.Aberto);
        Assert.Null(estado.AoDesligar());
        Assert.Null(estado.AoRegistrar());   // religar depois não manda ao fundo sem aviso novo
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

    [Fact]
    public void AreaAposRemover_UsaAAreaLidaNaHoraEmVezDoCache()
    {
        // Cache do WinForms ainda com a reserva (faixa de 66 px acima da barra de tarefas); o Windows
        // já devolveu o espaço.
        var cache = Rectangle.FromLTRB(0, 0, 1920, 966);
        var fresca = Rectangle.FromLTRB(0, 0, 1920, 1032);
        Assert.Equal(fresca, AppBarMath.AreaAposRemover(fresca, cache, Principal));

        var esperado = WidgetForm.CalculateNotchLayout(BordaTela.Base, 1.0, 0.5, 3, fresca, 96).WidgetBounds;
        var faixa = WidgetForm.CalculateNotchLayout(
            BordaTela.Base, 1.0, 0.5, 3, AppBarMath.AreaAposRemover(fresca, cache, Principal), 96).WidgetBounds;
        Assert.Equal(esperado, faixa);
        Assert.Equal(1032, faixa.Bottom);
    }

    [Fact]
    public void AreaAposRemover_NaoInvadeOutraAppBarNaMesmaBorda()
    {
        // Outra AppBar de 40 px continua na base, por dentro da que saiu. A área lida na hora a
        // desconta; uma área sintetizada até a face externa da reserva removida passaria por cima.
        var outra = Rectangle.FromLTRB(0, 926, 1920, 966);
        var fresca = Rectangle.FromLTRB(0, 0, 1920, outra.Top);
        var area = AppBarMath.AreaAposRemover(fresca, Rectangle.FromLTRB(0, 0, 1920, 926), Principal);

        var faixa = WidgetForm.CalculateNotchLayout(BordaTela.Base, 1.0, 0.5, 3, area, 96).WidgetBounds;
        Assert.False(faixa.IntersectsWith(outra), $"{faixa} invade {outra}");
    }

    [Fact]
    public void AreaAposRemover_LeituraInvalidaFicaComOCache()
    {
        var cache = Rectangle.FromLTRB(0, 0, 1920, 1032);
        Assert.Equal(cache, AppBarMath.AreaAposRemover(null, cache, Principal));
        Assert.Equal(cache, AppBarMath.AreaAposRemover(Rectangle.Empty, cache, Principal));
        Assert.Equal(cache, AppBarMath.AreaAposRemover(Rectangle.FromLTRB(1920, -200, 4480, 1240), cache, Principal));   // outro monitor
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
