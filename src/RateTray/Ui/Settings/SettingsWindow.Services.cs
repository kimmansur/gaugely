using System.Diagnostics;
using System.Globalization;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;
using RateTray.Providers;

namespace RateTray.Ui.Settings;

public sealed partial class SettingsWindow
{
    /// <summary>
    /// Fork: o que a tela precisa saber de cada serviço além do catálogo: nome do produto, como se
    /// autentica, qual chave do cofre usa, onde se gera a chave e o liga/desliga na configuração.
    /// </summary>
    private sealed record TrackInfo(
        string Group,
        string? Product,
        string AuthKey,
        string? VaultService,
        string? KeyUrl,
        string? NoteKey,
        Func<AppConfig, bool> GetEnabled,
        Action<AppConfig, bool> SetEnabled);

    private static readonly IReadOnlyList<TrackInfo> Tracks =
    [
        new("Claude", "Claude Code", "settings.auth.cli", null, null, null,
            c => c.Claude.Enabled, (c, v) => c.Claude.Enabled = v),
        new("Anthropic API", null, "settings.auth.admin", CredentialVault.AnthropicAdmin,
            "https://platform.claude.com/settings/admin-keys", "settings.note.anthropicOrg",
            c => c.AnthropicApi.Enabled, (c, v) => c.AnthropicApi.Enabled = v),
        new("Codex", "Codex", "settings.auth.cli", null, null, null,
            c => c.Codex.Enabled, (c, v) => c.Codex.Enabled = v),
        new("OpenAI API", null, "settings.auth.admin", CredentialVault.OpenAIAdmin,
            "https://platform.openai.com/settings/organization/admin-keys", "settings.note.openaiSpend",
            c => c.OpenAIApi.Enabled, (c, v) => c.OpenAIApi.Enabled = v),
        new("Antigravity", "AI Pro · Antigravity", "settings.auth.agy", null, null, null,
            c => c.Antigravity.Enabled, (c, v) => c.Antigravity.Enabled = v),
        new("Kimi", "Kimi Code", "settings.auth.vault", CredentialVault.Kimi, null, null,
            c => c.Kimi.Enabled, (c, v) => c.Kimi.Enabled = v),
        new("Kimi API", null, "settings.auth.platform", CredentialVault.KimiPlatform,
            "https://platform.kimi.ai/console/api-keys", "settings.note.kimiPlatform",
            c => c.KimiApi.Enabled, (c, v) => c.KimiApi.Enabled = v),
        new("OpenRouter", null, "settings.auth.vault", CredentialVault.OpenRouter,
            "https://openrouter.ai/settings/keys", null,
            c => c.OpenRouter.Enabled, (c, v) => c.OpenRouter.Enabled = v),
        new("DeepSeek", null, "settings.auth.vault", CredentialVault.DeepSeek,
            "https://platform.deepseek.com/api_keys", null,
            c => c.DeepSeek.Enabled, (c, v) => c.DeepSeek.Enabled = v),
    ];

    /// <summary>
    /// Se há chave no cofre. A renderização de prova troca por "nunca": as imagens publicadas não
    /// podem contar quais chaves existem na máquina de quem as gerou.
    /// </summary>
    internal static Func<string, bool> HasKey { get; set; } = CredentialVault.Has;

    private Control ServicesPage()
    {
        var (page, stack) = NewPage(Loc.T("settings.nav.services"), Loc.T("settings.services.subtitle"));
        stack.Controls.Add(new Chip(_theme, Loc.T("settings.services.vaultBanner")) { Margin = new Padding(0, 2, 0, 14) });

        // Duas colunas posicionadas à mão: FlowLayoutPanel com AutoSize cresce para o lado em vez
        // de quebrar a linha, e os cartões mudam de altura quando o "Testar" escreve o resultado.
        var grid = new Panel { BackColor = _theme.Window, Margin = Padding.Empty, Tag = "full" };
        var cards = ServiceCatalog.VendorOrder.Select(VendorCard).ToList();
        foreach (var card in cards) grid.Controls.Add(card);

        var busy = false;
        void Relayout()
        {
            if (busy || grid.Width <= 0) return;
            busy = true;
            try
            {
                var gap = Shapes.Scale(this, 12);
                // Duas colunas só quando cada cartão tem pelo menos ~300 px lógicos; abaixo disso
                // (tela estreita, escala alta) os botões e textos não cabem, e vira uma coluna.
                var columns = (grid.Width - gap) / 2 >= Shapes.Scale(this, 300) ? 2 : 1;
                var width = columns == 2 ? (grid.Width - gap) / 2 : grid.Width;
                var rtl = SettingsTheme.RightToLeft;
                var y = 0;
                for (var i = 0; i < cards.Count; i += columns)
                {
                    var first = cards[i];
                    var second = columns == 2 && i + 1 < cards.Count ? cards[i + 1] : null;
                    first.Width = width;
                    if (second is not null) second.Width = width;
                    first.Location = new Point(second is not null && rtl ? width + gap : 0, y);
                    if (second is not null) second.Location = new Point(rtl ? 0 : width + gap, y);
                    y += Math.Max(first.Height, second?.Height ?? 0) + gap;
                }

                grid.Height = y;
            }
            finally
            {
                busy = false;
            }
        }

        grid.SizeChanged += (_, _) => Relayout();
        foreach (var card in cards) card.SizeChanged += (_, _) => Relayout();
        stack.Controls.Add(grid);
        return page;
    }

    /// <summary>Cartão de um fornecedor: logo, nome e um bloco por trilho (assinatura, API).</summary>
    private CardPanel VendorCard(string vendor)
    {
        var card = new CardPanel(_theme);
        var body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = _theme.Surface,
            Margin = Padding.Empty,
        };
        card.Controls.Add(body);
        FitHeight(card, body);

        var services = ServiceCatalog.OfVendor(vendor).ToList();
        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = _theme.Surface, Margin = new Padding(0, 0, 0, 8) };
        // O cartão da OpenAI leva a marca da OpenAI, não a do Codex, que é só um dos produtos.
        var logoGroup = vendor == Vendors.OpenAI ? "OpenAI API" : services[0].Group;
        if (ServiceBadge.LogoFor(logoGroup, _theme.Dark) is { } logo)
        {
            var size = Shapes.Scale(this, 24);
            header.Controls.Add(new PictureBox
            {
                Image = logo,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(size, size),
                Margin = new Padding(0, 1, 8, 0),
                BackColor = _theme.Surface,
            });
        }
        header.Controls.Add(new Label
        {
            Text = vendor == Vendors.Google ? "Gemini · Antigravity" : vendor,
            AutoSize = true,
            Font = SettingsTheme.UiFont(11f, FontStyle.Bold),
            ForeColor = _theme.Text,
            Margin = new Padding(0, 2, 0, 0),
        });
        body.Controls.Add(header);

        foreach (var service in services)
        {
            if (Tracks.FirstOrDefault(t => t.Group == service.Group) is { } info)
                body.Controls.Add(TrackBlock(service, info, body));
        }

        // Google: o trilho de API não existe — o Google não informa gasto por chave. Mostrar a
        // ausência explicada é melhor que um cartão pela metade sem motivo.
        if (vendor == Vendors.Google)
            body.Controls.Add(MissingApiBlock(body));

        body.SizeChanged += (_, _) =>
        {
            foreach (Control child in body.Controls)
                if (child is CardPanel block) block.Width = body.ClientSize.Width;
        };
        return card;
    }

    private CardPanel TrackBlock(ServiceRecord service, TrackInfo info, Control host)
    {
        var block = new CardPanel(_theme) { Fill = _theme.SurfaceAlt, Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 0, 0, 8) };
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = _theme.SurfaceAlt,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        block.Controls.Add(grid);
        FitHeight(block, grid);

        // Rótulos quebram na largura do bloco, que só se conhece depois do encaixe.
        var wrapped = new List<Control>();
        block.SizeChanged += (_, _) =>
        {
            var width = Math.Max(Shapes.Scale(this, 120), block.ClientSize.Width - block.Padding.Horizontal - 2);
            foreach (var control in wrapped)
            {
                control.MaximumSize = new Size(width, 0);
                if (control is TextBox) control.Width = width;       // a caixa da chave ocupa a linha
            }
        };
        T Wrap<T>(T control) where T : Control { wrapped.Add(control); return control; }

        // Título do trilho e forma de acesso, com o liga/desliga à direita.
        var title = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = _theme.SurfaceAlt, Margin = Padding.Empty, Dock = DockStyle.Fill };
        var track = Loc.T(service.Track == ServiceTrack.Api ? "settings.track.api" : "settings.track.subscription");
        title.Controls.Add(Wrap(new Label
        {
            Text = (info.Product is null ? track : $"{track} · {info.Product}").ToUpper(Loc.Culture),
            AutoSize = true,
            Font = SettingsTheme.UiFont(8.5f, FontStyle.Bold),
            ForeColor = _theme.SubText,
            Margin = new Padding(0, 3, 0, 0),
        }));
        title.Controls.Add(new Chip(_theme, Loc.T(info.AuthKey)) { Font = SettingsTheme.UiFont(8f), Margin = new Padding(0, 4, 0, 0) });

        var toggle = Toggle($"{service.Group} · {track}", info.GetEnabled(_draft), v => info.SetEnabled(_draft, v));
        toggle.Anchor = AnchorStyles.Top;
        grid.Controls.Add(title, 0, 0);
        grid.Controls.Add(toggle, 1, 0);

        // O que a última leitura disse.
        var status = Wrap(new Label { AutoSize = true, ForeColor = _theme.Text, Margin = new Padding(0, 8, 0, 2) });
        var bar = new ThinBar(_theme) { Margin = new Padding(0, 3, 0, 3), Visible = false };
        var detail = Wrap(new Label { AutoSize = true, ForeColor = _theme.Muted, Font = SettingsTheme.UiFont(8.75f), Margin = new Padding(0, 2, 0, 0) });
        Wide(grid, status);
        Wide(grid, bar);
        Wide(grid, detail);
        ShowStatus(info, _results.FirstOrDefault(r => r.Group == service.Group), status, bar, detail);

        if (info.NoteKey is not null)
            Wide(grid, Wrap(new Label { Text = Loc.T(info.NoteKey), AutoSize = true, ForeColor = _theme.Muted, Font = SettingsTheme.UiFont(8.5f), Margin = new Padding(0, 6, 0, 0) }));

        if (info.VaultService is { } vaultService)
            Wide(grid, KeyEditor(service, info, vaultService, status, bar, detail, Wrap));

        return block;
    }

    private CardPanel MissingApiBlock(Control host)
    {
        var block = new CardPanel(_theme) { Fill = _theme.SurfaceAlt, Dashed = true, Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 0, 0, 8) };
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Top, AutoSize = true, BackColor = _theme.SurfaceAlt };
        var text = new Label { Text = Loc.T("settings.note.geminiApi"), AutoSize = true, ForeColor = _theme.Muted, Margin = new Padding(0, 4, 0, 0) };
        body.Controls.Add(new Label { Text = Loc.T("settings.track.api").ToUpper(Loc.Culture), AutoSize = true, Font = SettingsTheme.UiFont(8.5f, FontStyle.Bold), ForeColor = _theme.Muted });
        body.Controls.Add(text);
        block.Controls.Add(body);
        FitHeight(block, body);
        block.SizeChanged += (_, _) => text.MaximumSize = new Size(Math.Max(Shapes.Scale(this, 120), block.ClientSize.Width - block.Padding.Horizontal - 2), 0);
        return block;
    }

    /// <summary>Caixa mascarada; "Salvar no cofre", "Remover", "Testar"; o estado e o link para gerar a chave.</summary>
    private Control KeyEditor(ServiceRecord service, TrackInfo info, string vaultService, Label status, ThinBar bar, Label detail, Func<Control, Control> wrap)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = _theme.SurfaceAlt, Margin = new Padding(0, 10, 0, 0) };

        var box = Styled.Input(new TextBox
        {
            UseSystemPasswordChar = true,
            PlaceholderText = Loc.T("settings.keys.placeholder"),
            Margin = new Padding(0, 0, 0, 6),
            AccessibleName = Loc.T("settings.keys.label", service.Group),
        }, _theme);
        wrap(box);

        // Em alemão e francês os três botões não cabem numa linha do cartão: quebram, sem cortar.
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = _theme.SurfaceAlt, Margin = Padding.Empty };
        wrap(buttons);
        var save = Styled.Primary(new Button { Text = Loc.T("settings.keys.save"), Margin = new Padding(0, 0, 6, 6) }, _theme);
        var remove = Styled.Secondary(new Button { Text = Loc.T("settings.keys.remove"), Margin = new Padding(0, 0, 6, 6) }, _theme);
        var test = Styled.Secondary(new Button { Text = Loc.T("settings.keys.test"), Margin = new Padding(0, 0, 0, 6) }, _theme);
        buttons.Controls.AddRange([save, remove, test]);

        var footer = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = _theme.SurfaceAlt, Margin = Padding.Empty };
        wrap(footer);
        var state = new Label { AutoSize = true, Margin = new Padding(0, 0, 12, 0), Font = SettingsTheme.UiFont(8.75f) };
        footer.Controls.Add(state);
        if (info.KeyUrl is { } url)
        {
            var link = new LinkLabel
            {
                Text = Loc.T("settings.keys.getKey") + " \u2197",
                AutoSize = true,
                LinkColor = _theme.ChipText,
                ActiveLinkColor = _theme.Accent,
                VisitedLinkColor = _theme.ChipText,
                Margin = Padding.Empty,
                Font = SettingsTheme.UiFont(8.75f),
            };
            link.LinkClicked += (_, _) => OpenUrl(url);
            footer.Controls.Add(link);
        }

        panel.Controls.Add(box);
        panel.Controls.Add(buttons);
        panel.Controls.Add(footer);

        void ShowState()
        {
            var has = HasKey(vaultService);
            state.Text = (has ? "\u25CF " : "\u25CB ") + Loc.T(has ? "settings.keys.configured" : "settings.keys.notConfigured");
            state.ForeColor = has ? _theme.Good : _theme.Muted;
            remove.Enabled = has;
            test.Enabled = has;
        }

        async Task RunTest()
        {
            test.Enabled = false;
            status.ForeColor = _theme.Muted;
            status.Text = Loc.T("settings.keys.testing");
            bar.Visible = false;
            detail.Text = "";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var provider = ProviderFactory.For(service.Group, _draft);
                var result = provider is null ? null : await provider.ReadAsync(cts.Token);
                if (!IsDisposed)
                {
                    ShowStatus(info, result, status, bar, detail, tested: true);
                    Announce(test, status.Text);
                }
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed)
                {
                    status.ForeColor = _theme.Bad;
                    status.Text = Loc.T("settings.keys.testTimeout");
                    Announce(test, status.Text);
                }
            }
            finally
            {
                if (!IsDisposed) ShowState();
            }
        }

        save.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            var ok = CredentialVault.Write(vaultService, box.Text.Trim());
            box.Clear();                                // a chave não fica na tela um instante a mais
            ShowState();
            if (!ok)
            {
                MessageBox.Show(this, Loc.T("settings.keys.saveFailed"), "Gaugely", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            await RunTest();                            // prova na hora que a chave serve
        };

        remove.Click += (_, _) =>
        {
            box.Clear();
            if (!CredentialVault.Delete(vaultService))
                MessageBox.Show(this, Loc.T("settings.keys.removeFailed"), "Gaugely", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowState();
            ShowStatus(info, null, status, bar, detail);
        };

        test.Click += async (_, _) => await RunTest();

        // Enter na caixa grava no cofre, em vez de acionar o Salvar da janela e deixar a chave para trás.
        box.Enter += (_, _) => AcceptButton = save;
        box.Leave += (_, _) => AcceptButton = _save;

        ShowState();
        return panel;
    }

    /// <summary>A linha de estado de um trilho, a partir de um resultado (da bandeja ou do "Testar").</summary>
    private void ShowStatus(TrackInfo info, ProviderResult? result, Label status, ThinBar bar, Label detail, bool tested = false)
    {
        bar.Visible = false;
        detail.Text = "";
        detail.Visible = false;
        status.ForeColor = _theme.Text;

        if (result is null)
        {
            var needsKey = info.VaultService is { } v && !HasKey(v);
            status.ForeColor = _theme.Muted;
            status.Text = Loc.T(needsKey ? "settings.status.noKey"
                : info.GetEnabled(_draft) ? "settings.status.waiting" : "settings.status.off");
            return;
        }

        if (result.Readings.Count == 0)
        {
            status.ForeColor = _theme.Bad;
            status.Text = result.Error ?? Loc.T("settings.status.waiting");
            return;
        }

        var ordered = result.Readings
            .OrderByDescending(r => r.IsInformational ? -1 : r.Percent)
            .ToList();
        var main = ordered[0];
        status.Text = (tested ? "✓ " : "") + Describe(main);
        if (!main.IsInformational)
        {
            bar.Value = main.Percent;
            bar.BarColor = main.Percent >= _draft.Thresholds.Critical ? _theme.Bad
                : main.Percent >= _draft.Thresholds.Warn ? Color.FromArgb(240, 169, 59) : _theme.Accent;
            bar.Visible = true;
        }

        var rest = ordered.Skip(1).Take(2).Select(Describe).ToList();
        if (main.Note is { Length: > 0 } note) rest.Insert(0, note);
        detail.Text = string.Join("  ·  ", rest);
        if (result.Error is { } error) detail.Text += (detail.Text.Length > 0 ? "\n" : "") + error;
        detail.Visible = detail.Text.Length > 0;
    }

    private static string Describe(LimitReading r) => r.Amount is { } amount
        ? $"{r.Label} · {r.AmountUnit} {amount.ToString("N2", Loc.Culture)}"
        : $"{r.Label} · {Math.Round(r.Percent).ToString("0", CultureInfo.InvariantCulture)}%";

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
