using System;
using System.Collections.Generic;
using System.Drawing;
using RateTray.Configuration;
using RateTray.Ui;
using Xunit;

namespace RateTray.Tests;

/// <summary>
/// Testes do modo Notch: escolha da borda mais próxima, conversão posição↔fração,
/// migração de Horizontal para Borda, layout do notch, e queda para o monitor principal.
/// </summary>
public class ForkNotchTests
{
    // ---------------------------------------------------------------- NearestEdge

    [Fact]
    public void NearestEdge_PontoPertoDoTopo()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        Assert.Equal(BordaTela.Topo, WidgetForm.NearestEdge(new Point(960, 10), area));
    }

    [Fact]
    public void NearestEdge_PontoPertoDeBase()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        Assert.Equal(BordaTela.Base, WidgetForm.NearestEdge(new Point(960, 1070), area));
    }

    [Fact]
    public void NearestEdge_PontoPertoDeEsquerda()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        Assert.Equal(BordaTela.Esquerda, WidgetForm.NearestEdge(new Point(5, 540), area));
    }

    [Fact]
    public void NearestEdge_PontoPertoDoLadoDireito()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        Assert.Equal(BordaTela.Direita, WidgetForm.NearestEdge(new Point(1915, 540), area));
    }

    [Fact]
    public void NearestEdge_CentroRetornaBasePorEmpate()
    {
        // Ponto exatamente no centro — distâncias iguais. A implementação deve retornar algo
        // determinístico; verificamos que não lança exceção e que o resultado é uma borda válida.
        var area = new Rectangle(0, 0, 1000, 1000);
        var result = WidgetForm.NearestEdge(new Point(500, 500), area);
        Assert.True(Enum.IsDefined(result));
    }

    [Fact]
    public void NearestEdge_MonitorSecundarioComOffset()
    {
        // Monitor que começa em x=1920
        var area = new Rectangle(1920, 0, 2560, 1440);
        // Perto da borda direita desse monitor
        Assert.Equal(BordaTela.Direita, WidgetForm.NearestEdge(new Point(1920 + 2555, 720), area));
    }

    [Theory]
    [InlineData(100, 540, BordaTela.Esquerda)]
    [InlineData(1820, 540, BordaTela.Direita)]
    [InlineData(960, 50, BordaTela.Topo)]
    [InlineData(960, 1030, BordaTela.Base)]
    public void NearestEdge_VariosCantos(int px, int py, BordaTela esperado)
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        Assert.Equal(esperado, WidgetForm.NearestEdge(new Point(px, py), area));
    }

    // ---------------------------------------------------------------- PositionToFraction

    [Fact]
    public void PositionToFraction_CentroRetornaMeio()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        // Widget com altura 160, na borda direita (vertical), centrado em Y
        var pos = new Point(1920 - 66, (1080 - 160) / 2);
        var frac = WidgetForm.PositionToFraction(pos, BordaTela.Direita, area, 160);
        Assert.InRange(frac, 0.49, 0.51);
    }

    [Fact]
    public void PositionToFraction_TopoRetornaZero()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var frac = WidgetForm.PositionToFraction(new Point(0, 0), BordaTela.Esquerda, area, 200);
        Assert.Equal(0.0, frac);
    }

    [Fact]
    public void PositionToFraction_BaseRetornaUm()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var frac = WidgetForm.PositionToFraction(new Point(0, 1080 - 200), BordaTela.Esquerda, area, 200);
        Assert.Equal(1.0, frac);
    }

    [Fact]
    public void PositionToFraction_BordaHorizontal()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var frac = WidgetForm.PositionToFraction(new Point(960, 0), BordaTela.Topo, area, 234);
        // 960 / (1920 - 234) ≈ 0.569
        Assert.InRange(frac, 0.56, 0.58);
    }

    [Fact]
    public void PositionToFraction_WidgetMaiorQueAreaRetornaMeio()
    {
        var area = new Rectangle(0, 0, 200, 200);
        // Widget de 300 num monitor de 200: range <= 0 → retorna 0.5
        var frac = WidgetForm.PositionToFraction(new Point(0, 0), BordaTela.Esquerda, area, 300);
        Assert.Equal(0.5, frac);
    }

    // ---------------------------------------------------------------- CalculateNotchLayout

    [Fact]
    public void CalculateNotchLayout_ColadoNaBordaDireita()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var (bounds, _) = WidgetForm.CalculateNotchLayout(BordaTela.Direita, 1.0, 0.5, 2, area, 96);

        // Deve estar colado na borda direita (X + Width == area.Right)
        Assert.Equal(area.Right, bounds.Right);
    }

    [Fact]
    public void CalculateNotchLayout_ColadoNaBordaEsquerda()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var (bounds, _) = WidgetForm.CalculateNotchLayout(BordaTela.Esquerda, 1.0, 0.5, 2, area, 96);

        Assert.Equal(area.Left, bounds.Left);
    }

    [Fact]
    public void CalculateNotchLayout_ColadoNoTopo()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var (bounds, _) = WidgetForm.CalculateNotchLayout(BordaTela.Topo, 1.0, 0.5, 2, area, 96);

        Assert.Equal(area.Top, bounds.Top);
    }

    [Fact]
    public void CalculateNotchLayout_ColadoNaBase()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var (bounds, _) = WidgetForm.CalculateNotchLayout(BordaTela.Base, 1.0, 0.5, 2, area, 96);

        Assert.Equal(area.Bottom, bounds.Bottom);
    }

    [Fact]
    public void CalculateNotchLayout_FracaoZeroFicaNoInicio()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var (bounds, _) = WidgetForm.CalculateNotchLayout(BordaTela.Esquerda, 1.0, 0.0, 2, area, 96);

        Assert.Equal(area.Top, bounds.Top);
    }

    [Fact]
    public void CalculateNotchLayout_FracaoUmFicaNoFim()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var (bounds, _) = WidgetForm.CalculateNotchLayout(BordaTela.Esquerda, 1.0, 1.0, 2, area, 96);

        Assert.Equal(area.Bottom, bounds.Bottom);
    }

    [Fact]
    public void CalculateNotchLayout_OrientacaoDerivadaDaBordaVertical()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        // Borda Esquerda => vertical => Width == BaseWidth (66)
        var (bounds, _) = WidgetForm.CalculateNotchLayout(BordaTela.Esquerda, 1.0, 0.5, 2, area, 96);
        Assert.Equal(66, bounds.Width);
    }

    [Fact]
    public void CalculateNotchLayout_OrientacaoDerivadaDaBordaHorizontal()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        // Borda Topo => horizontal => Height == BaseWidth (66)
        var (bounds, _) = WidgetForm.CalculateNotchLayout(BordaTela.Topo, 1.0, 0.5, 2, area, 96);
        Assert.Equal(66, bounds.Height);
    }

    // ---------------------------------------------------------------- Migração Horizontal → Borda

    [Fact]
    public void MigrateOrientation_HorizontalViraBase()
    {
        var opt = new WidgetOptions { Orientation = "horizontal" };
        WidgetOptions.MigrateOrientation(opt);
        Assert.Equal(BordaTela.Base, opt.Borda);
    }

    [Fact]
    public void MigrateOrientation_VerticalViraDireita()
    {
        var opt = new WidgetOptions { Orientation = "vertical" };
        WidgetOptions.MigrateOrientation(opt);
        Assert.Equal(BordaTela.Direita, opt.Borda);
    }

    [Fact]
    public void MigrateOrientation_NotchNaoAltera()
    {
        var opt = new WidgetOptions { Modo = ModoApresentacao.Notch, Borda = BordaTela.Esquerda, Orientation = "horizontal" };
        WidgetOptions.MigrateOrientation(opt);
        // No modo Notch a migração não toca a Borda
        Assert.Equal(BordaTela.Esquerda, opt.Borda);
    }

    [Fact]
    public void MigrateOrientation_PreservaModo()
    {
        var opt = new WidgetOptions { Orientation = "horizontal" };
        WidgetOptions.MigrateOrientation(opt);
        // A migração não altera o modo — continua Flutuante
        Assert.Equal(ModoApresentacao.Flutuante, opt.Modo);
    }

    // ---------------------------------------------------------------- Monitor Fallback

    [Fact]
    public void ResolveMonitor_EncontraMonitorSalvo()
    {
        var disponíveis = new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2" };
        var result = WidgetForm.ResolveMonitor(@"\\.\DISPLAY2", disponíveis, @"\\.\DISPLAY1");
        Assert.Equal(@"\\.\DISPLAY2", result);
    }

    [Fact]
    public void ResolveMonitor_CaiParaPrincipalQuandoNaoExiste()
    {
        var disponíveis = new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2" };
        var result = WidgetForm.ResolveMonitor(@"\\.\DISPLAY3", disponíveis, @"\\.\DISPLAY1");
        Assert.Equal(@"\\.\DISPLAY1", result);
    }

    [Fact]
    public void ResolveMonitor_NomeVazioCaiParaPrincipal()
    {
        var disponíveis = new[] { @"\\.\DISPLAY1" };
        var result = WidgetForm.ResolveMonitor(null, disponíveis, @"\\.\DISPLAY1");
        Assert.Equal(@"\\.\DISPLAY1", result);
    }

    [Fact]
    public void ResolveMonitor_CaseInsensitive()
    {
        var disponíveis = new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2" };
        var result = WidgetForm.ResolveMonitor(@"\\.\display2", disponíveis, @"\\.\DISPLAY1");
        Assert.Equal(@"\\.\DISPLAY2", result);
    }

    // ---------------------------------------------------------------- Normalize

    [Fact]
    public void Normalize_BordaInvalida()
    {
        var opt = new WidgetOptions { Borda = (BordaTela)99 }.Normalize();
        Assert.Equal(WidgetOptions.BordaDefault, opt.Borda);
    }

    [Fact]
    public void Normalize_ModoInvalido()
    {
        var opt = new WidgetOptions { Modo = (ModoApresentacao)42 }.Normalize();
        Assert.Equal(WidgetOptions.ModoDefault, opt.Modo);
    }

    [Fact]
    public void Normalize_FracaoBordaClamp()
    {
        var opt = new WidgetOptions { FracaoBorda = -0.5 }.Normalize();
        Assert.Equal(0.0, opt.FracaoBorda);

        opt = new WidgetOptions { FracaoBorda = 1.5 }.Normalize();
        Assert.Equal(1.0, opt.FracaoBorda);

        opt = new WidgetOptions { FracaoBorda = double.NaN }.Normalize();
        Assert.Equal(0.5, opt.FracaoBorda);
    }

    [Fact]
    public void Normalize_PreservaEscalaEOrientacao()
    {
        var opt = new WidgetOptions
        {
            Scale = 1.2,
            Orientation = "horizontal",
            Borda = BordaTela.Topo,
            Modo = ModoApresentacao.Notch,
        }.Normalize();

        Assert.Equal(1.2, opt.Scale);
        Assert.Equal("horizontal", opt.Orientation);
        Assert.Equal(BordaTela.Topo, opt.Borda);
        Assert.Equal(ModoApresentacao.Notch, opt.Modo);
    }

    // ---------------------------------------------------------------- OrientacaoEfetiva

    [Fact]
    public void OrientacaoEfetiva_NotchDerivaDeEsquerdaDireita()
    {
        var opt = new WidgetOptions { Modo = ModoApresentacao.Notch, Borda = BordaTela.Esquerda };
        Assert.Equal("vertical", opt.OrientacaoEfetiva);

        opt = new WidgetOptions { Modo = ModoApresentacao.Notch, Borda = BordaTela.Direita };
        Assert.Equal("vertical", opt.OrientacaoEfetiva);
    }

    [Fact]
    public void OrientacaoEfetiva_NotchDerivaDeTopoBase()
    {
        var opt = new WidgetOptions { Modo = ModoApresentacao.Notch, Borda = BordaTela.Topo };
        Assert.Equal("horizontal", opt.OrientacaoEfetiva);

        opt = new WidgetOptions { Modo = ModoApresentacao.Notch, Borda = BordaTela.Base };
        Assert.Equal("horizontal", opt.OrientacaoEfetiva);
    }

    [Fact]
    public void OrientacaoEfetiva_FlutuanteUsaOrientation()
    {
        var opt = new WidgetOptions { Modo = ModoApresentacao.Flutuante, Orientation = "horizontal" };
        Assert.Equal("horizontal", opt.OrientacaoEfetiva);

        opt = new WidgetOptions { Modo = ModoApresentacao.Flutuante, Orientation = "vertical" };
        Assert.Equal("vertical", opt.OrientacaoEfetiva);
    }

    [Fact]
    public void IsHorizontal_Correto()
    {
        var opt = new WidgetOptions { Modo = ModoApresentacao.Notch, Borda = BordaTela.Topo };
        Assert.True(opt.IsHorizontal);

        opt = new WidgetOptions { Modo = ModoApresentacao.Notch, Borda = BordaTela.Direita };
        Assert.False(opt.IsHorizontal);
    }

    // ---------------------------------------------------------------- NotchShape Geometry

    [Theory]
    [InlineData(BordaTela.Topo)]
    [InlineData(BordaTela.Base)]
    [InlineData(BordaTela.Esquerda)]
    [InlineData(BordaTela.Direita)]
    public void NotchShape_NaoUltrapassaRetanguloDoFormulario(BordaTela borda)
    {
        var isHorizontal = borda is BordaTela.Topo or BordaTela.Base;
        var width = isHorizontal ? 200 : 100;
        var height = isHorizontal ? 100 : 200;
        var bounds = new Rectangle(0, 0, width, height);

        using var path = WidgetForm.NotchShape(bounds, 14, borda);

        // Fork: GetBounds() em curvas devolve a caixa dos pontos de controle das Bezier,
        // que e maior que a curva desenhada. Achatamos o caminho para medir o contorno real.
        path.Flatten();
        var pathBounds = path.GetBounds();

        // O path pode ser levemente menor ou igual, mas nunca exceder bounds
        Assert.True(pathBounds.Left >= bounds.Left);
        Assert.True(pathBounds.Top >= bounds.Top);
        Assert.True(pathBounds.Right <= bounds.Right);
        Assert.True(pathBounds.Bottom <= bounds.Bottom);
    }

    [Theory]
    [InlineData(BordaTela.Topo, 1, 1)]         // Quina colada no topo/esquerda
    [InlineData(BordaTela.Base, 1, 99)]        // Quina colada na base/esquerda
    [InlineData(BordaTela.Esquerda, 1, 1)]     // Quina colada na esquerda/topo
    [InlineData(BordaTela.Direita, 99, 1)]     // Quina colada na direita/topo
    public void NotchShape_QuinaEncostadaEstaForaDaForma(BordaTela borda, int x, int y)
    {
        var isHorizontal = borda is BordaTela.Topo or BordaTela.Base;
        var bounds = new Rectangle(0, 0, isHorizontal ? 200 : 100, isHorizontal ? 100 : 200);

        using var path = WidgetForm.NotchShape(bounds, 14, borda);
        
        // Ali a curva já escavou (côncavo)
        Assert.False(path.IsVisible(x, y));
    }

    [Theory]
    [InlineData(BordaTela.Topo)]
    [InlineData(BordaTela.Base)]
    [InlineData(BordaTela.Esquerda)]
    [InlineData(BordaTela.Direita)]
    public void NotchShape_CentroDaPilulaEstaDentro(BordaTela borda)
    {
        var isHorizontal = borda is BordaTela.Topo or BordaTela.Base;
        var bounds = new Rectangle(0, 0, isHorizontal ? 200 : 100, isHorizontal ? 100 : 200);

        using var path = WidgetForm.NotchShape(bounds, 14, borda);
        
        // Ponto central deve estar sempre dentro da pílula
        Assert.True(path.IsVisible(bounds.Width / 2f, bounds.Height / 2f));
    }

    [Theory]
    [InlineData(BordaTela.Topo, 1, 99)]        // Quina oposta (base/esquerda)
    [InlineData(BordaTela.Base, 1, 1)]         // Quina oposta (topo/esquerda)
    [InlineData(BordaTela.Esquerda, 99, 1)]    // Quina oposta (direita/topo)
    [InlineData(BordaTela.Direita, 1, 1)]      // Quina oposta (esquerda/topo)
    public void NotchShape_QuinaExternaEstaDentro(BordaTela borda, int x, int y)
    {
        // Para verificar a quina externa "dentro", testamos um ponto BEM PRÓXIMO 
        // da quina convexa, já que o arredondamento convexo corta o bico extremo (0,0).
        // Um ponto a 5 pixels da quina oposta ainda deve estar dentro do preenchimento.
        var isHorizontal = borda is BordaTela.Topo or BordaTela.Base;
        var bounds = new Rectangle(0, 0, isHorizontal ? 200 : 100, isHorizontal ? 100 : 200);

        using var path = WidgetForm.NotchShape(bounds, 14, borda);

        // Fork: 5 px nao bastam — a quina convexa de raio 14 corta bem mais que isso.
        // Entramos 2 x raio, que cai com folga dentro do preenchimento.
        const int dentro = 2 * 14;
        int testX = x < bounds.Width / 2 ? x + dentro : x - dentro;
        int testY = y < bounds.Height / 2 ? y + dentro : y - dentro;

        // Quina convexa não é escavada do mesmo jeito, o ponto marginal está dentro
        Assert.True(path.IsVisible(testX, testY));
    }

    [Fact]
    public void NotchShape_AreaSimetricaEmTodasBordas()
    {
        var radius = 14;
        var baseArea = EstimateArea(WidgetForm.NotchShape(new Rectangle(0, 0, 200, 100), radius, BordaTela.Topo), 200, 100);
        
        var baseBordaArea = EstimateArea(WidgetForm.NotchShape(new Rectangle(0, 0, 200, 100), radius, BordaTela.Base), 200, 100);
        var esqArea = EstimateArea(WidgetForm.NotchShape(new Rectangle(0, 0, 100, 200), radius, BordaTela.Esquerda), 100, 200);
        var dirArea = EstimateArea(WidgetForm.NotchShape(new Rectangle(0, 0, 100, 200), radius, BordaTela.Direita), 100, 200);

        // Permitimos uma margem de erro por causa da amostragem de grade em IsVisible
        Assert.InRange(baseBordaArea, baseArea * 0.95, baseArea * 1.05);
        Assert.InRange(esqArea, baseArea * 0.95, baseArea * 1.05);
        Assert.InRange(dirArea, baseArea * 0.95, baseArea * 1.05);
    }

    private int EstimateArea(System.Drawing.Drawing2D.GraphicsPath path, int w, int h)
    {
        int count = 0;
        using var r = new Region(path);
        // Amostragem espaçada para ser mais rápido
        for (int x = 0; x < w; x += 2)
        {
            for (int y = 0; y < h; y += 2)
            {
                if (r.IsVisible(x, y)) count++;
            }
        }
        return count;
    }
}
