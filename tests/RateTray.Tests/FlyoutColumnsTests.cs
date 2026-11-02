using RateTray.Ui;

namespace RateTray.Tests;

public class FlyoutColumnsTests
{
    private static int Tallest(IReadOnlyList<IReadOnlyList<int>> columns, int[] heights) =>
        columns.Max(column => column.Sum(index => heights[index]));

    [Fact]
    public void Tudo_cabe_fica_em_uma_coluna()
    {
        Assert.Single(FlyoutPlacement.Columns([100, 100, 100], 400, 3));
    }

    [Fact]
    public void Nao_cabe_abre_a_segunda_coluna_sem_perder_nem_reordenar()
    {
        int[] alturas = [100, 200, 150, 120, 180];

        var colunas = FlyoutPlacement.Columns(alturas, 460, 3);

        Assert.Equal(2, colunas.Count);
        Assert.Equal([0, 1, 2, 3, 4], colunas.SelectMany(c => c));
        Assert.True(Tallest(colunas, alturas) <= 460);
    }

    [Fact]
    public void Colunas_saem_equilibradas()
    {
        int[] alturas = [100, 100, 100, 100];

        Assert.Equal(200, Tallest(FlyoutPlacement.Columns(alturas, 300, 3), alturas));
    }

    [Fact]
    public void Tela_estreita_respeita_o_limite_de_colunas()
    {
        Assert.Single(FlyoutPlacement.Columns([300, 300, 300], 400, 1));
        Assert.Equal(2, FlyoutPlacement.Columns([300, 300, 300, 300, 300], 400, 2).Count);
    }

    [Fact]
    public void Bloco_maior_que_a_tela_nao_trava()
    {
        Assert.Equal(2, FlyoutPlacement.Columns([900, 100], 400, 3).Count);
    }

    [Fact]
    public void Tela_pequena_rola_o_que_as_colunas_nao_acomodam()
    {
        int[] alturas = Enumerable.Repeat(128, 11).ToArray();
        var colunas = FlyoutPlacement.Columns(alturas, 660, 2);
        var conteudo = 14 + 30 + Tallest(colunas, alturas);

        Assert.Equal(11, colunas.Sum(c => c.Count));
        Assert.Equal(conteudo - 704, FlyoutPlacement.MaxScroll(conteudo, 704));
        Assert.True(FlyoutPlacement.MaxScroll(conteudo, 704) > 0);
    }

    [Fact]
    public void Sem_sobra_nao_ha_rolagem()
    {
        Assert.Equal(0, FlyoutPlacement.MaxScroll(500, 704));
    }

    [Fact]
    public void Sem_servicos_nao_ha_colunas()
    {
        Assert.Empty(FlyoutPlacement.Columns([], 400, 3));
    }
}
