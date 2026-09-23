using System.Drawing.Drawing2D;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;

namespace RateTray.Ui;

/// <summary>
/// Fly-out panel listing every limit with a progress bar, its reset time and the validity of
/// the login it was read with. Fully owner-drawn so it can follow the Windows light/dark theme
/// without pulling in a UI framework.
///
/// All metrics derive from the DPI of the monitor the fly-out lands on, not from the form's
/// own <c>DeviceDpi</c> — the window is sized before it is moved there, so on a mixed 4K/HD
/// desktop its own DPI is still the previous monitor's. Fonts are sized in pixels for the
/// same reason: point sizes would be scaled a second time by the DPI the form ends up at.
/// </summary>
public sealed class DetailsForm : Form
{
    private const int BaseWidth = 560;
    private const int EdgeMargin = 12;

    private readonly AppConfig _config;
    private readonly Palette _palette;

    // Fork: Item 8 - cache do resultado e do LINQ do grupo para evitar recálculo no paint
    private IReadOnlyList<(ProviderResult Result, IReadOnlyList<LimitReading> Readings)> _cachedResults = [];
    private DateTimeOffset? _lastUpdate;
    private DateTimeOffset? _nextPoll;
    private int _dpi = 96;

    /// <summary>
    /// Redraws only the countdown strip. 250 ms keeps a 60-second sweep from advancing in
    /// visible jumps, and repainting a two-pixel band costs nothing.
    /// </summary>
    private readonly System.Windows.Forms.Timer _countdown = new() { Interval = 250 };

    private Font _titleFont = null!;
    private Font _labelFont = null!;
    private Font _smallFont = null!;
    private Font _valueFont = null!;
    private Font _headFont = null!;

    public DetailsForm(AppConfig config, Palette palette)
    {
        _config = config;
        _palette = palette;

        Text = "Gaugely Details";               // window title, used by the e2e smoke test
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;           // every metric is scaled explicitly below
        DoubleBuffered = true;
        KeyPreview = true;

        BuildFonts();
        Deactivate += (_, _) => { if (AutoHide) { LastAutoHidden = DateTime.UtcNow; Hide(); } };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        // A click anywhere in the panel dismisses it, the way a tray fly-out should.
        MouseClick += (_, _) => Hide();

        _countdown.Tick += (_, _) => InvalidateCountdown();
        VisibleChanged += (_, _) =>
        {
            if (Visible) _countdown.Start();
            else _countdown.Stop();
        };
    }

    /// <summary>Off in the --details preview, where losing focus must not close the window.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AutoHide { get; init; } = true;

    /// <summary>
    /// When the window last hid itself on losing focus. A tray-icon click deactivates it before
    /// the click handler runs, so <see cref="TrayApp"/> reads this to tell "the click that just
    /// closed it" apart from "a fresh click meant to open it".
    /// </summary>
    public DateTime LastAutoHidden { get; private set; } = DateTime.MinValue;

    private bool Dark => TrayIconRenderer.UsesDarkTaskbar(_config.Theme);

    private Color Background => Dark ? Color.FromArgb(31, 33, 38) : Color.White;
    private Color Foreground => Dark ? Color.FromArgb(236, 238, 242) : Color.FromArgb(26, 28, 32);
    private Color Muted => Dark ? Color.FromArgb(154, 160, 170) : Color.FromArgb(107, 114, 128);
    private Color BorderColor => Dark ? Color.FromArgb(58, 61, 69) : Color.FromArgb(214, 219, 228);

    private int Px(int value) => (int)Math.Round(value * (_dpi / 96.0));

    /// <param name="nextPoll">
    /// When the next poll is due, drawn as the countdown strip along the bottom edge. Null
    /// hides the strip — there is nothing to count down to.
    /// </param>
    public void ShowNearTray(IReadOnlyList<ProviderResult> results, DateTimeOffset? lastUpdate,
        DateTimeOffset? nextPoll = null)
    {
        _cachedResults = results.Select(r => (r, ServiceGroup.Ordered(r.Readings))).ToList();
        _lastUpdate = lastUpdate;
        _nextPoll = nextPoll;

        var taskbar = Native.TaskbarRect();
        var screen = taskbar is { } bar ? Screen.FromRectangle(bar) : Screen.PrimaryScreen!;
        var edge = taskbar is { } t
            ? FlyoutPlacement.EdgeOf(t, screen.Bounds)
            : FlyoutPlacement.Edge.Bottom;

        // Anchor the DPI probe inside the taskbar corner the fly-out will occupy.
        _dpi = Native.DpiForPoint(new Point(
            screen.WorkingArea.Left + screen.WorkingArea.Width / 2,
            screen.WorkingArea.Top + screen.WorkingArea.Height / 2));

        BuildFonts();

        var margin = Px(EdgeMargin);
        var size = FlyoutPlacement.Fit(new Size(Px(BaseWidth), MeasureHeight()), screen.WorkingArea, margin);
        Bounds = new Rectangle(FlyoutPlacement.Locate(size, screen.WorkingArea, edge, margin), size);

        // Fork: recorta a janela no contorno arredondado do cartão, para não sobrar quina clara.
        using (var outline = RoundedRect(new Rectangle(0, 0, size.Width, size.Height), Px(14)))
        {
            var old = Region;
            Region = new Region(outline);
            old?.Dispose();
        }

        Show();
        Activate();
        Invalidate();
    }

    private void BuildFonts()
    {
        var family = SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif;

        _titleFont?.Dispose();
        _labelFont?.Dispose();
        _smallFont?.Dispose();
        _valueFont?.Dispose();
        _headFont?.Dispose();

        _titleFont = new Font(family, Px(15), FontStyle.Bold, GraphicsUnit.Pixel);
        _labelFont = new Font(family, Px(14), FontStyle.Regular, GraphicsUnit.Pixel);
        _smallFont = new Font(family, Px(11), FontStyle.Regular, GraphicsUnit.Pixel);
        _valueFont = new Font(family, Px(14), FontStyle.Bold, GraphicsUnit.Pixel);
        _headFont = new Font(family, Px(19), FontStyle.Bold, GraphicsUnit.Pixel);
    }

    // Fork: layout de cartão. Um bloco por serviço — ícone, nome, plano e o pior
    // percentual no cabeçalho; embaixo, uma linha por janela com barra, valor e quanto falta para
    // zerar. O login só aparece quando está com problema: "válido até" em todo bloco era ruído.
    private const int ServiceHeader = 36;
    private const int ReadingRow = 26;
    private const int ServiceGap = 14;

    private static bool AuthNeedsAttention(ProviderResult result) => result.Auth is { IsValid: false };

    private int MeasureHeight()
    {
        var height = Px(14);
        foreach (var cached in _cachedResults)
        {
            var result = cached.Result;
            height += Px(ServiceHeader);
            if (AuthNeedsAttention(result)) height += Px(18);
            if (result.Notice is not null) height += Px(18);
            if (result.Error is not null) height += Px(20);
            height += result.Readings.Count * Px(ReadingRow);
            height += Px(ServiceGap);
        }

        return height + Px(30);                                 // footer
    }

    /// <summary>"OAuth · max" → "Max"; "chatgpt · plus" → "Plus"; "Allegretto" → "Allegretto".</summary>
    internal static string? PlanOf(AuthStatus? auth)
    {
        var detail = auth?.Detail;
        if (string.IsNullOrWhiteSpace(detail)) return null;

        var plan = detail.Split('·').Last().Trim();
        if (plan.Length == 0 || plan.Equals("OAuth", StringComparison.OrdinalIgnoreCase)) return null;
        return char.ToUpperInvariant(plan[0]) + plan[1..];
    }

    /// <summary>Band along the bottom edge, inside the border.</summary>
    private Rectangle CountdownBounds => new(1, Height - Px(3) - 1, Width - 2, Px(3));

    private void InvalidateCountdown()
    {
        if (_nextPoll is not null) Invalidate(CountdownBounds);
    }

    /// <summary>
    /// How far the current poll interval has run, 0 to 1. Pure so the sweep can be tested
    /// without a window.
    /// </summary>
    internal static double CountdownProgress(DateTimeOffset nextPoll, TimeSpan interval, DateTimeOffset now)
    {
        if (interval <= TimeSpan.Zero) return 0;

        var remaining = nextPoll - now;
        if (remaining <= TimeSpan.Zero) return 1;      // overdue: a poll is in flight or late
        if (remaining >= interval) return 0;           // clock skew, or the interval just grew

        return 1 - remaining / interval;
    }

    private void DrawCountdown(Graphics g, Rectangle bounds)
    {
        using (var back = new SolidBrush(Background)) g.FillRectangle(back, bounds);

        if (_nextPoll is not { } next) return;

        // The track is drawn even at zero progress. Without it the strip is simply absent for
        // the first seconds after a refresh, which reads as "the feature isn't there".
        using (var track = new SolidBrush(Color.FromArgb(Dark ? 38 : 30, Muted)))
            g.FillRectangle(track, bounds);

        var progress = CountdownProgress(next, TimeSpan.FromSeconds(Math.Max(1, _config.RefreshSeconds)), DateTimeOffset.Now);
        var width = (int)Math.Round(bounds.Width * progress);
        if (width <= 0) return;

        // Ambient, not something to read — but it has to be visible to be ambient at all.
        using var brush = new SolidBrush(Color.FromArgb(Dark ? 130 : 105, Muted));
        g.FillRectangle(brush, bounds with { Width = width });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        // A repaint confined to the strip redraws only the strip; the timer fires four times a
        // second and the rest of the window has not changed.
        if (e.ClipRectangle.Top >= CountdownBounds.Top)
        {
            DrawCountdown(g, CountdownBounds);
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var back = new SolidBrush(Background)) g.FillRectangle(back, ClientRectangle);
        using (var border = new Pen(BorderColor))
        using (var outline = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Px(14)))
            g.DrawPath(border, outline);

        using var foreground = new SolidBrush(Foreground);
        using var muted = new SolidBrush(Muted);

        var pad = Px(16);
        var y = Px(14);
        var textLeft = pad + Px(34);                            // alinhado ao nome, depois do ícone
        var barLeft = Px(170);
        var barWidth = Px(200);
        var valueLeft = barLeft + barWidth + Px(12);
        var resetLeft = valueLeft + Px(52);
        // Text that comes from a provider is drawn into these widths, never past them: an error
        // may be a server's whole answer, and a row here is one line high.
        var fullWidth = Width - pad - textLeft;
        var labelWidth = barLeft - textLeft - Px(10);

        if (_cachedResults.Count == 0)
        {
            g.DrawString(Loc.T("details.noData"), _labelFont, muted, pad, y);
            return;
        }

        for (var index = 0; index < _cachedResults.Count; index++)
        {
            var result = _cachedResults[index].Result;
            var readings = _cachedResults[index].Readings;
            var accent = Harmony.Legible(_palette.Service(result.Group), Dark);
            ServiceBadge.Draw(g, new RectangleF(pad, y + Px(2), Px(24), Px(24)), result.Group, accent, Dark);
            g.DrawString(result.Group, _titleFont, foreground, textLeft, y + Px(4));

            // O número grande do cabeçalho é o mesmo da faixa e do ícone: o que trava primeiro.
            // Serviço só de saldo mostra o saldo.
            var head = ServiceGroup.Binding(readings) is { } binding
                ? ($"{Math.Round(binding.Percent)} %", _palette.ForReading(result.Group, binding.Percent, 0, 1, Dark))
                : readings.FirstOrDefault(r => r.IsInformational) is { } balance
                    ? ($"{balance.AmountUnit} {balance.Amount:N2}", accent)
                    : ((string, Color)?)null;
            
            float headWidth = 0;
            if (head is { } h) headWidth = g.MeasureString(h.Item1, _headFont).Width;

            if (PlanOf(result.Auth) is { } plan)
            {
                var nameWidth = g.MeasureString(result.Group, _titleFont).Width;
                var spaceRight = Width - pad - headWidth - Px(16); // Margem antes do head
                var maxChipWidth = spaceRight - (textLeft + nameWidth + Px(8));
                
                var chipWidth = Math.Min(g.MeasureString(plan, _smallFont).Width + Px(12), Math.Max(Px(30), maxChipWidth));
                var chip = new Rectangle((int)(textLeft + nameWidth + Px(8)), y + Px(6), (int)chipWidth, Px(18));
                
                using (var chipBack = new SolidBrush(Color.FromArgb(Dark ? 40 : 22, Foreground)))
                using (var chipPath = RoundedRect(chip, Px(9)))
                    g.FillPath(chipBack, chipPath);
                    
                // Fork: Item 12 - corta o nome do plano com elipse usando TextLine
                TextLine.Draw(g, plan, _smallFont, muted, chip.X + Px(6), chip.Y + Px(2), chip.Width - Px(12));
            }

            if (head is { } hh)
            {
                using var headBrush = new SolidBrush(hh.Item2);
                g.DrawString(hh.Item1, _headFont, headBrush, Width - pad - headWidth, y + Px(2));
            }

            y += Px(ServiceHeader);

            if (AuthNeedsAttention(result) && result.Auth is { } auth)
            {
                var text = Loc.T("details.auth", auth.Summary()) +
                           (auth.Detail is { Length: > 0 } d ? $"  ·  {d}" : "");
                using var brush = new SolidBrush(Harmony.Legible(_palette.Critical, Dark));
                TextLine.Draw(g, text, _smallFont, brush, textLeft, y, fullWidth);
                y += Px(18);
            }

            if (result.Notice is { } notice)
            {
                // Not an error, so not in the critical colour — but not muted away either:
                // an endpoint someone else configured should catch the eye once.
                using var brush = new SolidBrush(Harmony.Legible(_palette.Warn, Dark));
                TextLine.Draw(g, notice, _smallFont, brush, textLeft, y, fullWidth);
                y += Px(18);
            }

            if (result.Error is { } error)
            {
                // "paused" without a duration reads like "broken", so say when it retries.
                if (result.RetryAt is { } retry && retry > DateTimeOffset.Now)
                    error += "  ·  " + Loc.T("details.retryIn", LimitReading.FormatSpan(retry - DateTimeOffset.Now));

                using var brush = new SolidBrush(Harmony.Legible(_palette.Critical, Dark));
                TextLine.Draw(g, error, _smallFont, brush, textLeft, y, fullWidth);
                y += Px(20);
            }

            foreach (var reading in readings)
            {
                TextLine.Draw(g, reading.Label, _labelFont, muted, textLeft, y + Px(2), labelWidth);

                if (reading.IsInformational)
                {
                    // Valor em dinheiro não tem barra: não existe "cheio". Alinha com os percentuais.
                    using var amountBrush = new SolidBrush(Foreground);
                    g.DrawString($"{reading.AmountUnit} {reading.Amount:N2}", _valueFont, amountBrush, barLeft, y + Px(2));
                    y += Px(ReadingRow);
                    continue;
                }

                var color = _palette.ForReading(reading.Group, reading.Percent, reading.Variant, reading.VariantCount, Dark);
                DrawBar(g, new Rectangle(barLeft, y + Px(8), barWidth, Px(7)), reading.Percent, color);

                using (var valueBrush = new SolidBrush(color))
                    g.DrawString($"{Math.Round(reading.Percent)} %", _valueFont, valueBrush, valueLeft, y + Px(2));

                // Quanto falta para zerar, curto: o horário completo fica no cartão do mouse.
                if (reading.ResetsAt is { } reset && reset > DateTimeOffset.Now)
                    TextLine.Draw(g, "↻ " + LimitReading.FormatSpan(reset - DateTimeOffset.Now), _smallFont, muted,
                        resetLeft, y + Px(4), Width - pad - resetLeft);

                y += Px(ReadingRow);
            }

            y += Px(ServiceGap);

            if (index < _cachedResults.Count - 1)
                using (var line = new Pen(BorderColor))
                    g.DrawLine(line, pad, y - Px(ServiceGap) / 2, Width - pad, y - Px(ServiceGap) / 2);
        }

        var footer = _lastUpdate is { } stamp
            ? Loc.T("details.footer", stamp.ToLocalTime().ToString("HH:mm:ss", Loc.Culture), _config.RefreshSeconds)
            : Loc.T("details.noData");
        g.DrawString(footer, _smallFont, muted, pad, Height - Px(24));

        DrawCountdown(g, CountdownBounds);
    }

    private void DrawBar(Graphics g, Rectangle bounds, double percent, Color color)
    {
        var radius = bounds.Height / 2f;

        using (var track = new SolidBrush(Palette.Track(color, Dark)))
        using (var path = RoundedRect(bounds, radius))
            g.FillPath(track, path);

        var filled = (int)Math.Round(bounds.Width * Math.Clamp(percent, 0, 100) / 100.0);
        // A non-zero value always shows at least a full cap, otherwise 1 % renders as nothing.
        if (filled < bounds.Height) filled = percent > 0 ? bounds.Height : 0;
        if (filled <= 0) return;

        using var fill = new SolidBrush(color);
        using var filledPath = RoundedRect(bounds with { Width = filled }, radius);
        g.FillPath(fill, filledPath);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, float radius)
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Region?.Dispose();
            _countdown.Dispose();
            _titleFont?.Dispose();
            _labelFont?.Dispose();
            _smallFont?.Dispose();
            _valueFont?.Dispose();
            _headFont?.Dispose();
        }

        base.Dispose(disposing);
    }
}
