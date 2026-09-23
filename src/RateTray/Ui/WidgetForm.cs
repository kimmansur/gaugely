using System.Drawing.Drawing2D;
using RateTray.Configuration;
using RateTray.Model;

namespace RateTray.Ui;

/// <summary>Um serviço como a faixa o desenha: as janelas dele e o erro do último poll, se houve.</summary>
public sealed record WidgetGroup(string Group, IReadOnlyList<LimitReading> Rows, string? Error);

/// <summary>
/// Fork: dados do hover na faixa — o grupo sob o mouse (ou null ao sair) e o retângulo do anel
/// em coordenadas de TELA, para o cartão ser posicionado ao lado correto da faixa.
/// </summary>
public sealed class WidgetHoverEventArgs : EventArgs
{
    /// <summary>Grupo sob o mouse, ou null quando saiu do anel / da faixa.</summary>
    public string? Group { get; init; }

    /// <summary>Retângulo do anel em coordenadas de tela. Só válido quando <see cref="Group"/> ≠ null.</summary>
    public Rectangle AnchorScreenRect { get; init; }
}

/// <summary>
/// Fork: faixa flutuante, no estilo do CodexBar do macOS — um anel por serviço, com o
/// ícone no centro e, embaixo, o percentual do limite que trava primeiro. O anel é o número em
/// forma de relance: dá para ler a situação dos cinco serviços sem focar em nenhum dígito. O
/// detalhe (5 h, semana, saldo, reset) fica na dica do mouse e no cartão que abre no clique.
///
/// Serviço que só informa valor (OpenRouter sem teto na chave) não tem percentual: o anel fica só
/// com o trilho e o texto mostra o saldo, em vez de um 0 % que enganaria.
///
/// Desenhada à mão, como o resto da interface. Fora do Alt+Tab e sem roubar o foco
/// (WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE). A janela é recortada no próprio contorno arredondado
/// (Region): sem isso o Windows pinta os cantos do retângulo e a faixa aparece com quinas brancas.
///
/// Posição: canto de um monitor, guardado por nome no settings.json. Arrastar reposiciona e grava
/// o canto mais próximo, sobre a área útil (que já desconta a barra de tarefas).
///
/// Fork: modo Notch — a faixa encaixa rente a uma borda da tela, com quinas
/// côncavas onde encontra a borda, parecendo escavada no monitor.
/// </summary>
public sealed class WidgetForm : Form
{
    private const int BaseWidth = 66;
    private const int RowHeight = 74;           // anel + texto
    private const int RingSize = 42;
    private const int RingStroke = 4;
    private const int BadgeSize = 22;           // logo sem placa ocupa melhor o miolo do anel
    private const int PadTop = 6;
    private const int Corner = 14;
    private const int EdgeMargin = 12;
    private const int DragSlop = 4;             // movimento menor que isso ainda é um clique
    private const int HandleThickness = 5;      // espessura da alça de arrasto

    private readonly AppConfig _config;
    private readonly Palette _palette;

    private IReadOnlyList<WidgetGroup> _groups = [];
    // Fork: Item 8 - cache do grupo para evitar recálculo (LINQ) no paint
    private IReadOnlyList<(WidgetGroup Group, LimitReading? Summary, LimitReading? Binding)> _cachedGroups = [];
    private int _dpi = 96;
    private Font _valueFont = null!;
    private Point _dragOrigin;
    private Point _dragScreenOrigin;
    private bool _dragging;
    private int _hovered = -1;

    // Fork: overlay de preview durante o arrasto (bordas candidatas)
    private DragOverlayForm? _dragOverlay;

    public WidgetForm(AppConfig config, Palette palette)
    {
        _config = config;
        _palette = palette;

        Text = "Gaugely Widget";                  // título usado pela captura de tela
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Background;                    // qualquer franja fora do recorte sai escura

        BuildFonts();

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        // Fork: ao sair da faixa, esconde o cartão rico e apaga o realce.
        MouseLeave += (_, _) =>
        {
            if (_hovered < 0) return;
            _hovered = -1;
            Invalidate();
            HoverChanged?.Invoke(this, new WidgetHoverEventArgs { Group = null });
        };
    }

    /// <summary>Clique simples na faixa — o app abre o cartão completo.</summary>
    public event EventHandler? Clicked;

    /// <summary>Fork: o anel sob o mouse mudou — o app decide se mostra ou esconde o cartão rico.</summary>
    public event EventHandler<WidgetHoverEventArgs>? HoverChanged;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x00000080;               // WS_EX_TOOLWINDOW: fora do Alt+Tab
            p.ExStyle |= 0x08000000;               // WS_EX_NOACTIVATE: não rouba o foco
            return p;
        }
    }

    private bool Dark => TrayIconRenderer.UsesDarkTaskbar(_config.Theme);

    private Color Background => Dark ? Color.FromArgb(26, 28, 33) : Color.FromArgb(248, 249, 251);
    private Color Foreground => Dark ? Color.FromArgb(236, 238, 242) : Color.FromArgb(26, 28, 32);
    private Color Muted => Dark ? Color.FromArgb(150, 156, 168) : Color.FromArgb(107, 114, 128);
    private Color BorderColor => Dark ? Color.FromArgb(54, 58, 67) : Color.FromArgb(214, 219, 228);

    private int Px(double value) => (int)Math.Round(value * _config.Widget.Scale * (_dpi / 96.0));

    private bool IsNotch => _config.Widget.Modo == ModoApresentacao.Notch;

    /// <summary>Troca os serviços exibidos e recalcula a altura.</summary>
    public void Apply(IReadOnlyList<WidgetGroup> groups)
    {
        _groups = groups;
        // Fork: Item 8 - cache LINQ para desenho
        _cachedGroups = groups.Select(g => (g, ServiceGroup.Summary(g.Group, g.Rows), ServiceGroup.Binding(g.Rows))).ToList();
        Relayout();
        Invalidate();
    }

    public event EventHandler? LayoutChanged; // Fork: evento para salvar a escala no app

    private string _layoutSignature = "";

    /// <summary>Recoloca a faixa no canto guardado, ajusta a altura e refaz o recorte arredondado.</summary>
    public void Relayout()
    {
        var screen = ScreenOfChoice();
        var area = screen.WorkingArea;

        var dpi = Native.DpiForPoint(new Point(area.Left + area.Width / 2, area.Top + area.Height / 2));

        // Fork: Item 7 - pula relayout se a assinatura (orientação, escala, canto, monitor, dpi e número de grupos) não mudou
        // Área útil e tema também entram: resolução, barra de tarefas ou tema claro/escuro mudam
        // posição e cor sem mexer em nenhuma configuração da faixa.
        var sig = $"{_config.Widget.OrientacaoEfetiva}|{_config.Widget.Scale}|{_config.Widget.Corner}|{_config.Widget.Monitor}|{dpi}|{_groups.Count}|{area}|{Dark}|{_config.Widget.Modo}|{_config.Widget.Borda}|{_config.Widget.FracaoBorda:F4}|{_config.Widget.MostrarAlca}";
        if (sig == _layoutSignature) return;
        _layoutSignature = sig;

        _dpi = dpi;
        BuildFonts();
        BackColor = Background;

        if (IsNotch)
        {
            var layout = CalculateNotchLayout(_config.Widget.Borda, _config.Widget.Scale, _config.Widget.FracaoBorda, _groups.Count, area, _dpi);
            Bounds = layout.WidgetBounds;

            using var contorno = NotchShape(new Rectangle(0, 0, layout.WidgetBounds.Width, layout.WidgetBounds.Height), Px(Corner), _config.Widget.Borda);
            var antigo = Region;
            Region = new Region(contorno);
            antigo?.Dispose();
        }
        else
        {
            var layout = CalculateLayout(_config.Widget.OrientacaoEfetiva, _config.Widget.Scale, _config.Widget.Corner, _groups.Count, area, _dpi);
            Bounds = layout.WidgetBounds;

            using var contorno = Rounded(new Rectangle(0, 0, layout.WidgetBounds.Width, layout.WidgetBounds.Height), Px(Corner));
            var antigo = Region;
            Region = new Region(contorno);
            antigo?.Dispose();
        }
    }

    internal static (Rectangle WidgetBounds, Rectangle[] RowBounds) CalculateLayout(
        string orientation, double scale, string corner, int groupsCount, Rectangle area, int dpi)
    {
        int Px(double value) => (int)Math.Round(value * scale * (dpi / 96.0));

        var rows = Math.Max(1, groupsCount);
        var isHorizontal = orientation.Equals("horizontal", StringComparison.OrdinalIgnoreCase);

        var width = isHorizontal ? Px(PadTop) * 2 + rows * Px(RowHeight) : Px(BaseWidth);
        var height = isHorizontal ? Px(BaseWidth) : Px(PadTop) * 2 + rows * Px(RowHeight);

        var size = new Size(width, height);
        var margin = Px(EdgeMargin);

        int x, y;
        if (corner.Contains("Center", StringComparison.OrdinalIgnoreCase))
            x = area.Left + (area.Width - size.Width) / 2;
        else if (corner.Contains("left", StringComparison.OrdinalIgnoreCase))
            x = area.Left + margin;
        else
            x = area.Right - size.Width - margin;

        if (corner.StartsWith("top", StringComparison.OrdinalIgnoreCase))
            y = area.Top + margin;
        else
            y = area.Bottom - size.Height - margin;

        var widgetBounds = new Rectangle(new Point(x, y), size);
        var rowBounds = new Rectangle[Math.Max(1, groupsCount)];
        
        var renderRows = groupsCount == 0 ? 1 : groupsCount;
        for (var i = 0; i < renderRows; i++)
        {
            if (isHorizontal)
                rowBounds[i] = new Rectangle(Px(PadTop) + i * Px(RowHeight), 0, Px(RowHeight), size.Height);
            else
                rowBounds[i] = new Rectangle(0, Px(PadTop) + i * Px(RowHeight), size.Width, Px(RowHeight));
        }

        return (widgetBounds, rowBounds);
    }

    /// <summary>
    /// Calcula o layout no modo Notch: colado na borda, sem margem do lado encostado,
    /// posição ao longo da borda como fração 0–1.
    /// </summary>
    internal static (Rectangle WidgetBounds, Rectangle[] RowBounds) CalculateNotchLayout(
        BordaTela borda, double scale, double fracaoBorda, int groupsCount, Rectangle area, int dpi)
    {
        int Px(double value) => (int)Math.Round(value * scale * (dpi / 96.0));

        var rows = Math.Max(1, groupsCount);
        var isHorizontal = borda is BordaTela.Topo or BordaTela.Base;

        var pillWidth = isHorizontal ? Px(PadTop) * 2 + rows * Px(RowHeight) : Px(BaseWidth);
        var pillHeight = isHorizontal ? Px(BaseWidth) : Px(PadTop) * 2 + rows * Px(RowHeight);

        var radius = Px(Corner);
        var width = isHorizontal ? pillWidth + 2 * radius : pillWidth;
        var height = isHorizontal ? pillHeight : pillHeight + 2 * radius;

        var size = new Size(width, height);

        int x, y;
        switch (borda)
        {
            case BordaTela.Esquerda:
                x = area.Left;  // rente à borda esquerda
                y = area.Top + (int)((area.Height - size.Height) * fracaoBorda);
                y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - size.Height));
                break;
            case BordaTela.Direita:
                x = area.Right - size.Width;  // rente à borda direita
                y = area.Top + (int)((area.Height - size.Height) * fracaoBorda);
                y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - size.Height));
                break;
            case BordaTela.Topo:
                y = area.Top;  // rente ao topo
                x = area.Left + (int)((area.Width - size.Width) * fracaoBorda);
                x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - size.Width));
                break;
            default: // Base
                y = area.Bottom - size.Height;  // rente à base
                x = area.Left + (int)((area.Width - size.Width) * fracaoBorda);
                x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - size.Width));
                break;
        }

        var widgetBounds = new Rectangle(new Point(x, y), size);
        var rowBounds = new Rectangle[Math.Max(1, groupsCount)];

        var renderRows = groupsCount == 0 ? 1 : groupsCount;
        for (var i = 0; i < renderRows; i++)
        {
            if (isHorizontal)
                rowBounds[i] = new Rectangle(radius + Px(PadTop) + i * Px(RowHeight), 0, Px(RowHeight), size.Height);
            else
                rowBounds[i] = new Rectangle(0, radius + Px(PadTop) + i * Px(RowHeight), size.Width, Px(RowHeight));
        }

        return (widgetBounds, rowBounds);
    }

    /// <summary>
    /// Determina a borda mais próxima do ponto dado, dentro da área de trabalho.
    /// Método puro para viabilizar testes unitários.
    /// </summary>
    public static BordaTela NearestEdge(Point point, Rectangle workArea)
    {
        var distLeft = Math.Abs(point.X - workArea.Left);
        var distRight = Math.Abs(point.X - workArea.Right);
        var distTop = Math.Abs(point.Y - workArea.Top);
        var distBottom = Math.Abs(point.Y - workArea.Bottom);

        var min = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));

        if (min == distLeft) return BordaTela.Esquerda;
        if (min == distRight) return BordaTela.Direita;
        if (min == distTop) return BordaTela.Topo;
        return BordaTela.Base;
    }

    /// <summary>
    /// Converte uma posição absoluta ao longo de uma borda em fração 0–1.
    /// Método puro para testes.
    /// </summary>
    public static double PositionToFraction(Point point, BordaTela borda, Rectangle workArea, int widgetLength)
    {
        double range;
        double pos;
        switch (borda)
        {
            case BordaTela.Esquerda:
            case BordaTela.Direita:
                range = workArea.Height - widgetLength;
                pos = point.Y - workArea.Top;
                break;
            default: // Topo, Base
                range = workArea.Width - widgetLength;
                pos = point.X - workArea.Left;
                break;
        }

        if (range <= 0) return 0.5;
        return Math.Clamp(pos / range, 0.0, 1.0);
    }

    /// <summary>
    /// Resolve o monitor guardado, caindo para o principal quando o salvo não existe.
    /// Método puro (testável) que recebe as listas de monitores.
    /// </summary>
    public static string ResolveMonitor(string? savedMonitor, IReadOnlyList<string> availableDeviceNames, string primaryDeviceName)
    {
        if (string.IsNullOrEmpty(savedMonitor))
            return primaryDeviceName;

        foreach (var name in availableDeviceNames)
        {
            if (name.Equals(savedMonitor, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        // Monitor guardado não existe mais — cai para o principal
        return primaryDeviceName;
    }

    private Screen ScreenOfChoice() =>
        Screen.AllScreens.FirstOrDefault(s =>
            s.DeviceName.Equals(_config.Widget.Monitor, StringComparison.OrdinalIgnoreCase))
        ?? Screen.PrimaryScreen!;

    private void BuildFonts()
    {
        var family = SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif;
        _valueFont?.Dispose();
        _valueFont = new Font(family, Math.Max(1, Px(12)), FontStyle.Bold, GraphicsUnit.Pixel);
    }

    private Rectangle RowBounds(int i)
    {
        var isHorizontal = _config.Widget.IsHorizontal;
        var offset = IsNotch ? Px(Corner) : 0;
        
        if (isHorizontal)
            return new Rectangle(offset + Px(PadTop) + i * Px(RowHeight), 0, Px(RowHeight), Height);
        return new Rectangle(0, offset + Px(PadTop) + i * Px(RowHeight), Width, Px(RowHeight));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var fundo = new SolidBrush(Background))
            g.FillRectangle(fundo, ClientRectangle);

        if (IsNotch)
        {
            // No modo Notch, a borda só é desenhada nos lados que não estão colados.
            using var borda = new Pen(BorderColor);
            using var forma = NotchShape(new Rectangle(0, 0, Width - 1, Height - 1), Px(Corner), _config.Widget.Borda);
            g.DrawPath(borda, forma);
        }
        else
        {
            using var borda = new Pen(BorderColor);
            using var forma = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Px(Corner));
            g.DrawPath(borda, forma);
        }

        // Alça de arrasto (grip handle) na face externa — um arco discreto
        if (_config.Widget.MostrarAlca && IsNotch)
            PaintHandle(g);

        if (_groups.Count == 0)
        {
            TextRenderer.DrawText(g, "…", _valueFont, ClientRectangle, Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        for (var i = 0; i < _cachedGroups.Count; i++)
            PaintGroup(g, _cachedGroups[i], RowBounds(i), i == _hovered);
    }

    /// <summary>Desenha a alça de arrasto na face externa da faixa (modo Notch).</summary>
    private void PaintHandle(Graphics g)
    {
        var handleLen = Px(24);
        var thick = Px(HandleThickness);
        var handleColor = Color.FromArgb(Dark ? 80 : 120, Foreground);

        using var pen = new Pen(handleColor, Math.Max(1, Px(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        int cx, cy;
        switch (_config.Widget.Borda)
        {
            case BordaTela.Esquerda:
                cx = Width - thick;
                cy = Height / 2;
                g.DrawLine(pen, cx, cy - handleLen / 2, cx, cy + handleLen / 2);
                break;
            case BordaTela.Direita:
                cx = thick;
                cy = Height / 2;
                g.DrawLine(pen, cx, cy - handleLen / 2, cx, cy + handleLen / 2);
                break;
            case BordaTela.Topo:
                cx = Width / 2;
                cy = Height - thick;
                g.DrawLine(pen, cx - handleLen / 2, cy, cx + handleLen / 2, cy);
                break;
            default: // Base
                cx = Width / 2;
                cy = thick;
                g.DrawLine(pen, cx - handleLen / 2, cy, cx + handleLen / 2, cy);
                break;
        }
    }

    private void PaintGroup(Graphics g, (WidgetGroup Group, LimitReading? Summary, LimitReading? Binding) cached, Rectangle linha, bool realcado)
    {
        if (realcado)
        {
            using var brilho = new SolidBrush(Color.FromArgb(Dark ? 22 : 16, Foreground));
            using var forma = Rounded(Rectangle.Inflate(linha, -Px(4), -Px(2)), Px(10));
            g.FillPath(brilho, forma);
        }

        var grupo = cached.Group;
        var resumo = cached.Summary;
        var trava = cached.Binding;                 // null quando só há saldo
        var servico = Harmony.Legible(_palette.Service(grupo.Group), Dark);
        var cor = trava is null
            ? servico
            : _palette.ForReading(grupo.Group, trava.Percent, 0, 1, Dark);

        // Anel: trilho completo e, por cima, o arco do percentual a partir do topo.
        var d = Px(RingSize);
        var anel = new Rectangle(linha.Left + (linha.Width - d) / 2, linha.Top + Px(6), d, d);
        var espessura = Px(RingStroke);
        var arco = Rectangle.Inflate(anel, -espessura / 2, -espessura / 2);

        using (var trilho = new Pen(Palette.Track(cor, Dark), espessura))
            g.DrawEllipse(trilho, arco);

        if (trava is not null && trava.Percent > 0)
        {
            using var progresso = new Pen(cor, espessura) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var varredura = (float)Math.Max(4, 360 * Math.Clamp(trava.Percent, 0, 100) / 100.0);
            g.DrawArc(progresso, arco, -90, varredura);
        }

        // Erro sem nenhuma leitura guardada: anel em cinza, para não parecer "0 %".
        if (resumo is null)
        {
            using var cinza = new Pen(Harmony.Legible(_palette.Unknown, Dark), espessura);
            g.DrawEllipse(cinza, arco);
        }

        var b = Px(BadgeSize);
        ServiceBadge.Draw(g, new RectangleF(anel.Left + (d - b) / 2f, anel.Top + (d - b) / 2f, b, b), grupo.Group, servico, Dark);

        var texto = resumo switch
        {
            null => "?",
            { IsInformational: true } => "$" + resumo.IconText,
            _ => resumo.IconText + "%",
        };
        var areaTexto = new Rectangle(linha.Left, anel.Bottom + Px(3), linha.Width, Px(16));
        TextRenderer.DrawText(g, texto, _valueFont, areaTexto, resumo is null ? Muted : cor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>Contorno arredondado (todos os 4 cantos convexos) para modo Flutuante.</summary>
    internal static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1, radius * 2);
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Contorno do modo Notch: as quinas do lado encostado na borda da tela são côncavas
    /// (quarto de círculo para dentro), e as do lado de fora são convexas como sempre.
    /// </summary>
    internal static GraphicsPath NotchShape(Rectangle bounds, int radius, BordaTela borda)
    {
        var r = radius;
        var d = Math.Max(1, r * 2);
        var w = bounds.Width;
        var h = bounds.Height;

        var isHorizontal = borda is BordaTela.Topo or BordaTela.Base;
        var edgeLength = isHorizontal ? w : h;
        var popOut = isHorizontal ? h : w;

        var path = new GraphicsPath();

        // Constrói sempre como se fosse Topo (aresta plana no y=0, comprimento edgeLength, altura popOut)
        // Percorremos no sentido anti-horário (côncavas nos cantos colados na tela, convexas do lado solto)
        
        // Top-Left côncavo: de (0,0) até (r,r)
        path.AddArc(-r, 0, d, d, 270, 90);
        // Bottom-Left convexo: de (r, popOut - r) até (2r, popOut)
        path.AddArc(r, popOut - d, d, d, 180, -90);
        // Bottom-Right convexo: de (edgeLength - 2r, popOut) até (edgeLength - r, popOut - r)
        path.AddArc(edgeLength - 3 * r, popOut - d, d, d, 90, -90);
        // Top-Right côncavo: de (edgeLength - r, r) até (edgeLength, 0)
        path.AddArc(edgeLength - r, 0, d, d, 180, 90);
        
        path.CloseFigure();

        using var m = new Matrix();

        switch (borda)
        {
            case BordaTela.Topo:
                break;
            case BordaTela.Base:
                m.RotateAt(180, new PointF(w / 2f, h / 2f));
                break;
            case BordaTela.Esquerda:
                m.Translate(0, h);
                m.Rotate(-90);
                break;
            case BordaTela.Direita:
                m.Translate(w, 0);
                m.Rotate(90);
                break;
        }

        path.Transform(m);
        return path;
    }

    private int RowAt(Point p)
    {
        for (var i = 0; i < _groups.Count; i++)
            if (RowBounds(i).Contains(p)) return i;
        return -1;
    }

    /// <summary>Fork: texto simples de um serviço — fallback quando RichTooltips está desligado.</summary>
    internal static string TipFor(WidgetGroup grupo)
    {
        if (grupo.Rows.Count == 0) return $"{grupo.Group}\n{grupo.Error ?? "?"}";

        var linhas = grupo.Rows.Select(r => r.IsInformational
            ? $"{r.Label}: {r.AmountUnit} {r.Amount:N2}"
            : $"{r.Label}: {Math.Round(r.Percent)} %");
        var trava = ServiceGroup.Binding(grupo.Rows);
        var rodape = grupo.Error ?? trava?.ResetText();
        return $"{grupo.Group}\n{string.Join("\n", linhas)}" + (string.IsNullOrEmpty(rodape) ? "" : $"\n{rodape}");
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            var delta = e.Delta > 0 ? 0.1 : -0.1;
            _config.Widget.Scale = Math.Clamp(_config.Widget.Scale + delta, WidgetOptions.ScaleMin, WidgetOptions.ScaleMax);
            Relayout();
            Invalidate();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragOrigin = e.Location;
        _dragScreenOrigin = PointToScreen(e.Location);
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragging && e.Button == MouseButtons.Left)
        {
            var dx = e.X - _dragOrigin.X;
            var dy = e.Y - _dragOrigin.Y;
            if (Math.Abs(dx) > DragSlop || Math.Abs(dy) > DragSlop)
            {
                Location = new Point(Location.X + dx, Location.Y + dy);

                // Fork: preview das bordas candidatas durante arrasto no modo Notch
                if (IsNotch)
                    UpdateDragOverlay();
            }
            return;
        }

        var linha = RowAt(e.Location);
        if (linha == _hovered) return;

        _hovered = linha;
        Invalidate();

        // Fork: dispara o evento com o grupo e o retângulo em coordenadas de TELA,
        // para que o TrayApp posicione o cartão rico ao lado correto da faixa.
        if (linha >= 0)
        {
            var bounds = RowBounds(linha);
            var screenRect = RectangleToScreen(bounds);
            HoverChanged?.Invoke(this, new WidgetHoverEventArgs
            {
                Group = _groups[linha].Group,
                AnchorScreenRect = screenRect,
            });
        }
        else
        {
            HoverChanged?.Invoke(this, new WidgetHoverEventArgs { Group = null });
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var arrastou = _dragging && (Math.Abs(e.X - _dragOrigin.X) > DragSlop || Math.Abs(e.Y - _dragOrigin.Y) > DragSlop);
        _dragging = false;

        // Esconde o overlay de preview
        HideDragOverlay();

        if (!arrastou)
        {
            Clicked?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (IsNotch)
            SnapToNearestEdge();
        else
            SnapToNearestCorner();
    }

    /// <summary>
    /// Fork: no modo Notch, encaixa na borda mais próxima do monitor sob o cursor,
    /// salvando borda, nome do monitor e fração ao longo da borda.
    /// </summary>
    private void SnapToNearestEdge()
    {
        var centro = new Point(Bounds.Left + Width / 2, Bounds.Top + Height / 2);
        var screen = Screen.FromPoint(centro);
        var area = screen.WorkingArea;

        var borda = NearestEdge(centro, area);

        // Calcula a fração ao longo da borda
        var isHorizontal = borda is BordaTela.Topo or BordaTela.Base;
        var widgetLength = isHorizontal ? Width : Height;
        var fracao = PositionToFraction(new Point(Bounds.Left, Bounds.Top), borda, area, widgetLength);

        _config.Widget.Borda = borda;
        _config.Widget.Monitor = screen.DeviceName;
        _config.Widget.FracaoBorda = fracao;
        ConfigStore.Save(_config);

        _layoutSignature = ""; // Força recálculo
        Relayout();
    }

    /// <summary>
    /// Guarda o monitor e o canto mais próximos de onde a faixa foi solta, e encosta nele. Sem isso
    /// a posição arrastada se perderia no próximo início — e com dois monitores a faixa reapareceria
    /// no lugar errado.
    /// </summary>
    private void SnapToNearestCorner()
    {
        var centro = new Point(Bounds.Left + Width / 2, Bounds.Top + Height / 2);
        var screen = Screen.FromPoint(centro);
        var area = screen.WorkingArea;

        var yThreshold = area.Top + area.Height / 2;
        var topo = centro.Y < yThreshold;

        // Fork: Se o arraste para a posição central for intuitivo, podemos dividir a largura em 3.
        var third = area.Width / 3;
        var esquerda = centro.X < area.Left + third;
        var direita = centro.X > area.Right - third;

        string c = topo ? "top" : "bottom";
        if (esquerda) c += "Left";
        else if (direita) c += "Right";
        else c += "Center";

        _config.Widget.Monitor = screen.DeviceName;
        _config.Widget.Corner = c;
        ConfigStore.Save(_config);

        _layoutSignature = ""; // Fork: Força o recálculo dos bounds para encaixar (snap) mesmo se o canto final for igual
        Relayout();
    }

    /// <summary>Recentraliza na borda atual (item de menu).</summary>
    public void Recentralizar()
    {
        _config.Widget.FracaoBorda = 0.5;
        ConfigStore.Save(_config);
        _layoutSignature = "";
        Relayout();
    }

    // ------------------------------------------------------ drag overlay (preview das bordas)

    private void UpdateDragOverlay()
    {
        var centro = new Point(Bounds.Left + Width / 2, Bounds.Top + Height / 2);
        var screen = Screen.FromPoint(centro);
        var area = screen.WorkingArea;
        var candidata = NearestEdge(centro, area);

        if (_dragOverlay is null)
        {
            _dragOverlay = new DragOverlayForm();
            _dragOverlay.Show();
        }

        _dragOverlay.UpdatePreview(screen.Bounds, area, candidata);
    }

    private void HideDragOverlay()
    {
        _dragOverlay?.Close();
        _dragOverlay?.Dispose();
        _dragOverlay = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _valueFont?.Dispose();
            Region?.Dispose();
            HideDragOverlay();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Fork: overlay semitransparente que mostra as 4 bordas candidatas durante o arrasto
/// no modo Notch, destacando a que seria escolhida. Sem foco, sem Alt+Tab.
/// </summary>
internal sealed class DragOverlayForm : Form
{
    private const int EdgeThickness = 4;
    private static readonly Color NormalEdgeColor = Color.FromArgb(50, 100, 160, 255);
    private static readonly Color ActiveEdgeColor = Color.FromArgb(160, 60, 130, 255);

    private Rectangle _screenBounds;
    private Rectangle _workArea;
    private BordaTela _active;

    public DragOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x00000080 | 0x08000000 | 0x00000020; // TOOLWINDOW | NOACTIVATE | TRANSPARENT
            return p;
        }
    }

    public void UpdatePreview(Rectangle screenBounds, Rectangle workArea, BordaTela active)
    {
        _screenBounds = screenBounds;
        _workArea = workArea;
        _active = active;
        Bounds = screenBounds;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(TransparencyKey);

        var local = new Rectangle(
            _workArea.Left - _screenBounds.Left,
            _workArea.Top - _screenBounds.Top,
            _workArea.Width,
            _workArea.Height);

        DrawEdge(g, BordaTela.Esquerda, local);
        DrawEdge(g, BordaTela.Direita, local);
        DrawEdge(g, BordaTela.Topo, local);
        DrawEdge(g, BordaTela.Base, local);
    }

    private void DrawEdge(Graphics g, BordaTela borda, Rectangle area)
    {
        var color = borda == _active ? ActiveEdgeColor : NormalEdgeColor;
        using var pen = new Pen(color, EdgeThickness);

        switch (borda)
        {
            case BordaTela.Esquerda:
                g.DrawLine(pen, area.Left, area.Top, area.Left, area.Bottom);
                break;
            case BordaTela.Direita:
                g.DrawLine(pen, area.Right - 1, area.Top, area.Right - 1, area.Bottom);
                break;
            case BordaTela.Topo:
                g.DrawLine(pen, area.Left, area.Top, area.Right, area.Top);
                break;
            default: // Base
                g.DrawLine(pen, area.Left, area.Bottom - 1, area.Right, area.Bottom - 1);
                break;
        }
    }
}
