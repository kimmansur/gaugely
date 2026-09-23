using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;

namespace RateTray.Ui.Settings;

public sealed partial class SettingsWindow
{
    // ------------------------------------------------------------------ bandeja

    private Control TrayPage()
    {
        var (page, stack) = NewPage(Loc.T("settings.nav.tray"), Loc.T("settings.tray.subtitle"));

        var general = Section(stack, null);
        Row(general, Loc.T("settings.tray.group"), Toggle(null, _draft.GroupByService, v => _draft.GroupByService = v),
            Loc.T("settings.tray.groupHint"));
        Row(general, Loc.T("settings.tray.rich"), Toggle(null, _draft.RichTooltips, v => _draft.RichTooltips = v),
            Loc.T("settings.tray.richHint"));

        var autostart = Toggle(null, AutoStart.IsEnabled, _ => { });
        _afterSave.Add(() =>
        {
            if (autostart.Checked == AutoStart.IsEnabled) return;
            if (!AutoStart.TrySet(autostart.Checked, out var error))
                MessageBox.Show(this, Loc.T("dialog.autostartFailed", error), "Gaugely", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
        Row(general, Loc.T("settings.tray.autostart"), autostart);

        ReadingChecklist(stack, Loc.T("settings.tray.icons"), Loc.T("settings.tray.iconsHint"),
            _draft.Icons, () => _draft.Icons, ids => _draft.Icons = ids);

        var rediscover = Section(stack, null);
        Row(rediscover, Loc.T("settings.tray.rediscover"), Toggle(null, false, v =>
        {
            if (v) _draft.IconsInitialized = false;
            else if (_draft.Icons.Count > 0) _draft.IconsInitialized = true;
        }), Loc.T("settings.tray.rediscoverHint"));

        return page;
    }

    /// <summary>
    /// Uma chave por janela de limite, agrupada por serviço. A ordem da lista salva é preservada:
    /// o que continua marcado fica onde estava, o que foi marcado agora entra no fim. Um limite que
    /// o app descobriu com a janela aberta não tem chave aqui e fica como o app deixou.
    /// </summary>
    private void ReadingChecklist(FlowLayoutPanel stack, string title, string hint, IReadOnlyList<string> selected,
                                  Func<IReadOnlyList<string>> current, Action<List<string>> commit)
    {
        var grid = Section(stack, title, hint);

        var entries = _known
            .Select(r => (r.Id, r.Label, r.Group))
            .Concat(selected
                .Where(id => _known.All(r => !r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .Select(id => (Id: id, Label: Loc.T("menu.notReported", id), Group: ServiceCatalog.GetByPrefix(id).Group)))
            .DistinctBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => ServiceCatalog.Services.ToList().FindIndex(s => s.Group == e.Group))
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        if (entries.Count == 0)
        {
            Wide(grid, HintLabel(Loc.T("settings.tray.noData"), 4));
            return;
        }

        var toggles = new List<(string Id, ToggleSwitch Toggle)>();
        string? group = null;
        foreach (var entry in entries)
        {
            if (entry.Group != group)
            {
                group = entry.Group;
                Wide(grid, new Label
                {
                    Text = group,
                    AutoSize = true,
                    Font = SettingsTheme.UiFont(8.5f, FontStyle.Bold),
                    ForeColor = _theme.SubText,
                    Margin = new Padding(0, toggles.Count == 0 ? 2 : 12, 0, 0),
                });
            }

            var toggle = new ToggleSwitch(_theme, entry.Label) { Checked = selected.Contains(entry.Id, StringComparer.OrdinalIgnoreCase) };
            toggles.Add((entry.Id, toggle));
            Row(grid, entry.Label, toggle);
        }

        _commit.Add(() =>
        {
            // Página aberta sem mexer em nada não muda a configuração — em especial, a lista da
            // faixa continua nula e seguindo a da bandeja.
            if (toggles.All(t => t.Toggle.Checked == selected.Contains(t.Id, StringComparer.OrdinalIgnoreCase))) return;

            var on = toggles.Where(t => t.Toggle.Checked).Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shown = toggles.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ordered = current().Where(id => on.Contains(id) || !shown.Contains(id)).ToList();
            ordered.AddRange(toggles.Select(t => t.Id).Where(id => on.Contains(id) && !ordered.Contains(id, StringComparer.OrdinalIgnoreCase)));
            commit(ordered);
        });
    }

    // ------------------------------------------------------------------ faixa

    private Control WidgetPage()
    {
        var (page, stack) = NewPage(Loc.T("settings.nav.widget"), Loc.T("settings.widget.subtitle"));
        var w = _draft.Widget;

        var basics = Section(stack, null);
        Row(basics, Loc.T("settings.widget.enabled"), Toggle(null, w.Enabled, v => w.Enabled = v), Loc.T("settings.widget.enabledHint"));

        var mode = Choice(
            [(nameof(ModoApresentacao.Flutuante), Loc.T("settings.widget.mode.floating")), (nameof(ModoApresentacao.Notch), Loc.T("settings.widget.mode.notch"))],
            w.Modo.ToString(), v => w.Modo = Enum.Parse<ModoApresentacao>(v));
        Row(basics, Loc.T("settings.widget.mode"), mode, Loc.T("settings.widget.modeHint"));

        var edge = Choice(
            [
                (nameof(BordaTela.Esquerda), Loc.T("settings.widget.edge.left")),
                (nameof(BordaTela.Direita), Loc.T("settings.widget.edge.right")),
                (nameof(BordaTela.Topo), Loc.T("settings.widget.edge.top")),
                (nameof(BordaTela.Base), Loc.T("settings.widget.edge.bottom")),
            ],
            w.Borda.ToString(), v => w.Borda = Enum.Parse<BordaTela>(v));
        Row(basics, Loc.T("settings.widget.edge"), edge);

        var corner = Choice(
            [
                ("topLeft", Loc.T("settings.widget.corner.topLeft")),
                ("topCenter", Loc.T("settings.widget.corner.topCenter")),
                ("topRight", Loc.T("settings.widget.corner.topRight")),
                ("bottomLeft", Loc.T("settings.widget.corner.bottomLeft")),
                ("bottomCenter", Loc.T("settings.widget.corner.bottomCenter")),
                ("bottomRight", Loc.T("settings.widget.corner.bottomRight")),
            ],
            w.Corner, v => w.Corner = v);
        Row(basics, Loc.T("settings.widget.corner"), corner);

        var orientation = Choice(
            [("vertical", Loc.T("settings.widget.vertical")), ("horizontal", Loc.T("settings.widget.horizontal"))],
            w.Orientation, v => w.Orientation = v);
        Row(basics, Loc.T("settings.widget.orientation"), orientation);

        // Borda só vale no Notch; canto e orientação só na faixa flutuante.
        void SyncMode()
        {
            var notch = mode.SelectedIndex == 1;
            edge.Enabled = notch;
            corner.Enabled = !notch;
            orientation.Enabled = !notch;
        }

        mode.SelectedIndexChanged += (_, _) => SyncMode();
        SyncMode();

        var scale = Number((decimal)WidgetOptions.ScaleMin, (decimal)WidgetOptions.ScaleMax, (decimal)Math.Round(w.Scale, 1),
            v => w.Scale = (double)v, 0.1m);
        scale.DecimalPlaces = 1;
        Row(basics, Loc.T("settings.widget.scale"), scale);

        var screens = Screen.AllScreens;
        var monitors = new List<(string, string)> { ("", Loc.T("settings.widget.monitor.primary")) };
        for (var i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            monitors.Add((screens[i].DeviceName, Loc.T("settings.widget.monitor.item", i + 1, $"{b.Width}×{b.Height}")));
        }

        Row(basics, Loc.T("settings.widget.monitor"), Choice(monitors, w.Monitor ?? "", v => w.Monitor = v.Length == 0 ? null : v, 240));
        Row(basics, Loc.T("settings.widget.handle"), Toggle(null, w.MostrarAlca, v => w.MostrarAlca = v), Loc.T("settings.widget.handleHint"));

        // Fork: forma do mostrador, com prévia desenhada pelo mesmo código da faixa.
        var dials = Section(stack, Loc.T("settings.widget.dial"), Loc.T("settings.widget.dialHint"));
        var dial = Choice(
            [
                ("gauge", Loc.T("settings.widget.dial.gauge")),
                ("semicircle", Loc.T("settings.widget.dial.semicircle")),
                ("segmented", Loc.T("settings.widget.dial.segmented")),
            ],
            w.Dial, v => w.Dial = v);
        Row(dials, Loc.T("settings.widget.dial"), dial);
        var preview = new Panel { Height = Shapes.Scale(this, 80), Margin = new Padding(0, 6, 0, 4) };
        preview.Paint += (_, e) => PaintDialPreview(e.Graphics, preview, dial.SelectedIndex switch
        {
            1 => DialStyle.Semicircle,
            2 => DialStyle.Segmented,
            _ => DialStyle.Gauge,
        });
        dial.SelectedIndexChanged += (_, _) => preview.Invalidate();
        Wide(dials, preview);

        ReadingChecklist(stack, Loc.T("settings.widget.items"), Loc.T("settings.widget.itemsHint"),
            _draft.WidgetIcons ?? _draft.Icons, () => _draft.WidgetIcons ?? _draft.Icons, ids => _draft.WidgetIcons = ids);

        return page;
    }

    /// <summary>Três serviços de exemplo, como na faixa: abaixo do alerta, no alerta e quase vazio.</summary>
    private void PaintDialPreview(Graphics g, Panel panel, DialStyle style)
    {
        var palette = new Palette(_draft);
        var dark = _theme.Dark;
        using (var back = new SolidBrush(dark ? Color.FromArgb(24, 25, 28) : Color.FromArgb(238, 238, 238)))
        using (var path = Shapes.Rounded(new RectangleF(0, 0, panel.Width - 1, panel.Height - 1), Shapes.Scale(this, 8)))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(panel.Parent?.BackColor ?? _theme.Surface);
            g.FillPath(back, path);
        }

        (string Group, double? Percent)[] samples = [("Claude", 32), ("Codex", 76), ("Kimi", 5), ("OpenRouter", null)];
        var d = Shapes.Scale(this, 42);
        var cell = Shapes.Scale(this, 64);
        var total = samples.Length * cell;
        var x = SettingsTheme.RightToLeft ? panel.Width - Shapes.Scale(this, 12) - total : Shapes.Scale(this, 12);
        using var font = SettingsTheme.UiFont(8.5f, FontStyle.Bold);
        foreach (var (group, percent) in samples)
        {
            var service = Harmony.Legible(palette.Service(group), dark);
            var color = percent is { } p ? palette.ForReading(group, p, 0, 1, dark) : service;
            var square = new Rectangle(x + (cell - d) / 2, Shapes.Scale(this, 8), d, d);
            Dial.Draw(g, square, style, Shapes.Scale(this, 4), color, Palette.Track(color, dark), percent);
            ServiceBadge.Draw(g, Dial.BadgeBox(square, style, Shapes.Scale(this, 22)), group, service, dark);
            var text = percent is { } q ? $"{q:0}%" : "$12";
            TextRenderer.DrawText(g, text, font, new Rectangle(x, square.Bottom + Shapes.Scale(this, 4), cell, Shapes.Scale(this, 18)), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            x += cell;
        }
    }

    // ------------------------------------------------------------------ aparência

    private static readonly IReadOnlyDictionary<string, Action<ColorOptions, string>> ColorSetters =
        new Dictionary<string, Action<ColorOptions, string>>
        {
            ["Claude"] = (c, v) => c.Claude = v,
            ["Anthropic API"] = (c, v) => c.AnthropicApi = v,
            ["Codex"] = (c, v) => c.Codex = v,
            ["OpenAI API"] = (c, v) => c.OpenAIApi = v,
            ["Antigravity"] = (c, v) => c.Antigravity = v,
            ["Kimi"] = (c, v) => c.Kimi = v,
            ["Kimi API"] = (c, v) => c.KimiApi = v,
            ["OpenRouter"] = (c, v) => c.OpenRouter = v,
            ["DeepSeek"] = (c, v) => c.DeepSeek = v,
        };

    private Control AppearancePage()
    {
        var (page, stack) = NewPage(Loc.T("settings.nav.appearance"), Loc.T("settings.appearance.subtitle"));

        var basics = Section(stack, null);
        var languages = new List<(string, string)> { ("auto", Loc.T("menu.languageAuto", Loc.DisplayName(Loc.SystemLanguage))) };
        languages.AddRange(Loc.Available.Select(code => (code, Loc.DisplayName(code))));
        Row(basics, Loc.T("settings.language"), Choice(languages, _draft.Language, v => _draft.Language = v, 240),
            Loc.T("settings.languageHint"));
        Row(basics, Loc.T("settings.theme"), Choice(
            [("auto", Loc.T("settings.theme.auto")), ("light", Loc.T("settings.theme.light")), ("dark", Loc.T("settings.theme.dark"))],
            _draft.Theme, v => _draft.Theme = v, 240), Loc.T("settings.themeHint"));

        var font = TextInput(_draft.FontFamily, v => _draft.FontFamily = string.IsNullOrWhiteSpace(v) ? "Segoe UI" : v.Trim(), 200);
        Row(basics, Loc.T("settings.fontFamily"), font);

        var colors = Section(stack, Loc.T("settings.colors"), Loc.T("settings.color.derived"));
        var preview = new Panel { Height = Shapes.Scale(this, 44), Margin = new Padding(0, 4, 0, 8) };
        var swatches = new Dictionary<string, Button>();

        foreach (var service in ServiceCatalog.Services)
        {
            // Código de cor é texto da esquerda para a direita em qualquer idioma: sem isto, o árabe
            // mostrava "D97757#".
            var swatch = Styled.Secondary(new Button { AutoSize = false, Width = Shapes.Scale(this, 96), Height = Shapes.Scale(this, 28), RightToLeft = RightToLeft.No }, _theme);
            swatch.FlatAppearance.BorderColor = _theme.Border;
            PaintSwatch(swatch, new Palette(_draft).Service(service.Group));
            swatch.AccessibleName = Loc.T("settings.color.of", service.Group);
            swatch.Click += (_, _) =>
            {
                using var dialog = new ColorDialog { Color = swatch.BackColor, FullOpen = true };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                ColorSetters[service.Group](_draft.Colors, Hex(dialog.Color));
                PaintSwatch(swatch, dialog.Color);
                preview.Invalidate();
            };
            swatches[service.Group] = swatch;
            Row(colors, service.Group, swatch);
        }

        var warnHue = Number(0, 359, _draft.Colors.WarnHue, v => _draft.Colors.WarnHue = (int)v);
        var criticalHue = Number(0, 359, _draft.Colors.CriticalHue, v => _draft.Colors.CriticalHue = (int)v);
        var spread = Number(0, 50, (decimal)Math.Round(_draft.Colors.ShadeSpread * 100), v => _draft.Colors.ShadeSpread = (double)v / 100.0);
        Row(colors, Loc.T("settings.color.warnHue"), warnHue);
        Row(colors, Loc.T("settings.color.criticalHue"), criticalHue);
        Row(colors, Loc.T("settings.color.shadeSpread"), spread, Loc.T("settings.color.shadeSpreadHint"));

        foreach (var number in new[] { warnHue, criticalHue, spread })
            number.ValueChanged += (_, _) =>
            {
                _draft.Colors.WarnHue = (int)warnHue.Value;
                _draft.Colors.CriticalHue = (int)criticalHue.Value;
                _draft.Colors.ShadeSpread = (double)spread.Value / 100.0;
                preview.Invalidate();
            };
        font.TextChanged += (_, _) => preview.Invalidate();

        Wide(colors, new Label { Text = Loc.T("settings.preview"), AutoSize = true, ForeColor = _theme.SubText, Margin = new Padding(0, 10, 0, 0) });
        preview.Paint += (_, e) => PaintPreview(e.Graphics, preview, font.Text);
        Wide(colors, preview);

        var reset = Styled.Secondary(new Button { Text = Loc.T("settings.color.reset") }, _theme);
        reset.Anchor = SettingsTheme.RightToLeft ? AnchorStyles.Right : AnchorStyles.Left;
        reset.Click += (_, _) =>
        {
            var defaults = new ColorOptions();
            ConfigStore.CopyInto(defaults, _draft.Colors);
            foreach (var (group, swatch) in swatches) PaintSwatch(swatch, new Palette(_draft).Service(group));
            warnHue.Value = defaults.WarnHue;
            criticalHue.Value = defaults.CriticalHue;
            spread.Value = (decimal)Math.Round(defaults.ShadeSpread * 100);
            preview.Invalidate();
        };
        reset.Margin = new Padding(0, 4, 0, 2);
        colors.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        colors.Controls.Add(reset);
        colors.SetColumnSpan(reset, 2);

        return page;
    }

    private static void PaintSwatch(Button swatch, Color color)
    {
        swatch.BackColor = color;
        swatch.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color, 0.2f);
        swatch.ForeColor = Harmony.ToHsl(color).L > 0.55 ? Color.Black : Color.White;
        swatch.Text = Hex(color);
    }

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Os ícones de verdade da bandeja com as cores pendentes: três limites do Claude para ver o
    /// sombreamento, um de cada outro serviço de assinatura, e os estados de alerta e crítico.
    /// </summary>
    private void PaintPreview(Graphics g, Panel panel, string fontFamily)
    {
        var palette = new Palette(_draft);
        var dark = TrayIconRenderer.UsesDarkTaskbar(_draft.Theme);
        using (var back = new SolidBrush(dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(238, 238, 238)))
        using (var path = Shapes.Rounded(new RectangleF(0, 0, panel.Width - 1, panel.Height - 1), Shapes.Scale(this, 6)))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(panel.Parent?.BackColor ?? _theme.Surface);
            g.FillPath(back, path);
        }

        (string Group, double Percent, int Variant, int Count)[] samples =
        [
            ("Claude", 12, 0, 3),
            ("Claude", 24, 1, 3),
            ("Claude", 18, 2, 3),
            ("Codex", 8, 0, 1),
            ("Antigravity", 31, 0, 1),
            ("Kimi", 5, 0, 1),
            ("Claude", _draft.Thresholds.Warn, 0, 3),
            ("Codex", _draft.Thresholds.Critical, 0, 1),
        ];

        var size = TrayIconRenderer.IconSize;
        var gap = Shapes.Scale(this, 10);
        var total = samples.Length * size + (samples.Length - 1) * gap;
        var x = SettingsTheme.RightToLeft ? panel.Width - Shapes.Scale(this, 12) - total : Shapes.Scale(this, 12);
        foreach (var (group, percent, variant, count) in samples)
        {
            var color = palette.ForReading(group, percent, variant, count, dark);
            using var icon = TrayIconRenderer.Render(Math.Round(percent).ToString("0"), color, fontFamily);
            g.DrawIcon(icon, new Rectangle(x, (panel.Height - size) / 2, size, size));
            x += size + gap;
        }
    }

    // ------------------------------------------------------------------ alertas

    private Control AlertsPage()
    {
        var (page, stack) = NewPage(Loc.T("settings.nav.alerts"), Loc.T("settings.alerts.subtitle"));

        var levels = Section(stack, Loc.T("settings.alerts.levels"), Loc.T("settings.alerts.levelsHint"));
        var warn = Number(1, 100, _draft.Thresholds.Warn, v => _draft.Thresholds.Warn = (int)v);
        var critical = Number(1, 100, _draft.Thresholds.Critical, v => _draft.Thresholds.Critical = (int)v);
        Row(levels, Loc.T("settings.warn"), warn);
        Row(levels, Loc.T("settings.critical"), critical);
        _commit.Add(() =>
        {
            // Aviso acima do crítico é legível mas estranho; a janela entrega sempre em ordem.
            var (low, high) = ((int)Math.Min(warn.Value, critical.Value), (int)Math.Max(warn.Value, critical.Value));
            _draft.Thresholds.Warn = low;
            _draft.Thresholds.Critical = high;
        });

        var notify = Section(stack, Loc.T("settings.alerts.notifications"));
        var enabled = Toggle(null, _draft.Notifications.Enabled, v => _draft.Notifications.Enabled = v);
        var at = Number(1, 100, _draft.Notifications.AtPercent, v => _draft.Notifications.AtPercent = (int)v);
        Row(notify, Loc.T("settings.notifyEnabled"), enabled, Loc.T("settings.notifyHint"));
        Row(notify, Loc.T("settings.notifyAt"), at);
        enabled.CheckedChanged += (_, _) => at.Enabled = enabled.Checked;
        at.Enabled = enabled.Checked;

        return page;
    }

    // ------------------------------------------------------------------ atualizações

    private Control UpdatesPage()
    {
        var (page, stack) = NewPage(Loc.T("settings.nav.updates"), Loc.T("settings.updates.subtitle"));

        var grid = Section(stack, null);
        var version = new Label { Text = AppInfo.Version, AutoSize = true, ForeColor = _theme.Text, Font = SettingsTheme.UiFont(9.5f, FontStyle.Bold) };
        Row(grid, Loc.T("settings.updates.current"), version);
        Row(grid, Loc.T("about.autoCheck"), Toggle(null, _draft.AutoUpdateCheck, v => _draft.AutoUpdateCheck = v),
            Loc.T("settings.updates.autoHint"));

        var check = Styled.Secondary(new Button { Text = Loc.T("about.checkUpdates") }, _theme);
        var status = new Label { AutoSize = true, ForeColor = _theme.Muted, Margin = new Padding(0, 8, 0, 0), Visible = false };
        Row(grid, Loc.T("settings.updates.now"), check, Loc.T("settings.updates.nowHint"));
        Wide(grid, status);

        check.Click += async (_, _) =>
        {
            check.Enabled = false;
            status.Visible = true;
            status.ForeColor = _theme.Muted;
            status.Text = Loc.T("about.checking");
            var result = await UpdateCheck.LatestAsync(AppInfo.SemVer);
            if (IsDisposed) return;
            check.Enabled = true;
            if (result is null)
            {
                status.ForeColor = _theme.Bad;
                status.Text = Loc.T("about.checkFailed");
                Announce(check, status.Text);
                return;
            }

            _onUpdateChecked?.Invoke(result);
            status.ForeColor = result.IsNewer ? _theme.Accent : _theme.Good;
            status.Text = result.IsNewer
                ? Loc.T("settings.updates.available", result.Latest.ToString(3))
                : Loc.T("about.upToDate", result.Latest.ToString(3));
            Announce(check, status.Text);
        };

        var about = Section(stack, null);
        Row(about, Loc.T("about.issues"), LinkTo(Loc.T("settings.updates.issuesLink"), AppInfo.IssuesUrl));
        Row(about, Loc.T("about.basedOn"), LinkTo("nowrap/rate-tray", AppInfo.UpstreamUrl));

        return page;
    }

    private LinkLabel LinkTo(string text, string url)
    {
        var link = new LinkLabel
        {
            Text = text + " ↗",
            AutoSize = true,
            LinkColor = _theme.ChipText,
            ActiveLinkColor = _theme.Accent,
            VisitedLinkColor = _theme.ChipText,
        };
        link.LinkClicked += (_, _) => OpenUrl(url);
        return link;
    }

    // ------------------------------------------------------------------ avançado

    private Control AdvancedPage()
    {
        var (page, stack) = NewPage(Loc.T("settings.nav.advanced"), Loc.T("settings.advanced.subtitle"));

        var polling = Section(stack, Loc.T("settings.advanced.polling"));
        Row(polling, Loc.T("settings.refresh"), Number(30, 3600, _draft.RefreshSeconds, v => _draft.RefreshSeconds = (int)v, 10),
            Loc.T("settings.refreshHint"));
        Row(polling, Loc.T("settings.maxBackoff"), Number(1, 120, _draft.MaxBackoffMinutes, v => _draft.MaxBackoffMinutes = (int)v),
            Loc.T("settings.maxBackoffHint"));

        var claude = Section(stack, "Claude Code");
        var path = TextInput(_draft.Claude.CredentialsPath ?? "", v => _draft.Claude.CredentialsPath = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), 240);
        path.PlaceholderText = Loc.T("settings.claude.credentialsDefault");
        var browse = Styled.Secondary(new Button { Text = Loc.T("settings.browse"), Margin = new Padding(6, 0, 0, 0) }, _theme);
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "JSON|*.json|*.*|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.FileName;
        };
        var pathRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = _theme.Surface, Margin = Padding.Empty };
        pathRow.Controls.Add(path);
        pathRow.Controls.Add(browse);
        Row(claude, Loc.T("settings.claude.credentials"), pathRow);
        Row(claude, Loc.T("settings.claude.autoRefresh"), Toggle(null, _draft.Claude.AutoRefreshToken, v => _draft.Claude.AutoRefreshToken = v),
            Loc.T("settings.claude.autoRefreshHint"));

        var timing = Section(stack, Loc.T("settings.advanced.timing"), Loc.T("settings.advanced.timingHint"));
        TimingRow(timing, "Claude", _draft.Claude.TimeoutSeconds, v => _draft.Claude.TimeoutSeconds = v, _draft.Claude.MinIntervalSeconds, v => _draft.Claude.MinIntervalSeconds = v, 0);
        TimingRow(timing, "Anthropic API", _draft.AnthropicApi.TimeoutSeconds, v => _draft.AnthropicApi.TimeoutSeconds = v, _draft.AnthropicApi.MinIntervalSeconds, v => _draft.AnthropicApi.MinIntervalSeconds = v, 60);
        TimingRow(timing, "Codex", _draft.Codex.TimeoutSeconds, v => _draft.Codex.TimeoutSeconds = v, null, null, 0);
        TimingRow(timing, "OpenAI API", _draft.OpenAIApi.TimeoutSeconds, v => _draft.OpenAIApi.TimeoutSeconds = v, _draft.OpenAIApi.MinIntervalSeconds, v => _draft.OpenAIApi.MinIntervalSeconds = v, 60);
        TimingRow(timing, "Antigravity", _draft.Antigravity.TimeoutSeconds, v => _draft.Antigravity.TimeoutSeconds = v, _draft.Antigravity.MinIntervalSeconds, v => _draft.Antigravity.MinIntervalSeconds = v, 0);
        TimingRow(timing, "Kimi", _draft.Kimi.TimeoutSeconds, v => _draft.Kimi.TimeoutSeconds = v, _draft.Kimi.MinIntervalSeconds, v => _draft.Kimi.MinIntervalSeconds = v, 0);
        TimingRow(timing, "Kimi API", _draft.KimiApi.TimeoutSeconds, v => _draft.KimiApi.TimeoutSeconds = v, _draft.KimiApi.MinIntervalSeconds, v => _draft.KimiApi.MinIntervalSeconds = v, 60);
        TimingRow(timing, "OpenRouter", _draft.OpenRouter.TimeoutSeconds, v => _draft.OpenRouter.TimeoutSeconds = v, _draft.OpenRouter.MinIntervalSeconds, v => _draft.OpenRouter.MinIntervalSeconds = v, 0);
        TimingRow(timing, "DeepSeek", _draft.DeepSeek.TimeoutSeconds, v => _draft.DeepSeek.TimeoutSeconds = v, _draft.DeepSeek.MinIntervalSeconds, v => _draft.DeepSeek.MinIntervalSeconds = v, 60);

        var json = Section(stack, Loc.T("settings.advanced.json"), Loc.T("settings.advanced.jsonHint"));
        var file = new Label { Text = ConfigStore.Path_, AutoSize = true, ForeColor = _theme.SubText, Font = new Font("Consolas", 9f), Margin = new Padding(0, 4, 0, 4) };
        Wide(json, file);

        return page;
    }

    /// <summary>Tempo limite e intervalo mínimo de um serviço, lado a lado.</summary>
    private void TimingRow(TableLayoutPanel grid, string service, int timeout, Action<int> setTimeout,
                           int? interval, Action<int>? setInterval, int intervalMin)
    {
        var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = _theme.Surface, Margin = Padding.Empty };
        line.Controls.Add(new Label { Text = Loc.T("settings.advanced.timeout"), AutoSize = true, ForeColor = _theme.Muted, Margin = new Padding(0, 6, 4, 0) });
        var t = Number(5, 300, timeout, v => setTimeout((int)v));
        t.Width = Shapes.Scale(this, 72);
        t.AccessibleName = $"{service} · {Loc.T("settings.advanced.timeout")}";
        line.Controls.Add(t);
        if (interval is { } value && setInterval is not null)
        {
            line.Controls.Add(new Label { Text = Loc.T("settings.advanced.interval"), AutoSize = true, ForeColor = _theme.Muted, Margin = new Padding(14, 6, 4, 0) });
            var i = Number(intervalMin, 86_400, value, v => setInterval((int)v), 30);
            i.Width = Shapes.Scale(this, 88);
            i.AccessibleName = $"{service} · {Loc.T("settings.advanced.interval")}";
            line.Controls.Add(i);
        }

        Row(grid, service, line);
    }
}
