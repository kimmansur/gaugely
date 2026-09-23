using System.ComponentModel;
using System.Diagnostics;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;

namespace RateTray.Ui.Settings;

/// <summary>
/// Fork: janela de ajustes. Barra lateral com as páginas, cartões por fornecedor com os trilhos de
/// assinatura e de API, e o <c>settings.json</c> como fonte de verdade.
///
/// A janela edita uma <b>cópia</b> da configuração (<see cref="_draft"/>). "Salvar" copia os
/// valores para dentro dos objetos que estão valendo (<see cref="ConfigStore.CopyInto"/>) e grava o
/// arquivo; "Cancelar" só descarta a cópia. A exceção são as chaves de API: salvar e remover agem
/// na hora, direto no Cofre de Credenciais do Windows, porque nunca passam pelo arquivo.
/// </summary>
public sealed partial class SettingsWindow : Form
{
    private readonly AppConfig _config;
    private readonly AppConfig _draft;
    private readonly IReadOnlyList<LimitReading> _known;
    private readonly IReadOnlyList<ProviderResult> _results;
    private readonly SettingsTheme _theme;
    private readonly Action<UpdateCheck.Result>? _onUpdateChecked;

    private readonly Panel _sidebar = new();
    private readonly FlowLayoutPanel _navList = new();
    private readonly Panel _content = new();
    private readonly List<(NavItem Item, Func<Control> Build)> _nav = [];
    private readonly Dictionary<NavItem, Control> _built = [];
    private NavItem? _current;
    private Button? _save;

    /// <summary>Ações que copiam o estado dos controles para a cópia antes de salvar.</summary>
    private readonly List<Action> _commit = [];

    /// <summary>
    /// Efeitos fora do settings.json (a entrada de inicialização no registro): só rodam depois que
    /// o arquivo foi gravado, para uma gravação recusada não deixar metade aplicada.
    /// </summary>
    private readonly List<Action> _afterSave = [];

    public SettingsWindow(AppConfig config, IReadOnlyList<LimitReading> known, IReadOnlyList<ProviderResult> results,
                          Action<UpdateCheck.Result>? onUpdateChecked = null)
    {
        _config = config;
        _onUpdateChecked = onUpdateChecked;
        _draft = ConfigStore.Clone(config);
        _known = known;
        _results = results;
        _theme = SettingsTheme.For(config.Theme);

        Text = Loc.T("settings.title");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1080, 740);
        MinimumSize = new Size(860, 560);
        Font = SettingsTheme.UiFont(9.5f);
        KeyPreview = true;
        AppIcon.ApplyTo(this);

        if (SettingsTheme.RightToLeft) RightToLeft = RightToLeft.Yes;

        _theme.ApplyTo(this);
        HandleCreated += (_, _) => _theme.ApplyTo(this);

        BuildShell();
        AddPage("\uE71D", Loc.T("settings.nav.services"), ServicesPage);
        AddPage("\uE7F4", Loc.T("settings.nav.tray"), TrayPage);
        AddPage("\uE8A7", Loc.T("settings.nav.widget"), WidgetPage);
        AddPage("\uE790", Loc.T("settings.nav.appearance"), AppearancePage);
        AddPage("\uEA8F", Loc.T("settings.nav.alerts"), AlertsPage);
        AddPage("\uE895", Loc.T("settings.nav.updates"), UpdatesPage);
        AddPage("\uE943", Loc.T("settings.nav.advanced"), AdvancedPage);
        ShowNav(_nav[0].Item);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
    }

    /// <summary>
    /// O tamanho pedido é lógico; com a escala do Windows em 125–150 % ele passa da tela de um
    /// notebook. Depois da escala aplicada, a janela é encolhida para caber na área útil do
    /// monitor onde abre, e recentralizada nele.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var margin = Shapes.Scale(this, 16);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width - margin), Math.Min(MinimumSize.Height, area.Height - margin));
        Size = new Size(Math.Min(Width, area.Width - margin), Math.Min(Height, area.Height - margin));
        if (StartPosition == FormStartPosition.CenterScreen)
            Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
    }

    /// <summary>Abre direto numa página (pelo índice da barra lateral) — usado na renderização de prova.</summary>
    internal void ShowPage(int index) => ShowNav(_nav[Math.Clamp(index, 0, _nav.Count - 1)].Item);

    internal int PageCount => _nav.Count;

    /// <summary>O conteúdo inteiro da página aberta, além do que cabe na janela — para a prova visual.</summary>
    internal Control? CurrentContent => _current is not null && _built.TryGetValue(_current, out var page) && page.Controls.Count > 0
        ? page.Controls[0]
        : null;

    // ------------------------------------------------------------------ moldura

    private void BuildShell()
    {
        var rtl = SettingsTheme.RightToLeft;

        _sidebar.Dock = rtl ? DockStyle.Right : DockStyle.Left;
        _sidebar.Width = Shapes.Scale(this, 228);
        _sidebar.BackColor = _theme.Sidebar;
        _sidebar.Padding = new Padding(8, 10, 8, 10);

        // Ordem de encaixe do WinForms: quem entra primeiro na coleção é encaixado por último. A
        // lista (Fill) entra primeiro para ocupar o que sobrar entre a marca e o rodapé.
        _navList.Dock = DockStyle.Fill;
        _navList.FlowDirection = FlowDirection.TopDown;
        _navList.WrapContents = false;
        _navList.BackColor = _theme.Sidebar;
        _navList.Margin = Padding.Empty;
        _navList.Padding = new Padding(0, 6, 0, 0);
        _sidebar.Controls.Add(_navList);

        var footer = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = Shapes.Scale(this, 56),
            ForeColor = _theme.Muted,
            Font = SettingsTheme.UiFont(8.5f),
            Text = Loc.T("settings.footer", AppInfo.Version),
            TextAlign = rtl ? ContentAlignment.BottomRight : ContentAlignment.BottomLeft,
            Padding = new Padding(10, 0, 10, 4),
        };
        _sidebar.Controls.Add(footer);

        var brand = new BrandMark(_theme)
        {
            Dock = DockStyle.Top,
            Height = Shapes.Scale(this, 56),
            Font = SettingsTheme.UiFont(15f, FontStyle.Bold),
        };
        _sidebar.Controls.Add(brand);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = _theme.Window,
            Padding = new Padding(16, 10, 16, 12),
        };
        _save = Styled.Primary(new Button { Text = Loc.T("settings.save") }, _theme);
        var cancel = Styled.Secondary(new Button { Text = Loc.T("settings.cancel"), DialogResult = DialogResult.Cancel }, _theme);
        var openJson = Styled.Secondary(new Button { Text = Loc.T("settings.openJson") }, _theme);
        _save.Click += (_, _) => SaveAndClose();
        openJson.Click += (_, _) => OpenJson();
        buttons.Controls.Add(_save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(openJson);
        AcceptButton = _save;
        CancelButton = cancel;

        _content.Dock = DockStyle.Fill;
        _content.BackColor = _theme.Window;
        _content.Padding = new Padding(28, 22, 28, 8);

        Controls.Add(_content);
        Controls.Add(buttons);
        Controls.Add(_sidebar);
    }

    private void AddPage(string glyph, string title, Func<Control> build)
    {
        var item = new NavItem(_theme, glyph, title)
        {
            Font = SettingsTheme.UiFont(9.75f),
            Dock = DockStyle.None,
            Width = _sidebar.Width - _sidebar.Padding.Horizontal,
            Height = Shapes.Scale(this, 40),
        };
        item.Click += (_, _) => ShowNav(item);
        _nav.Add((item, build));
        _navList.Controls.Add(item);
    }

    private void ShowNav(NavItem item)
    {
        if (_current == item) return;
        foreach (var (nav, _) in _nav) nav.Selected = nav == item;
        _current = item;

        if (!_built.TryGetValue(item, out var page))
        {
            page = _nav.First(n => n.Item == item).Build();
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _built[item] = page;
            _content.Controls.Add(page);
        }

        foreach (var other in _built.Values) other.Visible = other == page;
    }

    // ------------------------------------------------------------------ salvar

    private void SaveAndClose()
    {
        // O settings.json mudou por fora com a janela aberta: perguntar antes de gravar por cima.
        if (ConfigStore.HasExternalEdit(countDeletion: true))
        {
            switch (MessageBox.Show(this, Loc.T("settings.conflict"), "Gaugely", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning))
            {
                case DialogResult.No:                   // fica o arquivo; a recarga pendente o aplica
                    DialogResult = DialogResult.Cancel;
                    Close();
                    return;
                case DialogResult.Cancel:
                    return;
            }
        }

        // O app continua consultando com a janela aberta e grava estado que não é ajuste do
        // usuário: limites descobertos, a última verificação de atualização, a posição da faixa
        // arrastada. A cópia é de quando a janela abriu; sem isto, salvar desfaria tudo isso.
        _draft.KnownReadingIds = [.. _config.KnownReadingIds];
        _draft.KnownGroups = [.. _config.KnownGroups];
        _draft.LastUpdateCheck = _config.LastUpdateCheck;
        _draft.IconsInitialized = _config.IconsInitialized;
        _draft.Icons = [.. _config.Icons];
        _draft.WidgetIcons = _config.WidgetIcons is null ? null : [.. _config.WidgetIcons];
        _draft.Widget.FracaoBorda = _config.Widget.FracaoBorda;

        // Só então as páginas abertas aplicam o que o usuário mudou (listas de ícones incluídas).
        foreach (var commit in _commit) commit();
        _draft.Normalize();
        // Grava antes de mexer no que está valendo: se o disco recusar, nada muda e a janela fica
        // aberta com as alterações, em vez de fechar como se tivesse salvo.
        var saved = ConfigStore.Clone(_config);
        ConfigStore.CopyInto(_draft, saved);
        if (!ConfigStore.Save(saved, overwriteExternalEdit: true))
        {
            MessageBox.Show(this, Loc.T("dialog.saveFailed", ConfigStore.Path_), "Gaugely", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ConfigStore.CopyInto(_draft, _config);
        foreach (var effect in _afterSave) effect();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OpenJson()
    {
        try
        {
            // Nada é gravado por este botão; o app cria o arquivo ao iniciar.
            if (!File.Exists(ConfigStore.Path_)) throw new FileNotFoundException(ConfigStore.Path_);
            Process.Start(new ProcessStartInfo(ConfigStore.Path_) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, Loc.T("dialog.settingsFailed", ex is FileNotFoundException ? ConfigStore.Path_ : ex.Message), "Gaugely",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ------------------------------------------------------------------ peças de página

    /// <summary>Página rolável: título, subtítulo e a pilha de seções.</summary>
    private (Panel Page, FlowLayoutPanel Stack) NewPage(string title, string? subtitle)
    {
        var page = new Panel { BackColor = _theme.Window, AutoScroll = true };
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = _theme.Window,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 8, 16),
            Location = new Point(0, 0),
        };

        stack.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = SettingsTheme.UiFont(20f, FontStyle.Bold),
            ForeColor = _theme.Text,
            Margin = new Padding(0, 0, 0, 2),
        });

        if (subtitle is not null)
            stack.Controls.Add(HintLabel(subtitle, 10));

        page.Controls.Add(stack);
        page.Resize += (_, _) => FitWidth(page, stack);
        page.HandleCreated += (_, _) =>
        {
            SettingsTheme.ThemeScrollBars(page, _theme.Dark);
            FitWidth(page, stack);
        };
        return (page, stack);
    }

    /// <summary>Largura útil de uma página para as seções ocuparem a coluna toda.</summary>
    private void FitWidth(Panel page, FlowLayoutPanel stack)
    {
        // Sem largura mínima: numa tela estreita ou com escala alta, um mínimo fixo virava rolagem
        // horizontal e cortava os cartões. O conteúdo quebra linha e os cartões viram uma coluna.
        var width = Math.Max(Shapes.Scale(this, 200), page.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        stack.MaximumSize = new Size(width, 0);
        foreach (Control child in stack.Controls)
        {
            if (child is CardPanel or FlowLayoutPanel || child.Tag as string == "full")
                child.Width = width - stack.Padding.Horizontal;
            if (child is Label { AutoSize: true } label)
                label.MaximumSize = new Size(width - stack.Padding.Horizontal, 0);
        }

        stack.PerformLayout();
    }

    /// <summary>Seção em cartão, com título opcional; devolve a grade onde entram as linhas.</summary>
    private TableLayoutPanel Section(FlowLayoutPanel stack, string? title, string? hint = null)
    {
        if (title is not null)
        {
            stack.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                Font = SettingsTheme.UiFont(10.5f, FontStyle.Bold),
                ForeColor = _theme.Text,
                Margin = new Padding(0, 14, 0, 6),
            });
        }

        var card = new CardPanel(_theme) { Margin = new Padding(0, 0, 0, 4) };
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = _theme.Surface,
            Margin = Padding.Empty,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.Controls.Add(grid);
        FitHeight(card, grid);

        if (hint is not null)
        {
            var label = HintLabel(hint, 4);
            grid.Controls.Add(label);
            grid.SetColumnSpan(label, 2);
        }

        stack.Controls.Add(card);
        return grid;
    }

    /// <summary>
    /// O cartão tem largura imposta pela página e altura tirada do conteúdo: AutoSize do WinForms
    /// não sabe fazer as duas coisas ao mesmo tempo.
    /// </summary>
    private static void FitHeight(CardPanel card, Control content)
    {
        void Fit() => card.Height = content.Height + card.Padding.Vertical;
        content.SizeChanged += (_, _) => Fit();
        Fit();
    }

    /// <summary>Linha "rótulo e descrição à esquerda, controle à direita", no estilo do Windows 11.</summary>
    private void Row(TableLayoutPanel grid, string label, Control control, string? description = null)
    {
        var text = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = _theme.Surface,
            Margin = new Padding(0, 6, 12, 6),
            Dock = DockStyle.Fill,
        };
        text.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = _theme.Text, Margin = Padding.Empty });
        if (description is not null)
        {
            var desc = new Label
            {
                Text = description,
                AutoSize = true,
                ForeColor = _theme.Muted,
                Font = SettingsTheme.UiFont(8.75f),
                Margin = new Padding(0, 2, 0, 0),
                MaximumSize = new Size(Shapes.Scale(this, 520), 0),
            };
            text.Controls.Add(desc);
        }

        Label(control, label, description);
        control.Anchor = SettingsTheme.RightToLeft ? AnchorStyles.Left : AnchorStyles.Right;
        control.Margin = new Padding(6, 6, 0, 6);
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(text);
        grid.Controls.Add(control);
        FitControl(grid, control);
    }

    /// <summary>
    /// A coluna dos controles assume a largura do controle mais largo da seção. Numa tela estreita
    /// ou com escala alta, uma lista de escolha larga deixava a coluna dos rótulos com zero e os
    /// rótulos sumiam. Aqui cada controle fica com no máximo metade da linha; painéis compostos
    /// (caminho + Procurar, tempo + intervalo) quebram linha, e a caixa de texto encolhe.
    /// </summary>
    private void FitControl(TableLayoutPanel grid, Control control)
    {
        var preferred = control.Width;
        var box = (control as FlowLayoutPanel)?.Controls.OfType<TextBoxBase>().FirstOrDefault();
        var boxPreferred = box?.Width ?? 0;

        void Fit()
        {
            if (grid.ClientSize.Width <= 0) return;
            var allowed = Math.Max(Shapes.Scale(this, 90), grid.ClientSize.Width / 2);
            switch (control)
            {
                case ChoiceBox or TextBoxBase or NumericUpDown:
                    control.Width = Math.Min(preferred, allowed);
                    break;
                case FlowLayoutPanel flow when box is not null:
                    // Caixa + botão: numa linha só, na metade da linha; quem cede espaço é a caixa.
                    var others = flow.Controls.Cast<Control>().Where(c => c != box).Sum(c => c.Width + c.Margin.Horizontal);
                    flow.WrapContents = false;
                    box.Width = Math.Max(Shapes.Scale(this, 60), Math.Min(boxPreferred, allowed - others - box.Margin.Horizontal));
                    break;
                case FlowLayoutPanel flow:
                    // Painel que quebra linha dentro de coluna de largura automática colapsa para um
                    // item por linha. Então: numa linha quando cabe; senão, largura fixa e quebra.
                    var room = Math.Max(allowed, grid.ClientSize.Width * 2 / 3);
                    var natural = flow.Controls.Cast<Control>().Sum(c => c.PreferredSize.Width + c.Margin.Horizontal) + flow.Padding.Horizontal;
                    var wraps = natural > room;
                    flow.WrapContents = wraps;
                    flow.MinimumSize = wraps ? new Size(room, 0) : Size.Empty;
                    flow.MaximumSize = wraps ? new Size(room, 0) : Size.Empty;
                    break;
            }
        }

        grid.SizeChanged += (_, _) => Fit();
        Fit();
    }

    /// <summary>
    /// O rótulo visível fica noutro painel; o leitor de tela precisa dele no próprio controle. Num
    /// painel com vários controles, o nome vai no primeiro que recebe foco (a caixa de texto do
    /// arquivo, os campos numéricos de tempo). Um controle que já tem nome próprio o mantém.
    /// </summary>
    internal static void Label(Control control, string label, string? description = null)
    {
        var target = control is ToggleSwitch or ChoiceBox or NumericUpDown or TextBoxBase or ButtonBase or LinkLabel
            ? control
            : control.Controls.Cast<Control>().FirstOrDefault(c => c.TabStop) ?? control;
        if (string.IsNullOrEmpty(target.AccessibleName)) target.AccessibleName = label;
        if (description is not null && string.IsNullOrEmpty(target.AccessibleDescription)) target.AccessibleDescription = description;
    }

    /// <summary>Anuncia ao leitor de tela o resultado de uma ação que só mudou um texto na tela.</summary>
    internal static void Announce(Control control, string text)
    {
        try
        {
            control.AccessibilityObject.RaiseAutomationNotification(
                System.Windows.Forms.Automation.AutomationNotificationKind.ActionCompleted,
                System.Windows.Forms.Automation.AutomationNotificationProcessing.MostRecent,
                text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>Controle ocupando a linha inteira da seção (listas, prévia).</summary>
    private static void Wide(TableLayoutPanel grid, Control control)
    {
        control.Dock = DockStyle.Fill;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(control);
        grid.SetColumnSpan(control, 2);
    }

    private Label HintLabel(string text, int bottom) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = _theme.Muted,
        MaximumSize = new Size(Shapes.Scale(this, 760), 0),
        Margin = new Padding(0, 4, 0, bottom),
    };

    /// <param name="name">Nome acessível para quando o controle não entra por <see cref="Row"/>.</param>
    private ToggleSwitch Toggle(string? name, bool value, Action<bool> commit)
    {
        var toggle = new ToggleSwitch(_theme, name ?? "") { Checked = value };
        _commit.Add(() => commit(toggle.Checked));
        return toggle;
    }

    private NumericUpDown Number(decimal min, decimal max, decimal value, Action<decimal> commit, decimal increment = 1)
    {
        var number = Styled.Input(new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Increment = increment,
            Value = Math.Clamp(value, min, max),
            Width = Shapes.Scale(this, 96),
            TextAlign = HorizontalAlignment.Right,
        }, _theme);
        _commit.Add(() => commit(number.Value));
        return number;
    }

    private ChoiceBox Choice(IReadOnlyList<(string Value, string Label)> options, string current, Action<string> commit, int width = 200)
    {
        var combo = new ChoiceBox(_theme) { Width = Shapes.Scale(this, width), Height = Shapes.Scale(this, 30) };
        foreach (var (_, label) in options) combo.Items.Add(label);
        var index = options.ToList().FindIndex(o => o.Value.Equals(current, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = Math.Max(0, index);
        _commit.Add(() => commit(options[Math.Max(0, combo.SelectedIndex)].Value));
        return combo;
    }

    private TextBox TextInput(string value, Action<string> commit, int width = 260)
    {
        var box = Styled.Input(new TextBox { Text = value, Width = Shapes.Scale(this, width) }, _theme);
        _commit.Add(() => commit(box.Text));
        return box;
    }
}
