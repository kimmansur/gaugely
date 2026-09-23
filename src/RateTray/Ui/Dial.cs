using System.Drawing.Drawing2D;

namespace RateTray.Ui;

/// <summary>Fork: forma do mostrador de cada serviço na faixa flutuante.</summary>
public enum DialStyle
{
    /// <summary>Arco de 300° aberto embaixo, como o G da marca — o padrão.</summary>
    Gauge,

    /// <summary>Meia-lua de velocímetro, da esquerda para a direita por cima.</summary>
    Semicircle,

    /// <summary>Anel em 10 segmentos; cada um vale 10 %.</summary>
    Segmented,
}

/// <summary>
/// Fork: desenha um mostrador. Separado da faixa para a janela de ajustes mostrar a prévia com
/// o mesmo código que a faixa usa.
/// </summary>
internal static class Dial
{
    internal const float GaugeStart = 120f;
    internal const float GaugeSweep = 300f;
    internal const int Segments = 10;
    private const float SegmentGap = 8f;

    /// <param name="square">Área quadrada do mostrador.</param>
    /// <param name="percent">Nulo para leitura sem percentual (saldo em dinheiro): só o trilho.</param>
    public static void Draw(Graphics g, Rectangle square, DialStyle style, float stroke, Color color, Color track, double? percent)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var p = percent is { } value ? Math.Clamp(value, 0, 100) : (double?)null;

        switch (style)
        {
            case DialStyle.Semicircle:
            {
                // O círculo desce para a meia-lua ficar centrada na área.
                var d = square.Width - stroke;
                var arc = new RectangleF(square.Left + stroke / 2f, square.Top + stroke / 2f + square.Height * 0.2f, d, d);
                using (var pen = new Pen(track, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, arc, 180, 180);
                if (p > 0)
                {
                    using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawArc(pen, arc, 180, (float)Math.Max(3, 180 * p.Value / 100));
                }

                break;
            }

            case DialStyle.Segmented:
            {
                var arc = RectangleF.Inflate(square, -stroke / 2f, -stroke / 2f);
                var filled = p is { } v ? FilledSegments(v) : 0;
                var each = 360f / Segments;
                for (var i = 0; i < Segments; i++)
                {
                    using var pen = new Pen(i < filled ? color : track, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                    g.DrawArc(pen, arc, -90 + i * each + SegmentGap / 2, each - SegmentGap);
                }

                break;
            }

            default:
            {
                var arc = RectangleF.Inflate(square, -stroke / 2f, -stroke / 2f);
                using (var pen = new Pen(track, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, arc, GaugeStart, GaugeSweep);
                if (p > 0)
                {
                    using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawArc(pen, arc, GaugeStart, (float)Math.Max(4, GaugeSweep * p.Value / 100));
                }

                break;
            }
        }

        g.Restore(state);
    }

    /// <summary>Onde vai o logo do serviço dentro do mostrador.</summary>
    public static RectangleF BadgeBox(Rectangle square, DialStyle style, float badge) => style == DialStyle.Semicircle
        ? new RectangleF(square.Left + (square.Width - badge) / 2f, square.Top + square.Height * 0.62f - badge / 2f, badge, badge)
        : new RectangleF(square.Left + (square.Width - badge) / 2f, square.Top + (square.Height - badge) / 2f, badge, badge);

    /// <summary>Segmentos acesos: arredonda, mas qualquer uso acima de zero acende ao menos um.</summary>
    internal static int FilledSegments(double percent)
    {
        if (percent <= 0) return 0;
        return Math.Clamp((int)Math.Round(percent / (100.0 / Segments), MidpointRounding.AwayFromZero), 1, Segments);
    }

    /// <summary>Lê o valor do settings.json; desconhecido vira o padrão.</summary>
    internal static DialStyle Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "semicircle" => DialStyle.Semicircle,
        "segmented" => DialStyle.Segmented,
        _ => DialStyle.Gauge,
    };

    internal static string Name(DialStyle style) => style switch
    {
        DialStyle.Semicircle => "semicircle",
        DialStyle.Segmented => "segmented",
        _ => "gauge",
    };
}
