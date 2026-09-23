using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RateTray.Ui;

/// <summary>
/// Fork: a marca do Gaugely — um G que é também um mostrador, com o ponteiro no miolo.
/// Desenhada em código, e não guardada como imagem, para existir uma fonte só: o <c>app.ico</c>
/// (gerado por <c>--render-icon</c>), a marca da janela de ajustes e o ícone neutro da bandeja
/// saem daqui. Geometria em frações do raio externo do G, tirada do desenho de referência.
/// </summary>
internal static class GaugelyMark
{
    public static readonly Color Blue = Color.FromArgb(0x47, 0xC8, 0xFA);
    public static readonly Color Plate = Color.FromArgb(0x17, 0x19, 0x1D);

    // Anel do G: raio interno 0,7125 R; começa logo abaixo da barra e fecha em cima, à direita.
    private const float Inner = 0.7125f;
    private const float ArcStart = 5f;
    private const float ArcSweep = 345f;

    // O terminal de cima do G é um corte horizontal, não radial: o arco passa um pouco da conta e
    // é recortado nesta altura (fração de R acima do centro).
    private const float TopCut = 0.4125f;

    // Barra do G, colada à ponta de baixo do anel.
    private const float BarLeft = 0.2125f, BarTop = -0.05f, BarBottom = 0.1875f;

    // Ponteiro: miolo levemente à esquerda do centro, ponta para cima e para a direita.
    private const float HubX = -0.0625f, HubRadius = 0.155f;
    private const float TipX = 0.4125f, TipY = -0.475f;
    private const float BaseHalf = 0.09f, TipHalf = 0.028f;

    /// <summary>O G dentro de <paramref name="box"/> (quadrado), sem placa de fundo.</summary>
    /// <param name="needle">Sem ponteiro abaixo de ~20 px, onde ele vira ruído.</param>
    public static void Draw(Graphics g, RectangleF box, Color color, bool needle = true)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var r = Math.Min(box.Width, box.Height) / 2f;
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        var stroke = r * (1 - Inner);
        var mid = r - stroke / 2f;

        using var brush = new SolidBrush(color);
        var beforeArc = g.Save();
        using (var cut = new Region(new RectangleF(cx, cy - TopCut * r, r * 1.5f, (TopCut + BarTop) * r)))
            g.ExcludeClip(cut);
        using (var pen = new Pen(color, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
            g.DrawArc(pen, cx - mid, cy - mid, mid * 2, mid * 2, ArcStart, ArcSweep);
        g.Restore(beforeArc);

        g.FillRectangle(brush, cx + BarLeft * r, cy + BarTop * r, (1 - BarLeft) * r - stroke * 0.02f, (BarBottom - BarTop) * r);

        if (needle)
        {
            var hub = new PointF(cx + HubX * r, cy);
            var tip = new PointF(cx + TipX * r, cy + TipY * r);
            var dx = tip.X - hub.X;
            var dy = tip.Y - hub.Y;
            var len = MathF.Sqrt(dx * dx + dy * dy);
            var nx = -dy / len;                    // normal ao ponteiro
            var ny = dx / len;

            using var path = new GraphicsPath();
            path.AddPolygon(
            [
                new PointF(hub.X + nx * BaseHalf * r, hub.Y + ny * BaseHalf * r),
                new PointF(tip.X + nx * TipHalf * r, tip.Y + ny * TipHalf * r),
                new PointF(tip.X - nx * TipHalf * r, tip.Y - ny * TipHalf * r),
                new PointF(hub.X - nx * BaseHalf * r, hub.Y - ny * BaseHalf * r),
            ]);
            g.FillPath(brush, path);
            g.FillEllipse(brush, tip.X - TipHalf * r, tip.Y - TipHalf * r, TipHalf * 2 * r, TipHalf * 2 * r);
            g.FillEllipse(brush, hub.X - HubRadius * r, hub.Y - HubRadius * r, HubRadius * 2 * r, HubRadius * 2 * r);
        }

        g.Restore(state);
    }

    /// <summary>Ícone do app num tamanho: placa escura arredondada com o G azul.</summary>
    public static Bitmap IconBitmap(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var inset = Math.Max(0.5f, size * 0.02f);
        using (var plate = Settings.Shapes.Rounded(new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset), size * 0.22f))
        using (var fill = new SolidBrush(Plate))
            g.FillPath(fill, plate);

        // O G ocupa 80 % da placa; nos tamanhos pequenos um pouco mais, para não sumir.
        var fraction = size <= 24 ? 0.86f : 0.80f;
        var side = size * fraction;
        Draw(g, new RectangleF((size - side) / 2f, (size - side) / 2f, side, side), Blue, needle: size >= 24);
        return bmp;
    }

    /// <summary>Tamanhos que o Windows escolhe para barra de tarefas, alt-tab, Explorer e atalho.</summary>
    public static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>Um .ico com uma imagem PNG por tamanho (formato aceito desde o Windows Vista).</summary>
    public static byte[] CreateIco(IReadOnlyList<int> sizes)
    {
        var images = sizes.Select(size =>
        {
            using var bmp = IconBitmap(size);
            using var png = new MemoryStream();
            bmp.Save(png, ImageFormat.Png);
            return (Size: size, Data: png.ToArray());
        }).ToList();

        using var ico = new MemoryStream();
        using var w = new BinaryWriter(ico);
        w.Write((ushort)0);                        // reservado
        w.Write((ushort)1);                        // tipo: ícone
        w.Write((ushort)images.Count);

        var offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));   // 0 = 256
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);                          // sem paleta
            w.Write((byte)0);
            w.Write((ushort)1);                        // planos
            w.Write((ushort)32);                       // bits por pixel
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in images) w.Write(data);
        w.Flush();
        return ico.ToArray();
    }
}
