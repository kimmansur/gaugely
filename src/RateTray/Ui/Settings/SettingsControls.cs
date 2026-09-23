using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace RateTray.Ui.Settings;

/// <summary>
/// Fork: peças desenhadas à mão da janela de ajustes. Cada uma herda de um controle do Windows
/// quando existe um equivalente — a chave liga/desliga é um <see cref="CheckBox"/> — para manter
/// teclado, foco e leitor de tela de graça; só a pintura é nossa. Todas espelham o próprio
/// desenho quando o idioma se lê da direita para a esquerda.
/// </summary>
internal static class Shapes
{
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static int Scale(Control c, int value) => (int)Math.Round(value * c.DeviceDpi / 96.0);
}

/// <summary>Chave liga/desliga no estilo do Windows 11, sobre um <see cref="CheckBox"/>.</summary>
internal sealed class ToggleSwitch : CheckBox
{
    private readonly SettingsTheme _theme;

    public ToggleSwitch(SettingsTheme theme, string accessibleName)
    {
        _theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Size = new Size(44, 22);
        Cursor = Cursors.Hand;
        if (accessibleName.Length > 0) AccessibleName = accessibleName;
        Text = "";
        Margin = new Padding(3, 3, 3, 3);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? _theme.Surface);

        var h = Math.Min(Height - 2, Shapes.Scale(this, 20));
        var w = Math.Min(Width - 2, h * 2);
        var track = new RectangleF((Width - w) / 2f, (Height - h) / 2f, w, h);

        using (var path = Shapes.Rounded(track, h / 2f))
        {
            if (Checked)
            {
                using var fill = new SolidBrush(Enabled ? _theme.Accent : _theme.Muted);
                g.FillPath(fill, path);
            }
            else
            {
                using var pen = new Pen(_theme.Muted, 1.2f);
                g.DrawPath(pen, path);
            }
        }

        var knob = h - Shapes.Scale(this, Checked ? 8 : 10);
        var onRight = Checked ^ (RightToLeft == RightToLeft.Yes);
        var x = onRight ? track.Right - knob - (h - knob) / 2f : track.X + (h - knob) / 2f;
        using (var knobBrush = new SolidBrush(Checked ? _theme.AccentText : _theme.SubText))
            g.FillEllipse(knobBrush, x, track.Y + (h - knob) / 2f, knob, knob);

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(_theme.Text, 1f) { DashStyle = DashStyle.Dot };
            using var outline = Shapes.Rounded(RectangleF.Inflate(track, 2, 2), h / 2f + 2);
            g.DrawPath(focus, outline);
        }
    }
}

/// <summary>Item da barra lateral: glifo, texto e a barrinha de destaque quando selecionado.</summary>
internal sealed class NavItem : Control
{
    private readonly SettingsTheme _theme;
    private readonly string _glyph;
    private bool _hover;
    private bool _selected;

    public NavItem(SettingsTheme theme, string glyph, string text)
    {
        _theme = theme;
        _glyph = glyph;
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PageTab;
        AccessibleName = text;
        Height = 38;
        Dock = DockStyle.Top;
        Margin = Padding.Empty;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
            if (IsHandleCreated)
            {
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                if (value) AccessibilityNotifyClients(AccessibleEvents.Selection, -1);
            }
        }
    }

    /// <summary>O leitor de tela anuncia a página atual pelo estado, sem texto fixo em algum idioma.</summary>
    protected override AccessibleObject CreateAccessibilityInstance() => new NavAccessible(this);

    private sealed class NavAccessible(NavItem owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State =>
            base.State | AccessibleStates.Selectable | (owner.Selected ? AccessibleStates.Selected : AccessibleStates.None);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_theme.Sidebar);

        var rtl = RightToLeft == RightToLeft.Yes;
        var box = new RectangleF(Shapes.Scale(this, 6), 2, Width - Shapes.Scale(this, 12), Height - 4);
        if (_selected || _hover)
        {
            using var fill = new SolidBrush(_selected ? _theme.Selected : Color.FromArgb(_theme.Dark ? 36 : 228, _theme.Selected));
            using var path = Shapes.Rounded(box, Shapes.Scale(this, 5));
            g.FillPath(fill, path);
        }

        if (_selected)
        {
            var barH = box.Height * 0.45f;
            var barX = rtl ? box.Right - 3 : box.X;
            using var bar = new SolidBrush(_theme.Accent);
            using var path = Shapes.Rounded(new RectangleF(barX, box.Y + (box.Height - barH) / 2, 3, barH), 1.5f);
            g.FillPath(bar, path);
        }

        var pad = Shapes.Scale(this, 14);
        var glyphW = Shapes.Scale(this, 22);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (rtl) flags |= TextFormatFlags.RightToLeft | TextFormatFlags.Right;

        var glyphRect = rtl
            ? new Rectangle((int)box.Right - pad - glyphW, 0, glyphW, Height)
            : new Rectangle((int)box.X + pad, 0, glyphW, Height);
        var textRect = rtl
            ? new Rectangle((int)box.X + 4, 0, glyphRect.X - (int)box.X - 10, Height)
            : new Rectangle(glyphRect.Right + 6, 0, (int)box.Right - glyphRect.Right - 10, Height);

        using (var glyphFont = new Font("Segoe MDL2 Assets", Font.Size, FontStyle.Regular))
            TextRenderer.DrawText(g, _glyph, glyphFont, glyphRect, _selected ? _theme.Accent : _theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(g, Text, Font, textRect, _selected ? _theme.Text : _theme.SubText, flags);

        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(_theme.Text) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, Rectangle.Round(RectangleF.Inflate(box, -1, -1)));
        }
    }
}

/// <summary>Painel com fundo e borda arredondados; o conteúdo vai dentro, com o Padding.</summary>
internal class CardPanel : Panel
{
    private readonly SettingsTheme _theme;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Dashed { get; set; }

    public CardPanel(SettingsTheme theme)
    {
        _theme = theme;
        Fill = theme.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(14, 12, 14, 12);
        BackColor = theme.Surface;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? _theme.Window);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Shapes.Rounded(r, Shapes.Scale(this, 9));
        using (var fill = new SolidBrush(Fill)) g.FillPath(fill, path);
        using var pen = new Pen(_theme.Border, 1f) { DashStyle = Dashed ? DashStyle.Dash : DashStyle.Solid };
        g.DrawPath(pen, path);
    }

    protected override void OnPaint(PaintEventArgs e) { }
}

/// <summary>Etiqueta pequena em pílula ("Chave no cofre", "Login da CLI").</summary>
internal sealed class Chip : Control
{
    private readonly SettingsTheme _theme;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Ghost { get; set; }

    public Chip(SettingsTheme theme, string text)
    {
        _theme = theme;
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        AccessibleRole = AccessibleRole.StaticText;
        Margin = new Padding(6, 3, 0, 0);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        return new Size(text.Width + Shapes.Scale(this, 12), text.Height + Shapes.Scale(this, 4));
    }

    protected override void OnTextChanged(EventArgs e) { Size = GetPreferredSize(Size.Empty); base.OnTextChanged(e); }
    protected override void OnFontChanged(EventArgs e) { Size = GetPreferredSize(Size.Empty); base.OnFontChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? _theme.Surface);
        using var path = Shapes.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), Shapes.Scale(this, 4));
        using (var fill = new SolidBrush(Ghost ? _theme.Selected : _theme.ChipBack)) g.FillPath(fill, path);
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Ghost ? _theme.SubText : _theme.ChipText, flags);
    }
}

/// <summary>Barra de progresso fina, na cor do nível (normal, alerta, crítico).</summary>
internal sealed class ThinBar : Control
{
    private readonly SettingsTheme _theme;
    private double _value;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BarColor { get; set; }

    public ThinBar(SettingsTheme theme)
    {
        _theme = theme;
        BarColor = theme.Accent;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 5;
        AccessibleRole = AccessibleRole.ProgressBar;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 100); AccessibleDescription = $"{_value:0}%"; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? _theme.Surface);
        var r = new RectangleF(0, 0, Width, Height);
        using (var track = Shapes.Rounded(r, Height / 2f))
        using (var back = new SolidBrush(_theme.Border))
            g.FillPath(back, track);

        var w = (float)(Width * _value / 100.0);
        if (w < 1) return;
        var x = RightToLeft == RightToLeft.Yes ? Width - w : 0;
        using var fill = Shapes.Rounded(new RectangleF(x, 0, w, Height), Height / 2f);
        using var brush = new SolidBrush(BarColor);
        g.FillPath(brush, fill);
    }
}

/// <summary>Ajustes de aparência dos controles comuns do Windows para combinar com o tema.</summary>
internal static class Styled
{
    public static T Input<T>(T control, SettingsTheme theme) where T : Control
    {
        control.BackColor = theme.Input;
        control.ForeColor = theme.Text;
        if (control is TextBox box) box.BorderStyle = BorderStyle.FixedSingle;
        if (control is NumericUpDown number) number.BorderStyle = BorderStyle.FixedSingle;
        if (control is ComboBox combo) combo.FlatStyle = FlatStyle.Flat;
        return control;
    }

    public static Button Primary(Button button, SettingsTheme theme)
    {
        Base(button);
        button.BackColor = theme.Accent;
        button.ForeColor = theme.AccentText;
        button.FlatAppearance.BorderColor = theme.Accent;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(theme.Accent, 0.15f);
        return button;
    }

    public static Button Secondary(Button button, SettingsTheme theme)
    {
        Base(button);
        button.BackColor = theme.Dark ? Color.FromArgb(58, 58, 58) : Color.FromArgb(251, 251, 251);
        button.ForeColor = theme.Text;
        button.FlatAppearance.BorderColor = theme.Dark ? Color.FromArgb(74, 74, 74) : Color.FromArgb(208, 208, 208);
        button.FlatAppearance.MouseOverBackColor = theme.Dark ? Color.FromArgb(68, 68, 68) : Color.FromArgb(240, 240, 240);
        return button;
    }

    private static void Base(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(8, 2, 8, 2);
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
    }
}

/// <summary>
/// Lista de escolha desenhada pelo app. O ComboBox do Windows em estilo plano ignora o tema escuro
/// na lista aberta e não se desenha em <see cref="Control.DrawToBitmap"/>; este abre um menu com
/// as mesmas cores da janela. Teclado: setas, Home e End trocam; Espaço, Enter, F4 e Alt+↓ abrem.
/// </summary>
internal sealed class ChoiceBox : Control
{
    private readonly SettingsTheme _theme;
    private readonly List<string> _items = [];
    private int _selected = -1;
    private bool _hover;

    public event EventHandler? SelectedIndexChanged;

    public ChoiceBox(SettingsTheme theme)
    {
        _theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.Selectable | ControlStyles.ResizeRedraw | ControlStyles.StandardClick, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.ComboBox;
        Height = 30;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IList<string> Items => _items;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var clamped = _items.Count == 0 ? -1 : Math.Clamp(value, 0, _items.Count - 1);
            if (clamped == _selected) return;
            _selected = clamped;
            AccessibilityObject.Value = SelectedText;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string SelectedText => _selected >= 0 && _selected < _items.Count ? _items[_selected] : "";

    protected override AccessibleObject CreateAccessibilityInstance() => new ChoiceAccessible(this);

    private sealed class ChoiceAccessible(ChoiceBox owner) : ControlAccessibleObject(owner)
    {
        public override string? Value { get => owner.SelectedText; set { } }
    }

    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Down when e.Alt:
            case Keys.F4:
            case Keys.Space:
            case Keys.Enter:
                Open();
                e.Handled = true;
                break;
            case Keys.Up: SelectedIndex = Math.Max(0, _selected - 1); e.Handled = true; break;
            case Keys.Down: SelectedIndex = _selected + 1; e.Handled = true; break;
            case Keys.Home: SelectedIndex = 0; e.Handled = true; break;
            case Keys.End: SelectedIndex = _items.Count - 1; e.Handled = true; break;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        Open();
        base.OnClick(e);
    }

    private void Open()
    {
        if (!Enabled || _items.Count == 0) return;

        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new MenuColors(_theme)),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            RightToLeft = RightToLeft,
            Font = Font,
            MinimumSize = new Size(Width, 0),
            BackColor = _theme.Surface,
            ForeColor = _theme.Text,
        };
        for (var i = 0; i < _items.Count; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem(_items[i]) { Checked = i == _selected, ForeColor = _theme.Text, Padding = new Padding(0, 3, 0, 3) };
            item.Click += (_, _) => SelectedIndex = index;
            menu.Items.Add(item);
        }

        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        var rtl = RightToLeft == RightToLeft.Yes;
        menu.Show(this, new Point(rtl ? Width : 0, Height), rtl ? ToolStripDropDownDirection.BelowLeft : ToolStripDropDownDirection.BelowRight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? _theme.Surface);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Shapes.Rounded(r, Shapes.Scale(this, 5)))
        {
            var fill = _hover && Enabled ? ControlPaint.Light(_theme.Input, 0.08f) : _theme.Input;
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            using var pen = new Pen(Focused ? _theme.Accent : _theme.Border, Focused ? 1.5f : 1f);
            g.DrawPath(pen, path);
        }

        var rtl = RightToLeft == RightToLeft.Yes;
        var chevronW = Shapes.Scale(this, 26);
        var pad = Shapes.Scale(this, 10);
        var textRect = rtl
            ? new Rectangle(chevronW, 0, Width - chevronW - pad, Height)
            : new Rectangle(pad, 0, Width - chevronW - pad, Height);
        var chevronRect = rtl ? new Rectangle(0, 0, chevronW, Height) : new Rectangle(Width - chevronW, 0, chevronW, Height);

        var color = Enabled ? _theme.Text : _theme.Muted;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (rtl) flags |= TextFormatFlags.Right | TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(g, SelectedText, Font, textRect, color, flags);

        using var glyph = new Font("Segoe MDL2 Assets", Font.Size * 0.8f);
        TextRenderer.DrawText(g, "", glyph, chevronRect, _theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    /// <summary>Cores do menu aberto, tiradas do tema da janela.</summary>
    private sealed class MenuColors(SettingsTheme theme) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => theme.Surface;
        public override Color MenuBorder => theme.Border;
        public override Color MenuItemBorder => theme.Selected;
        public override Color MenuItemSelected => theme.Selected;
        public override Color MenuItemSelectedGradientBegin => theme.Selected;
        public override Color MenuItemSelectedGradientEnd => theme.Selected;
        public override Color CheckBackground => theme.Surface;
        public override Color CheckSelectedBackground => theme.Selected;
        public override Color CheckPressedBackground => theme.Selected;
        public override Color ImageMarginGradientBegin => theme.Surface;
        public override Color ImageMarginGradientMiddle => theme.Surface;
        public override Color ImageMarginGradientEnd => theme.Surface;
    }
}

/// <summary>A marca da barra lateral: o anel e o nome, desenhados lado a lado.</summary>
internal sealed class BrandMark : Control
{
    private readonly SettingsTheme _theme;

    public BrandMark(SettingsTheme theme)
    {
        _theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = "Gaugely";
        AccessibleRole = AccessibleRole.StaticText;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_theme.Sidebar);

        var rtl = RightToLeft == RightToLeft.Yes;
        var size = Shapes.Scale(this, 24);
        var pad = Shapes.Scale(this, 14);
        var stroke = size * 0.18f;
        var x = rtl ? Width - pad - size : pad;
        var ring = new RectangleF(x + stroke / 2, (Height - size) / 2f + stroke / 2, size - stroke, size - stroke);
        using (var back = new Pen(_theme.Border, stroke)) g.DrawEllipse(back, ring);
        using (var arc = new Pen(_theme.Accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(arc, ring, -90, 245);

        var gap = Shapes.Scale(this, 10);
        var textRect = rtl
            ? new Rectangle(0, 0, x - gap, Height)
            : new Rectangle(x + size + gap, 0, Width - x - size - gap, Height);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        if (rtl) flags |= TextFormatFlags.Right;
        TextRenderer.DrawText(g, Text, Font, textRect, _theme.Text, flags);
    }
}
