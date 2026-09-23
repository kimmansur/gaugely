using System.Diagnostics;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;
using RateTray.Providers;
using RateTray.Ui;

namespace RateTray;

/// <summary>
/// Owns the tray icons and the polling loop. One <see cref="NotifyIcon"/> per configured
/// limit, Core Temp style, all sharing a single context menu.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    /// <summary>Hover cards are hidden once the tray stops reporting mouse movement.</summary>
    private const int TooltipIdleMs = 700;

    /// <summary>How far the pointer may drift and still count as "still parked on the icon".</summary>
    private const int HoverSlack = 4;

    /// <summary>Floor for the poll interval — the usage endpoint rate-limits a tight loop.</summary>
    private const int MinRefreshSeconds = 30;

    private readonly AppConfig _config;
    private readonly Palette _palette;
    private readonly List<IUsageProvider> _providers;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Windows.Forms.Timer _tooltipTimer = new();
    private readonly System.Windows.Forms.Timer _layoutSaveTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _reloadTimer = new() { Interval = 700 };   // Fork: recarga do settings.json
    private FileSystemWatcher? _configWatcher;
    private bool _reloadPending;
    private int _reloadRetries;
    private readonly Dictionary<string, TrayIcon> _icons = [];
    private readonly ContextMenuStrip _menu = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Keys of "already warned" windows, so a toast fires once per reset period.</summary>
    private readonly HashSet<string> _notified = [];

    private ToolStripMenuItem _iconsMenu = null!;
    private ToolStripMenuItem _widgetIconsMenu = null!; // Fork: submenu separado para a faixa
    private ToolStripMenuItem _languageMenu = null!;
    private ToolStripMenuItem _aboutMenu = null!;
    private NotifyIcon? _neutralIcon;               // Fork: ícone neutro quando não há ícone de serviço e a faixa está desligada
    private DetailsForm? _details;
    private WidgetForm? _widget;                 // Fork: faixa flutuante, ligada pelo menu
    private TooltipWindow? _tooltip;
    private UpdateCheck.Result? _latestUpdate;

    /// <summary>The one modal dialog (About or Settings) allowed open at a time.</summary>
    private Form? _dialog;
    private DateTime _lastHover = DateTime.MinValue;
    private Point _lastHoverPos;
    private string? _hoveredId;

    // Fork: quem abriu o cartão rico — impede HideTooltipWhenIdle de esconder o cartão da faixa.
    private enum TooltipOwner { None, Tray, Widget }
    private TooltipOwner _tooltipOwner;

    /// <summary>Fork: tooltip nativa usada pela faixa quando RichTooltips está desligado.</summary>
    private ToolTip? _widgetTip;

    private IReadOnlyList<ProviderResult> _lastResults = [];
    private Dictionary<string, LimitReading> _lastReadings = [];
    private DateTimeOffset? _lastUpdate;
    private bool _refreshing;

    /// <summary>When the timer fires next, drawn as the countdown strip in the details window.</summary>
    private DateTimeOffset _nextPoll;

    /// <summary>Last readings that actually arrived, per provider, so a failed poll can keep
    /// showing numbers instead of blanking the tray. Persisted across restarts.</summary>
    private readonly Dictionary<string, CachedReadings> _lastGood;

    /// <summary>Decides which providers may be polled after a failure.</summary>
    private readonly PollScheduler _schedule;

    /// <summary>
    /// Poll interval in milliseconds. Multiplied as a <c>long</c> on purpose: a hand-edited
    /// refreshSeconds big enough to overflow an int used to arrive as a negative interval, which
    /// the timer rejects — the config is clamped, and this keeps the arithmetic safe regardless.
    /// </summary>
    private int PollIntervalMs => (int)Math.Clamp(
        (long)Math.Max(MinRefreshSeconds, _config.RefreshSeconds) * 1000,
        MinRefreshSeconds * 1000L,
        int.MaxValue);

    public TrayApp()
    {
        _config = ConfigStore.Load();
        Loc.Use(_config.Language);

        _palette = new Palette(_config);
        _providers = ProviderFactory.All(_config);

        _lastGood = UsageCache.Load();
        _schedule = new PollScheduler(_config.MaxBackoffMinutes);
        ApplyPollSpacing();

        BuildMenu();
        _menu.Opened += (_, _) => HideTooltip();
        ShowCachedReadings();
        if (_config.Widget.Enabled) SetWidget(true);   // Fork: a faixa volta como o usuário deixou

        _timer.Interval = PollIntervalMs;
        _timer.Tick += (_, _) => { ScheduleNextPoll(); _ = RefreshAsync(); };
        _timer.Start();
        ScheduleNextPoll();

        _tooltipTimer.Interval = 200;
        _tooltipTimer.Tick += (_, _) => HideTooltipWhenIdle();

        _layoutSaveTimer.Tick += (_, _) =>
        {
            _layoutSaveTimer.Stop();
            ConfigStore.Save(_config);
        };

        WatchConfigFile();

        _ = RefreshAsync();

        // Fork: se a inicialização chegou até aqui, a versão instalada sobe — o binário anterior
        // deixa de ser o retorno possível e pode sair do disco.
        UpdateInstaller.CleanupOld(UpdateInstaller.CurrentExecutable());
        AutoStart.MigrateLegacy();

        MaybeCheckForUpdates();
    }

    // ---------------------------------------------------------------- polling

    /// <param name="force">
    /// Set by the menu's refresh command: an explicit request from the user clears any
    /// backoff, because they are entitled to a retry now even if the last poll failed.
    /// </param>
    private async Task RefreshAsync(bool force = false)
    {
        if (_refreshing) return;                 // a slow poll must not stack up behind the timer
        _refreshing = true;

        if (force) _schedule.Reset();

        try
        {
            var tasks = _providers
                .Where(p => p.Enabled)
                .Select(async p =>
                {
                    // A provider that just failed is left alone for a while. Without this a
                    // rate-limited usage endpoint would be hammered once a minute forever,
                    // which is what got it rate-limited in the first place.
                    if (!_schedule.ShouldPoll(p.Group, DateTimeOffset.Now))
                        return (Result: LastResultFor(p.Group) with { RetryAt = _schedule.RetryAt(p.Group) }, Polled: false);

                    try { return (Result: await p.ReadAsync(_shutdown.Token).ConfigureAwait(false), Polled: true); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return (Result: ProviderResult.Failed(p.Group, ex.Message), Polled: true); }
                });

            // Only cycles that actually reached a provider are recorded. Counting a skipped
            // one would extend the very pause that caused the skip, and nothing would ever be
            // retried again.
            var results = (await Task.WhenAll(tasks).ConfigureAwait(true))
                .Select(outcome => outcome.Polled ? RememberAndRestore(outcome.Result) : outcome.Result)
                .ToList();

            _lastResults = results;
            _lastReadings = results
                .SelectMany(r => r.Readings)
                .GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            // The oldest of the values on display, not the newest: one provider polling fine
            // would otherwise put its own time under numbers the other one has been serving from
            // cache for hours. Only providers in this cycle count, so a disabled one cannot
            // age the footer with an entry nothing is drawing.
            _lastUpdate = results
                .Select(r => _lastGood.TryGetValue(r.Group, out var entry) ? entry.FetchedAt : (DateTimeOffset?)null)
                .Where(fetched => fetched is not null)
                .Min() ?? DateTimeOffset.Now;

            SeedIconsOnFirstRun();
            SyncIcons();
            RaiseNotifications();
            RefreshIconsMenu();
            _widget?.Apply(WidgetGroups());

            if (_details is { Visible: true }) _details.ShowNearTray(_lastResults, _lastUpdate, _nextPoll);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// Paints whatever the cache holds before the first poll returns, so a restart does not
    /// start with a row of "?" — least of all when polling is slow because something is wrong.
    /// </summary>
    private void ShowCachedReadings()
    {
        if (_lastGood.Count == 0) return;

        _lastResults = _lastGood
            .Select(entry => ProviderResult.Success(entry.Key, entry.Value.Readings))
            .ToList();
        _lastReadings = _lastResults
            .SelectMany(r => r.Readings)
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _lastUpdate = _lastGood.Values.Min(entry => entry.FetchedAt);

        SyncIcons();
        RefreshIconsMenu();
        _widget?.Apply(WidgetGroups());
    }

    /// <summary>
    /// Records a successful poll, and on a failed one hands back the last numbers that did
    /// arrive. A transient error — a rate limit, a dropped connection — should show up as a
    /// message next to slightly stale values, not blank every icon out.
    /// </summary>
    private ProviderResult RememberAndRestore(ProviderResult result)
    {
        var now = DateTimeOffset.Now;

        if (result.Ok)
        {
            _lastGood[result.Group] = new CachedReadings(now, result.Readings);
            _schedule.RecordSuccess(result.Group, now);
            UsageCache.Save(_lastGood);
            return result;
        }

        var retryAt = _schedule.RecordFailure(
            result.Group, result.RetryAfter, now, _config.RefreshSeconds, result.RateLimited);

        if (!_lastGood.TryGetValue(result.Group, out var previous))
            return result with { RetryAt = retryAt };

        // Numbers that are too old to load from disk are too old to keep showing. Dropping the
        // entry also stops it being written back to the cache on the next successful poll.
        if (!UsageCache.IsFresh(previous, now))
        {
            _lastGood.Remove(result.Group);
            UsageCache.Save(_lastGood);
            return result with { RetryAt = retryAt };
        }

        return result with { Readings = previous.Readings, RetryAt = retryAt };
    }

    private void ScheduleNextPoll() => _nextPoll = DateTimeOffset.Now.AddMilliseconds(_timer.Interval);

    /// <summary>
    /// Hands each provider's own floor to the scheduler, so the timer may tick as often as the
    /// fastest of them allows. Asked of the providers rather than decided here: whether a poll
    /// costs anything is a property of what is being polled — a local process that answers for
    /// free, or a metered endpoint the tray is not the only client of.
    /// </summary>
    private void ApplyPollSpacing()
    {
        _schedule.MaxBackoffMinutes = _config.MaxBackoffMinutes;
        foreach (var provider in _providers) _schedule.SetMinInterval(provider.Group, provider.MinInterval);
    }

    private ProviderResult LastResultFor(string group) =>
        _lastResults.FirstOrDefault(r => r.Group == group)
        ?? ProviderResult.Failed(group, Loc.T("error.backoff"));

    /// <summary>
    /// Shows every limit the account reports the first time round. Only after a poll do we
    /// know whether this plan has a session window, per-model windows such as Fable, or a
    /// second Codex bucket — so the icon list is discovered, not guessed.
    ///
    /// Seeds from whatever answered, rather than waiting for a complete set: someone signed in
    /// to only one of the two CLIs, or holding a rate limit, would otherwise be left with an
    /// empty tray forever. The list stays open until every enabled provider has been heard from
    /// once, so a service that recovers later still contributes its limits.
    /// </summary>
    private void SeedIconsOnFirstRun()
    {
        // Fork: janelas vistas neste ciclo, inclusive as restauradas do cache de um provedor que falhou,
        // para um serviço fora do ar não parecer "inédito" quando voltar.
        var vistas = _lastResults.SelectMany(result => result.Readings).ToList();
        var mudou = false;

        // Fork: migração automática — na primeira vez que há leituras e WidgetIcons ainda é nulo,
        // copia Icons para que a faixa comece idêntica à bandeja de hoje.
        if (_config.WidgetIcons is null && vistas.Count > 0)
        {
            _config.WidgetIcons = new List<string>(_config.Icons);
            mudou = true;
        }

        foreach (var grupo in vistas.GroupBy(reading => reading.Group, StringComparer.OrdinalIgnoreCase))
        {
            var ids = grupo.Select(reading => reading.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (!_config.KnownGroups.Contains(grupo.Key, StringComparer.OrdinalIgnoreCase))
            {
                // Primeira vez que este serviço é examinado. Com alguma janela já em Icons, a escolha
                // do usuário vem de antes (configuração antiga): só registra, sem reativar desmarcadas.
                // Sem nenhuma, é serviço novo, ou um que a versão anterior do fork exibia inteiro
                // (lista vazia = tudo): entra todo marcado, visível como estava.
                if (!ids.Any(id => _config.Icons.Contains(id, StringComparer.OrdinalIgnoreCase)))
                    _config.Icons.AddRange(ids);
                // Fork: espelha em WidgetIcons com a mesma regra — serviço novo aparece nas duas listas.
                if (_config.WidgetIcons is not null &&
                    !ids.Any(id => _config.WidgetIcons.Contains(id, StringComparer.OrdinalIgnoreCase)))
                    _config.WidgetIcons.AddRange(ids);
                _config.KnownGroups.Add(grupo.Key);
                mudou = true;   // Fork: registrar o grupo precisa ser salvo mesmo sem janela nova
            }
            else
            {
                // Serviço conhecido: só janela inédita entra marcada; conhecida e desmarcada fica fora.
                var novasParaIcons = ids.Where(id =>
                    !_config.KnownReadingIds.Contains(id, StringComparer.OrdinalIgnoreCase) &&
                    !_config.Icons.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
                _config.Icons.AddRange(novasParaIcons);
                // Fork: espelha janelas inéditas em WidgetIcons — conhecida e desmarcada não reaparece.
                if (_config.WidgetIcons is not null)
                {
                    var novasParaWidget = ids.Where(id =>
                        !_config.KnownReadingIds.Contains(id, StringComparer.OrdinalIgnoreCase) &&
                        !_config.WidgetIcons.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
                    _config.WidgetIcons.AddRange(novasParaWidget);
                }
            }

            var novasConhecidas = ids.Where(id => !_config.KnownReadingIds.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
            if (novasConhecidas.Count > 0) { _config.KnownReadingIds.AddRange(novasConhecidas); mudou = true; }
        }
        if (mudou) ConfigStore.Save(_config);

        if (_config.IconsInitialized) return;

        var added = _lastResults
            .Where(result => result.Ok)
            .SelectMany(result => result.Readings)
            .OrderBy(reading => reading.Group, StringComparer.Ordinal)
            .ThenBy(reading => reading.Id, StringComparer.Ordinal)
            .Where(reading => !_config.Icons.Contains(reading.Id, StringComparer.OrdinalIgnoreCase))
            .Select(reading => reading.Id)
            .ToList();

        _config.Icons.AddRange(added);
        // Fork: upstream seed também espelha em WidgetIcons
        _config.WidgetIcons?.AddRange(added.Where(id =>
            !_config.WidgetIcons.Contains(id, StringComparer.OrdinalIgnoreCase)));

        var complete = _lastResults.Count > 0 && _lastResults.All(result => result.Ok);
        if (complete) _config.IconsInitialized = true;

        if (added.Count > 0 || complete) ConfigStore.Save(_config);
    }

    // ------------------------------------------------------------- tray icons

    /// <summary>
    /// Fork: leituras de um serviço filtradas por uma lista de ícones. Método puro extraído para
    /// viabilizar testes unitários sem instanciar TrayApp.
    /// </summary>
    internal static IReadOnlyList<LimitReading> FilteredReadingsOf(
        IReadOnlyList<LimitReading> readings,
        List<string> iconList,
        IReadOnlyList<string> knownGroups,
        string group)
    {
        var marcadas = readings.Where(r => iconList.Contains(r.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (knownGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
            return ServiceGroup.Ordered(marcadas);
        return ServiceGroup.Ordered(marcadas.Count == 0 ? readings : marcadas);
    }

    /// <summary>
    /// Fork: limites para os ícones da bandeja — filtra por <see cref="AppConfig.Icons"/>.
    /// </summary>
    private IReadOnlyList<LimitReading> TrayReadingsOf(string group)
    {
        var readings = _lastResults.FirstOrDefault(r => r.Group == group)?.Readings ?? [];
        return FilteredReadingsOf(readings, _config.Icons, _config.KnownGroups, group);
    }

    /// <summary>
    /// Fork: limites para a faixa flutuante — filtra por <see cref="AppConfig.WidgetIcons"/>,
    /// caindo para <see cref="AppConfig.Icons"/> enquanto a migração não aconteceu (nulo).
    /// </summary>
    private IReadOnlyList<LimitReading> WidgetReadingsOf(string group)
    {
        var readings = _lastResults.FirstOrDefault(r => r.Group == group)?.Readings ?? [];
        var list = _config.WidgetIcons ?? _config.Icons;
        return FilteredReadingsOf(readings, list, _config.KnownGroups, group);
    }

    /// <summary>
    /// Fork: um ícone por serviço, com o número do limite que trava primeiro. Um serviço cujo
    /// provedor falhou sem nenhuma leitura guardada aparece como "?" — sumir com ele esconderia
    /// justamente o problema.
    /// </summary>
    private void SyncGroupedIcons()
    {
        var wanted = new List<string>();
        var dark = TrayIconRenderer.UsesDarkTaskbar(_config.Theme);

        foreach (var result in _lastResults)
        {
            var rows = TrayReadingsOf(result.Group);
            if (rows.Count == 0 && result.Error is null) continue;

            var id = ServiceGroup.IdFor(result.Group);
            wanted.Add(id);
            var icon = _icons.TryGetValue(id, out var existing) ? existing : CreateIcon(id);

            var summary = ServiceGroup.Summary(result.Group, rows);
            var color = summary is null
                ? Harmony.Legible(_palette.Unknown, dark)
                : _palette.ForReading(summary.Group, summary.Percent, 0, 1, dark);

            var previous = icon.Icon;
            icon.Icon = TrayIconRenderer.Render(summary?.IconText ?? "?", color, _config.FontFamily);
            previous?.Dispose();

            icon.Text = _config.RichTooltips
                ? string.Empty
                : Clamp(summary is null
                    ? $"{result.Group}\n{result.Error ?? "?"}"
                    : $"{result.Group} {LimitReading.FormatValue(summary)}: " + string.Join(" · ", rows.Select(LimitReading.FormatValue)));
            icon.Visible = true;
        }

        foreach (var stale in _icons.Keys.Except(wanted, StringComparer.OrdinalIgnoreCase).ToList())
            RemoveIcon(stale);
    }

    private void SyncIcons()
    {
        if (_config.GroupByService)
        {
            SyncGroupedIcons();
            SyncNeutralIcon();  // Fork: requisito 5 — verifica ícone neutro também no modo agrupado
            return;
        }

        var wanted = new List<string>();

        foreach (var id in _config.Icons)
        {
            var reading = _lastReadings.GetValueOrDefault(id);
            var error = reading is null ? ErrorForId(id) : null;

            // A configured id that simply doesn't exist for this account (and whose provider
            // answered fine) is dropped rather than shown as a permanent "?".
            if (reading is null && error is null)
            {
                RemoveIcon(id);
                continue;
            }

            wanted.Add(id);
            var icon = _icons.TryGetValue(id, out var existing) ? existing : CreateIcon(id);

            var dark = TrayIconRenderer.UsesDarkTaskbar(_config.Theme);
            var color = reading is null
                ? Harmony.Legible(_palette.Unknown, dark)
                : _palette.ForReading(reading.Group, reading.Percent, reading.Variant, reading.VariantCount, dark);

            var previous = icon.Icon;
            icon.Icon = TrayIconRenderer.Render(reading?.IconText ?? "?", color, _config.FontFamily);
            previous?.Dispose();

            // With the hover card active the native tooltip must stay empty, or Windows
            // would show its own text balloon alongside it.
            icon.Text = _config.RichTooltips ? string.Empty : Tooltip(id, reading, error);
            icon.Visible = true;
        }

        foreach (var stale in _icons.Keys.Except(wanted, StringComparer.OrdinalIgnoreCase).ToList())
            RemoveIcon(stale);

        SyncNeutralIcon();  // Fork: requisito 5 — sem ícone de serviço e sem faixa, mostra ícone neutro
    }

    /// <summary>
    /// Fork: requisito 5 — se não houver nenhum ícone de serviço na bandeja e a faixa estiver
    /// desligada, mostra um único ícone neutro do app para que o menu continue acessível. Se
    /// houver ícone de serviço ou a faixa estiver ligada, o neutro desaparece.
    /// </summary>
    private void SyncNeutralIcon()
    {
        var temIconeServico = _icons.Values.Any(i => i.Visible);
        var faixaLigada = _config.Widget.Enabled;

        if (!temIconeServico && !faixaLigada)
        {
            if (_neutralIcon is null)
            {
                _neutralIcon = new NotifyIcon
                {
                    Icon = AppIcon.Value ?? SystemIcons.Application,
                    Text = "Gaugely",
                    ContextMenuStrip = _menu,
                    Visible = true,
                };
                _neutralIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleDetails(); };
            }
            else
            {
                _neutralIcon.Visible = true;
            }
        }
        else if (_neutralIcon is not null)
        {
            _neutralIcon.Visible = false;
            _neutralIcon.Dispose();
            _neutralIcon = null;
        }
    }

    private TrayIcon CreateIcon(string id)
    {
        var icon = new TrayIcon(TrayIcon.GuidFor(id), PrettyLabel(id), nativeTooltip: !_config.RichTooltips)
        {
            ContextMenuStrip = _menu,
        };

        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleDetails(); };
        icon.MouseMove += (_, _) => ShowTooltip(id);
        _icons[id] = icon;
        return icon;
    }

    /// <summary>
    /// Human-readable, stable name for a limit id ("claude.weekly_scoped.fable" -> "Claude · Weekly
    /// scoped · Fable"), used to name the Windows settings entry. Derived from the id, not the
    /// provider label: the label is empty until the first live poll, and the settings name freezes
    /// when the icon is first added — usually from cache, before any poll has run.
    /// </summary>
    private static string PrettyLabel(string id) =>
        string.Join(" · ", id.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var text = part.Replace('_', ' ').Trim();
            return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
        }));

    private void RemoveIcon(string id)
    {
        if (!_icons.Remove(id, out var icon)) return;

        icon.Visible = false;
        var image = icon.Icon;
        icon.Dispose();
        image?.Dispose();
    }

    /// <summary>
    /// Fallback for <c>richTooltips: false</c>. NotifyIcon.Text is capped at 63 characters by
    /// WinForms, so this carries only value and reset.
    /// </summary>
    internal static string Tooltip(string id, LimitReading? reading, string? error)
    {
        if (reading is null) return Clamp($"{id}\n{error ?? "?"}");

        // Fork: usar LimitReading.FormatValue para lidar com leituras informativas corretamente
        var line = $"{reading.Label}: {LimitReading.FormatValue(reading)}";
        return reading.ResetsAt is null ? Clamp(line) : Clamp($"{line}\n{reading.ResetText()}");
    }

    internal static string Clamp(string text) => text.Length <= 63 ? text : text[..62] + "…";

    private string? ErrorForId(string id)
    {
        var group = ServiceGroup.IsGroupId(id) ? ServiceGroup.GroupOfId(id) : GroupOf(id);
        return _lastResults.FirstOrDefault(r => r.Group == group)?.Error;
    }

    /// <summary>
    /// Fork: o grupo sai do prefixo do id. Antes só havia dois serviços e tudo que não era
    /// "codex." caía em Claude — com Kimi e OpenRouter isso atribuiria o erro de um ao outro.
    /// Prefixo desconhecido continua indo para Claude, como no upstream.
    /// </summary>
    internal static string GroupOf(string id) => ServiceCatalog.GetByPrefix(id).Group;

    // --------------------------------------------------------------- tooltips

    private void ShowTooltip(string id)
    {
        var pos = Cursor.Position;
        _lastHover = DateTime.UtcNow;
        _lastHoverPos = pos;
        if (!_config.RichTooltips) return;

        // The pinned details window is the rich view; a hover card would both cover it and, by
        // pulling the foreground off it, dismiss it on the very next mouse move (issue #5). The
        // context menu likewise owns the screen while it is open.
        if (_details is { Visible: true } || _menu.Visible) return;

        _tooltipTimer.Start();

        // MouseMove fires repeatedly while the pointer rests on one icon. Re-showing the card on
        // every message repositioned and repainted it constantly; only move it when the pointer
        // actually crosses to a different icon.
        if (id == _hoveredId && _tooltip is { Visible: true }) return;

        _hoveredId = id;
        _tooltipOwner = TooltipOwner.Tray;  // Fork: marca que o cartão foi aberto pela bandeja
        _tooltip ??= new TooltipWindow(_config, _palette);

        if (ServiceGroup.IsGroupId(id))
        {
            var group = ServiceGroup.GroupOfId(id);
            _tooltip.ShowForGroup(TrayReadingsOf(group), group, ErrorForId(id), pos);
            return;
        }

        _tooltip.ShowFor(_lastReadings.GetValueOrDefault(id), GroupOf(id), ErrorForId(id), pos);
    }

    /// <summary>
    /// The shell reports no "mouse left the icon" event, so the card is dismissed once the
    /// stream of MouseMove notifications stops.
    /// </summary>
    private void HideTooltipWhenIdle()
    {
        if (_tooltip is not { Visible: true }) { _tooltipTimer.Stop(); return; }

        var pos = Cursor.Position;

        // Fork: o cartão pertence à faixa — o evento HoverChanged da faixa também cuida dele,
        // mas aqui garantimos que ele esconda se o mouse sair da faixa e do cartão
        if (_tooltipOwner == TooltipOwner.Widget)
        {
            if (_widget is not { Visible: true } || (!_widget.Bounds.Contains(pos) && !_tooltip.Bounds.Contains(pos)))
            {
                _tooltipTimer.Stop();
                HideTooltip();
            }
            return;
        }

        // The shell stops sending MouseMove once the pointer is still, so idle time alone would
        // dismiss a card the user is actively hovering — very visible in the overflow flyout, where
        // MouseMove is sparse. Treat "pointer hasn't moved" as "still hovering" and keep the card
        // up; only once it has clearly moved away, with no MouseMove to refresh us, does it hide.
        if (Math.Abs(pos.X - _lastHoverPos.X) <= HoverSlack && Math.Abs(pos.Y - _lastHoverPos.Y) <= HoverSlack)
        {
            _lastHover = DateTime.UtcNow;
            return;
        }

        if ((DateTime.UtcNow - _lastHover).TotalMilliseconds < TooltipIdleMs) return;

        _tooltipTimer.Stop();
        HideTooltip();
    }

    /// <summary>
    /// Hides the hover card and forgets which icon it was showing, so the next hover re-shows it
    /// rather than treating the pointer as still parked on the icon it last drew for.
    /// </summary>
    private void HideTooltip()
    {
        _tooltip?.Hide();
        _hoveredId = null;
        _tooltipOwner = TooltipOwner.None;  // Fork: libera a posse do cartão
    }

    // ---------------------------------------------------------- notifications

    private void RaiseNotifications()
    {
        if (!_config.Notifications.Enabled) return;

        var anchor = _icons.Values.FirstOrDefault(i => i.Visible);
        if (anchor is null) return;

        foreach (var reading in _lastReadings.Values)
        {
            // Keying on the reset timestamp makes the warning repeat in the next window.
            var key = $"{reading.Id}@{reading.ResetsAt?.ToUnixTimeSeconds() ?? 0}";

            if (reading.Percent < _config.Notifications.AtPercent)
            {
                _notified.Remove(key);
                continue;
            }

            if (!_notified.Add(key)) continue;

            anchor.BalloonTipTitle = Loc.T("toast.title", reading.Group, Math.Round(reading.Percent));
            anchor.BalloonTipText = $"{reading.Label}\n{reading.ResetText()}";
            anchor.BalloonTipIcon = reading.Percent >= _config.Thresholds.Critical
                ? ToolTipIcon.Error
                : ToolTipIcon.Warning;
            anchor.ShowBalloonTip(10_000);
        }
    }

    // ------------------------------------------------------------------- menu

    private void BuildMenu()
    {
        _menu.Items.Clear();

        _menu.Items.Add(new ToolStripMenuItem(Loc.T("menu.details"), null, (_, _) => ToggleDetails())
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        });
        _menu.Items.Add(new ToolStripMenuItem(Loc.T("menu.refresh"), null, (_, _) => _ = RefreshAsync(force: true)));

        // Fork: a faixa flutuante. Fica junto do painel porque as duas mostram a mesma coisa —
        // uma sob pedido, a outra o tempo todo.
        var widget = new ToolStripMenuItem(Loc.T("menu.widget"))
        {
            Checked = _config.Widget.Enabled,
            CheckOnClick = true,
        };
        widget.Click += (_, _) => SetWidget(widget.Checked);
        _menu.Items.Add(widget);

        // Fork: modo de apresentação (Flutuante / Notch)
        var modoMenu = new ToolStripMenuItem(Loc.T("menu.widget.modo"));
        var modoFlutuante = new ToolStripMenuItem(Loc.T("menu.widget.modo.flutuante"))
        {
            Checked = _config.Widget.Modo == ModoApresentacao.Flutuante,
        };
        modoFlutuante.Click += (_, _) =>
        {
            _config.Widget.Modo = ModoApresentacao.Flutuante;
            ConfigStore.Save(_config);
            _widget?.Relayout();
            _widget?.Invalidate();
            BuildMenu();  // Atualiza marcações
        };
        var modoNotch = new ToolStripMenuItem(Loc.T("menu.widget.modo.notch"))
        {
            Checked = _config.Widget.Modo == ModoApresentacao.Notch,
        };
        modoNotch.Click += (_, _) =>
        {
            _config.Widget.Modo = ModoApresentacao.Notch;
            ConfigStore.Save(_config);
            _widget?.Relayout();
            _widget?.Invalidate();
            BuildMenu();
        };
        modoMenu.DropDownItems.AddRange([modoFlutuante, modoNotch]);
        _menu.Items.Add(modoMenu);

        // Fork: borda (só visível no modo Notch, mas sempre construído para simplificar)
        var bordaMenu = new ToolStripMenuItem(Loc.T("menu.widget.borda"));
        foreach (var b in new[] {
            (Label: Loc.T("menu.widget.borda.esquerda"), Value: BordaTela.Esquerda),
            (Label: Loc.T("menu.widget.borda.direita"), Value: BordaTela.Direita),
            (Label: Loc.T("menu.widget.borda.topo"), Value: BordaTela.Topo),
            (Label: Loc.T("menu.widget.borda.base"), Value: BordaTela.Base),
        })
        {
            var bItem = new ToolStripMenuItem(b.Label)
            {
                Checked = _config.Widget.Borda == b.Value,
            };
            bItem.Click += (_, _) =>
            {
                _config.Widget.Borda = b.Value;
                ConfigStore.Save(_config);
                _widget?.Relayout();
                _widget?.Invalidate();
                foreach (ToolStripMenuItem irmao in bordaMenu.DropDownItems) irmao.Checked = _config.Widget.Borda == (BordaTela)irmao.Tag!;
            };
            bItem.Tag = b.Value;
            bordaMenu.DropDownItems.Add(bItem);
        }
        _menu.Items.Add(bordaMenu);

        // Fork: "Horizontal" no modo Flutuante (mantém compatibilidade)
        if (_config.Widget.Modo == ModoApresentacao.Flutuante)
        {
            var horizontal = new ToolStripMenuItem(Loc.T("menu.widget.horizontal"))
            {
                Checked = _config.Widget.Orientation == "horizontal",
                CheckOnClick = true,
            };
            horizontal.Click += (_, _) =>
            {
                _config.Widget.Orientation = horizontal.Checked ? "horizontal" : "vertical";
                ConfigStore.Save(_config);
                _widget?.Relayout();
                _widget?.Invalidate();
            };
            _menu.Items.Add(horizontal);
        }

        // Fork: alça de arrasto
        var alca = new ToolStripMenuItem(Loc.T("menu.widget.alca"))
        {
            Checked = _config.Widget.MostrarAlca,
            CheckOnClick = true,
        };
        alca.Click += (_, _) =>
        {
            _config.Widget.MostrarAlca = alca.Checked;
            ConfigStore.Save(_config);
            _widget?.Invalidate();
        };
        _menu.Items.Add(alca);

        // Fork: recentralizar na borda atual
        var recentralizar = new ToolStripMenuItem(Loc.T("menu.widget.recentralizar"));
        recentralizar.Click += (_, _) => _widget?.Recentralizar();
        _menu.Items.Add(recentralizar);

        var sizeMenu = new ToolStripMenuItem(Loc.T("menu.widget.size"));
        var sizes = new[]
        {
            (Name: Loc.T("menu.widget.size.small"), Value: 0.8),
            (Name: Loc.T("menu.widget.size.normal"), Value: 1.0),
            (Name: Loc.T("menu.widget.size.large"), Value: 1.3),
            (Name: Loc.T("menu.widget.size.xlarge"), Value: 1.6)
        };
        foreach (var s in sizes)
        {
            var sItem = new ToolStripMenuItem(s.Name)
            {
                Checked = Math.Abs(_config.Widget.Scale - s.Value) < 0.01,
                CheckOnClick = true
            };
            sItem.Click += (_, _) =>
            {
                // Fork: marca só o tamanho escolhido; CheckOnClick sozinho deixava vários marcados.
                foreach (ToolStripMenuItem irmao in sizeMenu.DropDownItems) irmao.Checked = irmao == sItem;
                _config.Widget.Scale = s.Value;
                ConfigStore.Save(_config);
                _widget?.Relayout();
                _widget?.Invalidate();
            };
            sizeMenu.DropDownItems.Add(sItem);
        }
        _menu.Items.Add(sizeMenu);

        var agrupar = new ToolStripMenuItem(Loc.T("menu.groupByService"))
        {
            Checked = _config.GroupByService,
            CheckOnClick = true,
        };
        agrupar.Click += (_, _) =>
        {
            _config.GroupByService = agrupar.Checked;
            ConfigStore.Save(_config);
            HideTooltip();
            SyncIcons();
        };
        _menu.Items.Add(agrupar);

        _menu.Items.Add(new ToolStripSeparator());

        _iconsMenu = new ToolStripMenuItem(Loc.T("menu.icons"));
        _menu.Items.Add(_iconsMenu);

        // Fork: submenu separado para escolher quais serviços aparecem na faixa flutuante
        _widgetIconsMenu = new ToolStripMenuItem(Loc.T("menu.widgetItems"));
        _menu.Items.Add(_widgetIconsMenu);

        _languageMenu = new ToolStripMenuItem(Loc.T("menu.language"));
        _menu.Items.Add(_languageMenu);
        BuildLanguageMenu();

        _menu.Items.Add(new ToolStripSeparator());

        var autostart = new ToolStripMenuItem(Loc.T("menu.autostart")) { Checked = AutoStart.IsEnabled, CheckOnClick = true };
        autostart.Click += (_, _) =>
        {
            if (AutoStart.TrySet(autostart.Checked, out var error)) return;

            autostart.Checked = AutoStart.IsEnabled;
            MessageBox.Show(Loc.T("dialog.autostartFailed", error), "Gaugely",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        _menu.Items.Add(autostart);

        var notify = new ToolStripMenuItem(Loc.T("menu.notifyAt", _config.Notifications.AtPercent))
        {
            Checked = _config.Notifications.Enabled,
            CheckOnClick = true,
        };
        notify.Click += (_, _) =>
        {
            _config.Notifications.Enabled = notify.Checked;
            ConfigStore.Save(_config);
        };
        _menu.Items.Add(notify);

        _menu.Items.Add(new ToolStripMenuItem(Loc.T("menu.settings"), null, (_, _) => OpenSettings()));
        _aboutMenu = new ToolStripMenuItem(Loc.T("menu.about"), null, (_, _) => OpenAbout());
        _menu.Items.Add(_aboutMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(Loc.T("menu.quit"), null, (_, _) => Quit()));

        UpdateAboutMarker();
        RefreshIconsMenu();
    }

    private void BuildLanguageMenu()
    {
        _languageMenu.DropDownItems.Clear();

        var auto = new ToolStripMenuItem(Loc.T("menu.languageAuto", Loc.DisplayName(Loc.SystemLanguage)))
        {
            Checked = _config.Language.Equals("auto", StringComparison.OrdinalIgnoreCase),
        };
        auto.Click += (_, _) => ApplyLanguage("auto");
        _languageMenu.DropDownItems.Add(auto);
        _languageMenu.DropDownItems.Add(new ToolStripSeparator());

        foreach (var code in Loc.Available)
        {
            var item = new ToolStripMenuItem(Loc.DisplayName(code))
            {
                Checked = _config.Language.Equals(code, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => ApplyLanguage(code);
            _languageMenu.DropDownItems.Add(item);
        }
    }

    private void ApplyLanguage(string language)
    {
        _config.Language = language;
        ConfigStore.Save(_config);
        Loc.Use(language);

        BuildMenu();

        // Labels are produced by the providers, so they only pick up the new language on the
        // next poll; the details window is rebuilt from that result.
        HideTooltip();
        _ = RefreshAsync();
    }

    /// <summary>
    /// Lists every limit discovered on this account plus anything still referenced by the
    /// config, so an id can always be unchecked again even after it stopped being reported.
    /// Fork: popula os dois submenus (Icons e Widget items) de uma só vez.
    /// </summary>
    private void RefreshIconsMenu()
    {
        _iconsMenu.DropDownItems.Clear();
        _widgetIconsMenu.DropDownItems.Clear();

        var known = _lastReadings.Values
            .Select(r => (r.Id, r.Label, r.Group))
            .Concat(_config.Icons
                .Where(id => !_lastReadings.ContainsKey(id))
                .Select(id => (Id: id, Label: Loc.T("menu.notReported", id), Group: "")))
            // Fork: inclui ids que estão só em WidgetIcons (desmarcados em Icons mas marcados na faixa)
            .Concat((_config.WidgetIcons ?? [])
                .Where(id => !_lastReadings.ContainsKey(id) && !_config.Icons.Contains(id, StringComparer.OrdinalIgnoreCase))
                .Select(id => (Id: id, Label: Loc.T("menu.notReported", id), Group: "")))
            .DistinctBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (known.Count == 0)
        {
            _iconsMenu.DropDownItems.Add(new ToolStripMenuItem(Loc.T("menu.noData")) { Enabled = false });
            _widgetIconsMenu.DropDownItems.Add(new ToolStripMenuItem(Loc.T("menu.noData")) { Enabled = false });
            return;
        }

        // Fork: a lista efetiva para a faixa — WidgetIcons se existir, senão Icons
        var widgetList = _config.WidgetIcons ?? _config.Icons;

        string? lastGroupIcons = null;
        string? lastGroupWidget = null;
        foreach (var entry in known.OrderBy(e => e.Group, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            // --- submenu Icons (bandeja) ---
            if (entry.Group != lastGroupIcons && lastGroupIcons is not null)
                _iconsMenu.DropDownItems.Add(new ToolStripSeparator());
            lastGroupIcons = entry.Group;

            var item = new ToolStripMenuItem(entry.Label)
            {
                Checked = _config.Icons.Contains(entry.Id, StringComparer.OrdinalIgnoreCase),
                CheckOnClick = true,
            };
            item.Click += (_, _) => ToggleIcon(entry.Id, item.Checked);
            _iconsMenu.DropDownItems.Add(item);

            // --- submenu Widget items (faixa) ---
            if (entry.Group != lastGroupWidget && lastGroupWidget is not null)
                _widgetIconsMenu.DropDownItems.Add(new ToolStripSeparator());
            lastGroupWidget = entry.Group;

            var wItem = new ToolStripMenuItem(entry.Label)
            {
                Checked = widgetList.Contains(entry.Id, StringComparer.OrdinalIgnoreCase),
                CheckOnClick = true,
            };
            wItem.Click += (_, _) => ToggleWidgetIcon(entry.Id, wItem.Checked);
            _widgetIconsMenu.DropDownItems.Add(wItem);
        }
    }

    private void ToggleIcon(string id, bool enabled)
    {
        if (enabled)
        {
            if (!_config.Icons.Contains(id, StringComparer.OrdinalIgnoreCase)) _config.Icons.Add(id);
        }
        else
        {
            _config.Icons.RemoveAll(existing => existing.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        ConfigStore.Save(_config);
        SyncIcons();
        SyncNeutralIcon();  // Fork: pode ter ficado sem ícone de serviço
    }

    /// <summary>
    /// Fork: alterna uma janela em WidgetIcons. Materializa a lista a partir de Icons se for nula,
    /// salva e atualiza a faixa.
    /// </summary>
    private void ToggleWidgetIcon(string id, bool enabled)
    {
        // Fork: materializa na primeira interação — a faixa começa idêntica à bandeja
        _config.WidgetIcons ??= new List<string>(_config.Icons);

        if (enabled)
        {
            if (!_config.WidgetIcons.Contains(id, StringComparer.OrdinalIgnoreCase))
                _config.WidgetIcons.Add(id);
        }
        else
        {
            _config.WidgetIcons.RemoveAll(existing => existing.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        ConfigStore.Save(_config);
        _widget?.Apply(WidgetGroups());
    }

    private void OpenSettings()
    {
        if (BringOpenDialogToFront()) return;

        using var form = new Ui.Settings.SettingsWindow(_config, _lastReadings.Values.ToList(), _lastResults, SetLatestUpdate);
        _dialog = form;
        DialogResult result;
        try { result = form.ShowDialog(); }
        finally { _dialog = null; }
        if (result == DialogResult.OK) ApplyConfigChanges();
        ReloadIfPending();
    }

    /// <summary>
    /// Fork: aplica uma configuração que mudou por inteiro — pela janela de ajustes ou por edição
    /// do settings.json. Os valores já estão dentro de <see cref="_config"/>; aqui só se refaz o
    /// que guarda cópia deles (idioma, relógio, janelas com fonte e cor em cache, faixa, menu).
    /// </summary>
    private void ApplyConfigChanges()
    {
        Loc.Use(_config.Language);
        _timer.Interval = PollIntervalMs;
        ApplyPollSpacing();
        ScheduleNextPoll();

        // Both windows cache fonts and colours at construction, so they are rebuilt rather
        // than patched after a settings change.
        _tooltip?.Dispose();
        _tooltip = null;
        _widgetTip?.Dispose();               // Fork: refaz junto com o cartão rico
        _widgetTip = null;
        _details?.Dispose();
        _details = null;

        // Fork: a faixa também é ajustada pela configuração — liga, desliga ou se redesenha.
        SetWidget(_config.Widget.Enabled, save: false);
        _widget?.Relayout();
        _widget?.Invalidate();
        SyncIcons();

        BuildMenu();
        _ = RefreshAsync();
    }

    // ------------------------------------------------------------ settings.json

    /// <summary>
    /// Fork: quem prefere editar o settings.json vê a mudança valer sem reiniciar o app. O
    /// observador só avisa; a leitura espera o arquivo parar de mudar (editores gravam em mais de
    /// um passo) e ignora a gravação do próprio app.
    /// </summary>
    private void WatchConfigFile()
    {
        var ui = SynchronizationContext.Current;
        ConfigStore.SaveBlocked += () => ui?.Post(_ => NotifySaveBlocked(), null);
        _reloadTimer.Tick += (_, _) => { _reloadTimer.Stop(); ReloadConfigFromDisk(); };

        try
        {
            System.IO.Directory.CreateDirectory(ConfigStore.Directory);
            _configWatcher = new FileSystemWatcher(ConfigStore.Directory, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };

            void Changed(object? sender, EventArgs e) => ui?.Post(_ => { _reloadTimer.Stop(); _reloadTimer.Start(); }, null);
            _configWatcher.Changed += Changed;
            _configWatcher.Created += Changed;
            _configWatcher.Renamed += Changed;
            _configWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _configWatcher?.Dispose();
            _configWatcher = null;              // sem recarga automática; a janela de ajustes continua valendo
        }
    }

    private DateTime _saveBlockedShown = DateTime.MinValue;

    /// <summary>Um aviso por rajada: a mesma edição pendente recusa várias gravações seguidas.</summary>
    private void NotifySaveBlocked()
    {
        if (DateTime.UtcNow - _saveBlockedShown < TimeSpan.FromSeconds(30)) return;
        _saveBlockedShown = DateTime.UtcNow;

        var text = Loc.T("notice.saveBlocked");
        if (_icons.Values.FirstOrDefault(i => i.Visible) is { } anchor)
        {
            anchor.BalloonTipTitle = "Gaugely";
            anchor.BalloonTipText = text;
            anchor.BalloonTipIcon = ToolTipIcon.Warning;
            anchor.ShowBalloonTip(10_000);
        }
        else if (_neutralIcon is { Visible: true } neutral)
        {
            neutral.ShowBalloonTip(10_000, "Gaugely", text, ToolTipIcon.Warning);
        }
    }

    private void ReloadConfigFromDisk()
    {
        // Com uma janela aberta, a recarga espera: a janela de ajustes tem a sua própria cópia e
        // salvar nela gravaria por cima do que acabou de chegar do arquivo.
        if (_dialog is not null) { _reloadPending = true; return; }

        var edited = ConfigStore.ReadExternalEdit(out var busy);
        if (busy && ++_reloadRetries <= 5) { _reloadTimer.Start(); return; }
        _reloadRetries = 0;
        if (edited is null) return;

        ConfigStore.CopyInto(edited, _config);
        ApplyConfigChanges();
    }

    private void ReloadIfPending()
    {
        if (!_reloadPending) return;
        _reloadPending = false;
        ReloadConfigFromDisk();
    }

    // ------------------------------------------------------------------ about

    private void OpenAbout()
    {
        if (BringOpenDialogToFront()) return;

        using var about = new AboutForm(_config, _latestUpdate, result => { if (result is not null) SetLatestUpdate(result); });
        _dialog = about;
        try { about.ShowDialog(); }
        finally { _dialog = null; }
        ReloadIfPending();
    }

    /// <summary>
    /// Keeps a single modal window (About or Settings) at a time: if one is already open, brings it
    /// to the front instead of stacking a second dialog — or a second copy of the same one — on top.
    /// The tray menu stays clickable while a dialog is up, so without this the user could do either.
    /// </summary>
    private bool BringOpenDialogToFront()
    {
        HideTooltip();
        if (_dialog is not { IsDisposed: false }) return false;

        if (_dialog.WindowState == FormWindowState.Minimized) _dialog.WindowState = FormWindowState.Normal;
        _dialog.Activate();
        return true;
    }

    /// <summary>
    /// Fires the daily start-up update check, unless it is switched off or has already run in the
    /// last 24 hours. The result only marks the About entry — nothing interrupts the user.
    /// </summary>
    private void MaybeCheckForUpdates()
    {
        if (!_config.AutoUpdateCheck) return;
        if (_config.LastUpdateCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(24)) return;

        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        var result = await UpdateCheck.LatestAsync(AppInfo.SemVer).ConfigureAwait(true);

        _config.LastUpdateCheck = DateTimeOffset.Now;
        ConfigStore.Save(_config);
        if (result is null) return;

        SetLatestUpdate(result);
        NotifyUpdate(result);
    }

    /// <summary>
    /// Fork: avisa que há versão nova e para aí. Instalar é sempre um clique no Sobre — o hash da
    /// release prova integridade no caminho, não quem publicou, então a decisão fica com a pessoa.
    /// </summary>
    private void NotifyUpdate(UpdateCheck.Result result)
    {
        if (!result.IsNewer) return;

        var anchor = _icons.Values.FirstOrDefault(i => i.Visible);
        if (anchor is null) return;

        anchor.BalloonTipTitle = Loc.T("about.title");
        anchor.BalloonTipText = Loc.T("about.updateNotice", result.Latest.ToString(3));
        anchor.BalloonTipIcon = ToolTipIcon.Info;
        anchor.ShowBalloonTip(10_000);
    }

    private void SetLatestUpdate(UpdateCheck.Result result)
    {
        _latestUpdate = result;
        UpdateAboutMarker();
    }

    /// <summary>Appends a dot to the About entry while a newer version is known.</summary>
    private void UpdateAboutMarker() =>
        _aboutMenu.Text = _latestUpdate is { IsNewer: true } ? Loc.T("menu.about") + "  •" : Loc.T("menu.about");

    private void ToggleDetails()
    {
        HideTooltip();
        _details ??= new DetailsForm(_config, _palette);

        if (_details.Visible)
        {
            _details.Hide();
            return;
        }

        // Clicking a tray icon deactivates the details window first, so it may have auto-hidden
        // itself a few milliseconds ago — that same click therefore means "close", not "reopen".
        if ((DateTime.UtcNow - _details.LastAutoHidden).TotalMilliseconds < 250) return;

        _details.ShowNearTray(_lastResults, _lastUpdate, _nextPoll);
    }

    // --------------------------------------------------------------- shutdown

    /// <summary>
    /// Fork: liga ou desliga a faixa flutuante e guarda a escolha, para ela voltar sozinha no
    /// próximo início. O clique nela abre o mesmo painel do ícone, e o botão direito abre o
    /// mesmo menu — uma janela sem nenhuma dessas duas saídas viraria um enfeite preso na tela.
    /// </summary>
    private void SetWidget(bool ligada, bool save = true)
    {
        _config.Widget.Enabled = ligada;
        if (save) ConfigStore.Save(_config);

        if (!ligada)
        {
            HideTooltip();                         // Fork: esconde o cartão se a faixa sumiu
            _widgetTip?.Dispose();
            _widgetTip = null;
            _widget?.Close();
            _widget?.Dispose();
            _widget = null;
            SyncNeutralIcon();                     // Fork: sem faixa, pode precisar do ícone neutro
            return;
        }

        if (_widget is null)
        {
            _widget = new WidgetForm(_config, _palette) { ContextMenuStrip = _menu };
            _widget.Clicked += (_, _) => { HideTooltip(); ToggleDetails(); };   // Fork: clique = panorama, sem o cartão individual por cima
            _widget.HoverChanged += OnWidgetHover;  // Fork: cartão rico ao passar o mouse
            _widget.LayoutChanged += (_, _) =>
            {
                // Fork: debounce de 500 ms no salvamento para não sobrecarregar o disco
                _layoutSaveTimer.Stop();
                _layoutSaveTimer.Start();
            };
        }

        _widget.Apply(WidgetGroups());
        _widget.Show();
        SyncNeutralIcon();                         // Fork: faixa ligada, ícone neutro sai
    }

    /// <summary>
    /// Fork: reage ao hover na faixa flutuante. Com RichTooltips ligado, mostra o cartão rico
    /// ao lado da faixa; com ele desligado, usa uma ToolTip nativa simples. A posição do cartão
    /// usa a sobrecarga que recebe o retângulo de ancoragem.
    /// </summary>
    private void OnWidgetHover(object? sender, WidgetHoverEventArgs e)
    {
        if (e.Group is null)
        {
            // Fork: saiu do anel — esconde o cartão (rico ou simples).
            if (_tooltipOwner == TooltipOwner.Widget) HideTooltip();
            _widgetTip?.Hide(_widget!);
            return;
        }

        // Fork: o painel de detalhes ou o menu estão abertos — não sobrepor.
        if (_details is { Visible: true } || _menu.Visible) return;

        var rows = WidgetReadingsOf(e.Group);
        var error = _lastResults.FirstOrDefault(r => r.Group == e.Group)?.Error;

        if (!_config.RichTooltips)
        {
            // Fork: fallback — tooltip nativa simples, como antes do cartão rico.
            _widgetTip ??= new ToolTip { InitialDelay = 250, ReshowDelay = 120 };
            var grupo = new WidgetGroup(e.Group, rows, error);
            _widgetTip.SetToolTip(_widget!, WidgetForm.TipFor(grupo));
            return;
        }

        _tooltip ??= new TooltipWindow(_config, _palette);
        _tooltipOwner = TooltipOwner.Widget;

        // Fork: usa o retângulo da faixa inteira para decidir o lado, e o do anel para alinhar.
        _tooltip.ShowForGroup(rows, e.Group, error, e.AnchorScreenRect, _widget!.Bounds);
        _tooltipTimer.Start();   // Fork: arma o vigia; sem isso, sem MouseLeave o cartão ficava preso
    }

    /// <summary>Fork: os serviços da faixa, pelo filtro de WidgetIcons (ou Icons se nulo).</summary>
    private IReadOnlyList<WidgetGroup> WidgetGroups() =>
        _lastResults
            .Select(r => new WidgetGroup(r.Group, WidgetReadingsOf(r.Group), r.Error))
            .Where(g => g.Rows.Count > 0 || g.Error is not null)
            .ToList();

    private void Quit()
    {
        _timer.Stop();
        _tooltipTimer.Stop();
        if (_layoutSaveTimer.Enabled) ConfigStore.Save(_config);   // Fork: Ctrl+roda nos últimos 500 ms não se perde
        _layoutSaveTimer.Stop();
        _shutdown.Cancel();

        foreach (var id in _icons.Keys.ToList()) RemoveIcon(id);

        _tooltip?.Dispose();
        _widgetTip?.Dispose();               // Fork: tooltip simples de fallback da faixa
        if (_neutralIcon is not null) { _neutralIcon.Visible = false; _neutralIcon.Dispose(); _neutralIcon = null; }
        _details?.Dispose();
        _widget?.Dispose();
        _dialog?.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tooltipTimer.Dispose();
            _layoutSaveTimer.Dispose();
            _reloadTimer.Dispose();
            _configWatcher?.Dispose();
            _menu.Dispose();
            _shutdown.Dispose();
        }

        base.Dispose(disposing);
    }
}
