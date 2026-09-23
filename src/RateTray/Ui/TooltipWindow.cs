using System.Drawing.Drawing2D;
using RateTray.Configuration;
using RateTray.Model;

namespace RateTray.Ui;

/// <summary>
/// Hover card shown next to a tray icon.
///
/// Windows tray tooltips are plain text — <c>NOTIFYICONDATA.szTip</c> holds no image and is
/// capped at 63 characters by WinForms — so showing the service mark next to the value means
/// drawing our own popup. It never takes focus (WS_EX_NOACTIVATE), otherwise hovering a tray
/// icon would deactivate whatever the user is working in.
/// </summary>
public sealed class TooltipWindow : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopMost = 0x00000008;
    private const int WsExTransparent = 0x00000020;

    private readonly AppConfig _config;
    private readonly Palette _palette;

    private LimitReading? _reading;
    private string _group = "";
    private string? _error;
    private int _dpi = 96;

    private Font _labelFont = null!;
    private Font _valueFont = null!;
    private Font _smallFont = null!;

    public TooltipWindow(AppConfig config, Palette palette)
    {
        _config = config;
        _palette = palette;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        Enabled = false;                 // purely decorative: never accepts input

        BuildFonts();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopMost | WsExTransparent;
            return cp;
        }
    }

    private bool Dark => TrayIconRenderer.UsesDarkTaskbar(_config.Theme);
    private Color Background => Dark ? Color.FromArgb(38, 40, 46) : Color.FromArgb(252, 252, 253);
    private Color Foreground => Dark ? Color.FromArgb(236, 238, 242) : Color.FromArgb(26, 28, 32);
    private Color Muted => Dark ? Color.FromArgb(154, 160, 170) : Color.FromArgb(107, 114, 128);
    private Color BorderColor => Dark ? Color.FromArgb(66, 70, 79) : Color.FromArgb(210, 215, 224);

    private int Px(int value) => (int)Math.Round(value * (_dpi / 96.0));

    /// <summary>Fork: linhas do cartão de serviço; vazia no cartão de um limite só.</summary>
    private IReadOnlyList<LimitReading> _groupRows = [];

    private const int GroupRowHeight = 21;

    public void ShowFor(LimitReading? reading, string group, string? error, Point cursor)
    {
        _groupRows = [];
        Present(reading, group, error, cursor);
    }

    /// <summary>
    /// Fork: cartão de um serviço inteiro — o maior percentual no cabeçalho, uma linha por janela
    /// abaixo e, no rodapé, quando zera a que trava primeiro.
    /// </summary>
    // Fork: serviço só com saldo (OpenRouter) não tem limite que trava; o cabeçalho mostra o saldo.
    public void ShowForGroup(IReadOnlyList<LimitReading> rows, string group, string? error, Point cursor)
    {
        _groupRows = rows;
        Present((ServiceGroup.Binding(rows) ?? rows.FirstOrDefault(r => r.IsInformational)), group, error, cursor);
    }

    /// <summary>
    /// Fork: sobrecarga para a faixa flutuante — o cartão abre à esquerda ou à direita do
    /// <paramref name="widgetRect"/> (o retângulo completo da faixa em coordenadas de tela),
    /// alinhado verticalmente ao <paramref name="anchorRect"/> (o retângulo do anel).
    /// </summary>
    public void ShowForGroup(IReadOnlyList<LimitReading> rows, string group, string? error,
                             Rectangle anchorRect, Rectangle widgetRect)
    {
        _groupRows = rows;
        PresentBesideWidget((ServiceGroup.Binding(rows) ?? rows.FirstOrDefault(r => r.IsInformational)), group, error, anchorRect, widgetRect);
    }

    /// <summary>
    /// Fork: posiciona o cartão ao lado da faixa. Se a faixa está na metade direita da tela,
    /// o cartão abre à esquerda; caso contrário, à direita. Sem sair da área de trabalho.
    /// </summary>
    private void PresentBesideWidget(LimitReading? reading, string group, string? error,
                                     Rectangle anchorRect, Rectangle widgetRect)
    {
        _reading = reading;
        _group = group;
        _error = error;

        var refPoint = new Point(anchorRect.Left + anchorRect.Width / 2,
                                 anchorRect.Top + anchorRect.Height / 2);
        _dpi = Native.DpiForPoint(refPoint);
        BuildFonts();

        var screen = Screen.FromPoint(refPoint);
        var work = screen.WorkingArea;
        var size = Measure();
        var gap = Px(6);
        var pad = Px(4);

        int x, y;

        if (widgetRect.Width > widgetRect.Height)
        {
            // Fork: faixa horizontal -> cartão acima se na metade inferior, abaixo se na superior
            var widgetCenterY = widgetRect.Top + widgetRect.Height / 2;
            var areaCenterY = work.Top + work.Height / 2;

            y = widgetCenterY >= areaCenterY
                ? widgetRect.Top - size.Height - gap      // acima
                : widgetRect.Bottom + gap;                // abaixo

            // alinhado horizontalmente pelo centro do anel
            x = anchorRect.Left + anchorRect.Width / 2 - size.Width / 2;
        }
        else
        {
            // Fork: faixa vertical -> cartão à esquerda se na metade direita, à direita se na esquerda
            var widgetCenterX = widgetRect.Left + widgetRect.Width / 2;
            var areaCenterX = work.Left + work.Width / 2;

            x = widgetCenterX >= areaCenterX
                ? widgetRect.Left - size.Width - gap      // à esquerda
                : widgetRect.Right + gap;                  // à direita

            // alinhado verticalmente pelo centro do anel
            y = anchorRect.Top + anchorRect.Height / 2 - size.Height / 2;
        }

        // Clamped para não sair da área de trabalho.
        x = Math.Clamp(x, work.Left + pad, Math.Max(work.Left + pad, work.Right - size.Width - pad));
        y = Math.Clamp(y, work.Top + pad, Math.Max(work.Top + pad, work.Bottom - size.Height - pad));

        Bounds = new Rectangle(x, y, size.Width, size.Height);

        if (!Visible) Show();
        Invalidate();
    }

    private void Present(LimitReading? reading, string group, string? error, Point cursor)
    {
        _reading = reading;
        _group = group;
        _error = error;

        _dpi = Native.DpiForPoint(cursor);
        BuildFonts();

        var screen = Screen.FromPoint(cursor);
        var work = screen.WorkingArea;
        var size = Measure();
        var gap = Px(6);
        var pad = Px(4);

        var taskbar = Native.TaskbarRect();
        int x, y;

        // A tray icon is hovered either on the taskbar itself or inside the overflow flyout that
        // pops up from the chevron. On the taskbar we anchor to its edge so the card sits at a
        // steady height like the details fly-out; in the flyout the icon is nowhere near that
        // edge, so there we follow the pointer — otherwise the card detaches from the icon.
        if (taskbar is { } bar && bar.Contains(cursor))
        {
            switch (FlyoutPlacement.EdgeOf(bar, screen.Bounds))
            {
                case FlyoutPlacement.Edge.Top:
                    x = cursor.X - size.Width / 2; y = work.Top + gap; break;
                case FlyoutPlacement.Edge.Left:
                    x = work.Left + gap; y = cursor.Y - size.Height / 2; break;
                case FlyoutPlacement.Edge.Right:
                    x = work.Right - size.Width - gap; y = cursor.Y - size.Height / 2; break;
                default:
                    x = cursor.X - size.Width / 2; y = work.Bottom - size.Height - gap; break;
            }
        }
        else if (Native.WindowRectAt(cursor) is { } flyout && flyout.Contains(cursor))
        {
            // Overflow flyout (the "^" popup): sit just above the whole flyout, LEFT-aligned to its
            // visible edge — the way the shell's own tooltips appear there — never covering it. The
            // reported window left sits a little outside the visible rounded box (corner margin), so
            // nudge right to line the card up with the box edge.
            y = flyout.Top - size.Height - gap;
            x = flyout.Left + Px(11);
        }
        else
        {
            // Fallback (flyout rect unavailable): sit just above the pointer, dropping below it
            // only when there is no room above.
            x = cursor.X - size.Width / 2;
            y = cursor.Y - size.Height - Px(14);
            if (y < work.Top + pad) y = cursor.Y + Px(20);
        }

        // Clamped so the card never leaves the work area.
        x = Math.Clamp(x, work.Left + pad, Math.Max(work.Left + pad, work.Right - size.Width - pad));
        y = Math.Clamp(y, work.Top + pad, Math.Max(work.Top + pad, work.Bottom - size.Height - pad));

        Bounds = new Rectangle(x, y, size.Width, size.Height);

        if (!Visible) Show();
        Invalidate();
    }

    private void BuildFonts()
    {
        var family = SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif;

        _labelFont?.Dispose();
        _valueFont?.Dispose();
        _smallFont?.Dispose();

        _labelFont = new Font(family, Px(13), FontStyle.Regular, GraphicsUnit.Pixel);
        _valueFont = new Font(family, Px(15), FontStyle.Bold, GraphicsUnit.Pixel);
        _smallFont = new Font(family, Px(11), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private Size Measure()
    {
        using var g = CreateGraphics();

        if (_groupRows.Count > 0)
        {
            // Fork: evita "0 %" no cabeçalho se não há leitura que trava, e usa o formatador para medir
            var cabecaValor = _reading is { } rCabeca ? LimitReading.FormatValue(rCabeca) : "";
            var cabecaExtra = cabecaValor.Length > 0 ? g.MeasureString(cabecaValor, _valueFont).Width + Px(16) : 0;
            var cabeca = g.MeasureString(_group, _labelFont).Width + cabecaExtra;
            var linhas = _groupRows.Max(r =>
                g.MeasureString(r.Label, _labelFont).Width + g.MeasureString(LimitReading.FormatValue(r), _labelFont).Width + Px(16));
            var rodape = _error ?? _reading?.ResetText() ?? "";
            var rodapeLargura = rodape.Length == 0 ? 0 : g.MeasureString(rodape, _smallFont).Width;

            var largura = (int)Math.Ceiling(new[] { cabeca, linhas, rodapeLargura }.Max()) + Px(20) + Px(22);
            var altura = Px(34) + _groupRows.Count * Px(GroupRowHeight) + (rodape.Length == 0 ? Px(6) : Px(24));
            return new Size(Math.Clamp(largura, Px(180), Px(460)), altura);
        }

        var head = _reading?.Label ?? _group;
        var value = _reading is { } r ? LimitReading.FormatValue(r) : "?";
        var detail = _error ?? _reading?.ResetText() ?? "";

        var headWidth = g.MeasureString(head, _labelFont).Width + g.MeasureString(value, _valueFont).Width + Px(16);
        var detailWidth = string.IsNullOrEmpty(detail) ? 0 : g.MeasureString(detail, _smallFont).Width;

        var width = (int)Math.Ceiling(Math.Max(headWidth, detailWidth)) + Px(20) + Px(22);
        var height = Px(string.IsNullOrEmpty(detail) ? 34 : 52);

        return new Size(Math.Clamp(width, Px(160), Px(460)), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var back = new SolidBrush(Background)) g.FillRectangle(back, ClientRectangle);
        using (var border = new Pen(BorderColor)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var accent = Harmony.Legible(_palette.Service(_group), Dark);
        var pad = Px(10);

        ServiceBadge.Draw(g, new RectangleF(pad, Px(9), Px(16), Px(16)), _group, accent, Dark);

        var textLeft = pad + Px(22);
        var head = _reading?.Label ?? _group;

        // The card is width-clamped, so anything longer than it — an error carrying a server's
        // answer above all — is drawn to the edge and cut with an ellipsis, never past it.
        var value = _reading is { } rDraw ? LimitReading.FormatValue(rDraw) : null;
        var valueWidth = value is null ? 0f : g.MeasureString(value, _valueFont).Width;

        using (var brush = new SolidBrush(Foreground))
            TextLine.Draw(g, head, _labelFont, brush, textLeft, Px(9), Width - pad - textLeft - valueWidth - Px(6));

        if (_reading is { } reading && value is not null)
        {
            var color = _palette.ForReading(reading.Group, reading.Percent, reading.Variant, reading.VariantCount, Dark);

            using var brush = new SolidBrush(color);
            g.DrawString(value, _valueFont, brush, Width - pad - valueWidth, Px(7));
        }

        var detail = _error ?? _reading?.ResetText();

        if (_groupRows.Count > 0)
        {
            // Fork: uma linha por janela, rótulo à esquerda e valor na cor do próprio limite.
            var y = Px(33);
            using var rotulo = new SolidBrush(Muted);
            foreach (var linha in _groupRows)
            {
                var valor = LimitReading.FormatValue(linha);
                var larguraValor = g.MeasureString(valor, _labelFont).Width;
                TextLine.Draw(g, linha.Label, _labelFont, rotulo, textLeft, y, Width - pad - textLeft - larguraValor - Px(6));

                using var cor = new SolidBrush(_palette.ForReading(linha.Group, linha.Percent, linha.Variant, linha.VariantCount, Dark));
                g.DrawString(valor, _labelFont, cor, Width - pad - larguraValor, y);
                y += Px(GroupRowHeight);
            }

            if (string.IsNullOrEmpty(detail)) return;
            using var rodape = new SolidBrush(_error is null ? Muted : Harmony.Legible(_palette.Critical, Dark));
            TextLine.Draw(g, detail, _smallFont, rodape, textLeft, y + Px(2), Width - pad - textLeft);
            return;
        }

        if (string.IsNullOrEmpty(detail)) return;

        using var detailBrush = new SolidBrush(_error is null ? Muted : Harmony.Legible(_palette.Critical, Dark));
        TextLine.Draw(g, detail, _smallFont, detailBrush, textLeft, Px(30), Width - pad - textLeft);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _labelFont?.Dispose();
            _valueFont?.Dispose();
            _smallFont?.Dispose();
        }

        base.Dispose(disposing);
    }
}
