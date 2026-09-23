using System.Drawing;
using RateTray.Configuration;
using RateTray.Ui;

namespace RateTray.Tests;

/// <summary>Fork: marca G medidor (ícone gerado em código) e as três formas de mostrador.</summary>
public class ForkMarcaEMostradorTests
{
    [Theory]
    [InlineData(null, DialStyle.Gauge)]
    [InlineData("", DialStyle.Gauge)]
    [InlineData("gauge", DialStyle.Gauge)]
    [InlineData(" Semicircle ", DialStyle.Semicircle)]
    [InlineData("SEGMENTED", DialStyle.Segmented)]
    [InlineData("pizza", DialStyle.Gauge)]
    public void Mostrador_desconhecido_vira_o_padrao(string? valor, DialStyle esperado) =>
        Assert.Equal(esperado, Dial.Parse(valor));

    [Fact]
    public void Arquivo_de_ajustes_normaliza_o_mostrador()
    {
        Assert.Equal("gauge", ConfigStore.FromJson("""{ "widget": { "dial": "qualquer" } }""").Widget.Dial);
        Assert.Equal("segmented", ConfigStore.FromJson("""{ "widget": { "dial": "Segmented" } }""").Widget.Dial);
        Assert.Equal("gauge", ConfigStore.FromJson("{}").Widget.Dial);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.4, 1)]      // qualquer uso acende um segmento
    [InlineData(4, 1)]
    [InlineData(15, 2)]
    [InlineData(76, 8)]
    [InlineData(100, 10)]
    [InlineData(250, 10)]
    public void Segmentos_acesos(double percentual, int esperado) =>
        Assert.Equal(esperado, Dial.FilledSegments(percentual));

    [Fact]
    public void Icone_tem_uma_imagem_png_por_tamanho()
    {
        var ico = GaugelyMark.CreateIco(GaugelyMark.IconSizes);

        Assert.Equal(0, BitConverter.ToUInt16(ico, 0));
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2));
        var count = BitConverter.ToUInt16(ico, 4);
        Assert.Equal(GaugelyMark.IconSizes.Length, count);

        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            var expected = GaugelyMark.IconSizes[i];
            Assert.Equal(expected >= 256 ? 0 : expected, ico[entry]);
            var length = BitConverter.ToInt32(ico, entry + 8);
            var offset = BitConverter.ToInt32(ico, entry + 12);
            Assert.True(offset + length <= ico.Length);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, ico[offset..(offset + 4)]);
        }

        using var icon = new Icon(new MemoryStream(ico), 32, 32);
        Assert.Equal(32, icon.Width);
    }

    [Fact]
    public void Marca_pinta_o_G_no_meio_e_deixa_o_canto_livre()
    {
        using var bmp = GaugelyMark.IconBitmap(256);
        Assert.Equal(0, bmp.GetPixel(0, 0).A);                     // canto fora da placa arredondada
        var leftOfG = bmp.GetPixel((int)(128 - 0.4 * 256 * 0.85), 128); // dentro do anel, à esquerda
        Assert.Equal(GaugelyMark.Blue.ToArgb(), leftOfG.ToArgb());
    }
}
