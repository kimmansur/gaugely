using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RateTray.Ui;

/// <summary>
/// Small mark drawn next to a service name in the details window, tinted with that service's
/// colour so the group header ties back to its tray icons.
///
/// Fork: a marca é o logo de cada serviço, só para identificá-lo (ver Logos/NOTICE.md). O desenho genérico do upstream — faísca e prompt — fica como reserva para um
/// serviço sem logo embutido ou um recurso que não carregue.
/// </summary>
public static class ServiceBadge
{
    /// <summary>
    /// Bitmaps decodificados uma vez só: o widget redesenha a cada atualização e o cartão a cada
    /// passada do mouse, e decodificar PNG nesse ritmo é desperdício.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Image?> Logos = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="darkBackground">
    /// Kimi e OpenRouter têm traço monocromático; o logo claro some num fundo claro, então cada
    /// um tem as duas variantes.
    /// </param>
    public static void Draw(Graphics g, RectangleF bounds, string group, Color color, bool darkBackground = true)
    {
        if (LogoFor(group, darkBackground) is { } logo)
        {
            DrawLogo(g, bounds, logo);
            return;
        }

        using (var back = new SolidBrush(Color.FromArgb(48, color)))
        using (var plate = Rounded(bounds, bounds.Width * 0.30f))
            g.FillPath(back, plate);

        var inner = RectangleF.Inflate(bounds, -bounds.Width * 0.28f, -bounds.Height * 0.28f);

        switch (group.ToLowerInvariant())
        {
            case "codex": DrawPrompt(g, inner, color); break;
            default: DrawSpark(g, inner, color); break;
        }
    }

    private static Image? LogoFor(string group, bool dark)
    {
        var record = RateTray.Model.ServiceCatalog.FindByGroup(group);
        if (record is null) return null;

        var key = record.HasDarkVariant && dark ? $"{record.LogoKey}-dark" : record.HasDarkVariant ? $"{record.LogoKey}-light" : record.LogoKey;
        return Logos.GetOrAdd(key, Load);
    }

    private static Image? Load(string name)
    {
        try
        {
            using var stream = typeof(ServiceBadge).Assembly.GetManifestResourceStream($"Logos.{name}.png");
            if (stream is null) return null;

            // Cópia desacoplada do stream: um Bitmap criado direto dele exige o stream aberto
            // pela vida inteira da imagem.
            using var decoded = new Bitmap(stream);
            return new Bitmap(decoded);
        }
        catch (ArgumentException)
        {
            return null;                                 // recurso corrompido: cai no desenho genérico
        }
    }

    /// <summary>Encaixa o logo no quadrado mantendo a proporção, centralizado, com reamostragem boa.</summary>
    private static void DrawLogo(Graphics g, RectangleF bounds, Image logo)
    {
        var scale = Math.Min(bounds.Width / logo.Width, bounds.Height / logo.Height);
        var w = logo.Width * scale;
        var h = logo.Height * scale;
        var target = new RectangleF(bounds.Left + (bounds.Width - w) / 2f, bounds.Top + (bounds.Height - h) / 2f, w, h);

        var interpolation = g.InterpolationMode;
        var pixelOffset = g.PixelOffsetMode;
        var compositing = g.CompositingQuality;
        try
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            // TileFlipXY evita a franja semitransparente que o bicúbico puxa da borda da imagem.
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(logo, Rectangle.Round(target), 0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, attributes);
        }
        finally
        {
            g.InterpolationMode = interpolation;
            g.PixelOffsetMode = pixelOffset;
            g.CompositingQuality = compositing;
        }
    }

    /// <summary>Four-pointed spark: straight diagonals pinched towards the centre.</summary>
    private static void DrawSpark(Graphics g, RectangleF r, Color color)
    {
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
        float rx = r.Width / 2f, ry = r.Height / 2f;
        var pinch = 0.16f;   // lower = sharper points

        using var path = new GraphicsPath();
        PointF top = new(cx, cy - ry), right = new(cx + rx, cy), bottom = new(cx, cy + ry), left = new(cx - rx, cy);

        AddPinched(path, top, right, cx, cy, rx * pinch, ry * pinch, 1, -1);
        AddPinched(path, right, bottom, cx, cy, rx * pinch, ry * pinch, 1, 1);
        AddPinched(path, bottom, left, cx, cy, rx * pinch, ry * pinch, -1, 1);
        AddPinched(path, left, top, cx, cy, rx * pinch, ry * pinch, -1, -1);
        path.CloseFigure();

        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static void AddPinched(GraphicsPath path, PointF from, PointF to,
        float cx, float cy, float dx, float dy, int signX, int signY)
    {
        // Both control points sit near the centre, so the edge bows inward instead of bulging.
        var control = new PointF(cx + dx * signX, cy + dy * signY);
        path.AddBezier(from, control, control, to);
    }

    /// <summary>Terminal prompt: a chevron and an underscore.</summary>
    private static void DrawPrompt(Graphics g, RectangleF r, Color color)
    {
        using var pen = new Pen(color, Math.Max(1.3f, r.Width * 0.17f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        g.DrawLines(pen,
        [
            new PointF(r.Left, r.Top + r.Height * 0.06f),
            new PointF(r.Left + r.Width * 0.46f, r.Top + r.Height * 0.5f),
            new PointF(r.Left, r.Bottom - r.Height * 0.06f),
        ]);

        g.DrawLine(pen, r.Left + r.Width * 0.60f, r.Bottom, r.Right, r.Bottom);
    }

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
