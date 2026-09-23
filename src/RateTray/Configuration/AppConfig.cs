namespace RateTray.Configuration;

public sealed class AppConfig
{
    internal const string ThemeDefault = "auto";
    internal const string LanguageDefault = "auto";
    internal const string FontDefault = "Segoe UI";

    /// <summary>
    /// Poll interval. The windows being watched move in hours and days, so anything under a
    /// minute buys nothing — and the Claude usage endpoint rate-limits, which a tight loop
    /// will eventually run into.
    /// </summary>
    public int RefreshSeconds { get; set; } = 90;

    /// <summary>auto | light | dark — decides the tray icon's base colour.</summary>
    public string Theme { get; set; } = ThemeDefault;

    /// <summary>auto | en | de — "auto" follows the Windows UI language.</summary>
    public string Language { get; set; } = LanguageDefault;

    public string FontFamily { get; set; } = FontDefault;

    /// <summary>
    /// Own hover card with the service mark instead of the native text tooltip. Turn off to
    /// fall back to the plain Windows tooltip (text only, capped at 63 characters).
    /// </summary>
    public bool RichTooltips { get; set; } = true;

    public ColorOptions Colors { get; set; } = new();

    /// <summary>
    /// Ordered list of <see cref="Model.LimitReading.Id"/> values to show as tray icons.
    /// Unknown ids are ignored, so a stale config never breaks startup.
    /// </summary>
    public List<string> Icons { get; set; } = [];

    /// <summary>
    /// Fork: lista separada de janelas visíveis na faixa flutuante. Nulo enquanto a configuração
    /// ainda não foi migrada — nesse caso a faixa usa <see cref="Icons"/>. Materializada na
    /// primeira interação com o submenu "Widget items" ou na migração automática.
    /// </summary>
    public List<string>? WidgetIcons { get; set; }

    /// <summary>
    /// False until the first successful poll has filled <see cref="Icons"/> with everything
    /// the account actually reports — which windows exist differs per plan (per-model limits
    /// such as Fable only appear for some), so a hardcoded default list would silently miss them.
    /// Set to false again to re-discover.
    /// </summary>
    public bool IconsInitialized { get; set; }

    /// <summary>
    /// Fork: toda janela que o app já viu, marcada ou não em <see cref="Icons"/>. Janela conhecida
    /// e fora de Icons foi desmarcada de propósito e continua escondida; janela inédita (serviço que
    /// ganhou chave depois, ou limite novo de um provedor) entra marcada uma única vez.
    /// </summary>
    public List<string> KnownReadingIds { get; set; } = [];

    /// <summary>
    /// Fork: serviços já examinados. A migração é por serviço, não global: um serviço que estava
    /// fora do ar na primeira execução após atualizar é examinado quando voltar, sem reativar o que
    /// estava desmarcado.
    /// </summary>
    public List<string> KnownGroups { get; set; } = [];

    /// <summary>
    /// Longest a failing provider is left alone before the next attempt. The wait doubles with
    /// each consecutive failure until it reaches this ceiling — low enough that a service which
    /// recovers overnight is picked up again, high enough not to pester a rate-limited endpoint.
    /// </summary>
    public int MaxBackoffMinutes { get; set; } = 15;

    /// <summary>
    /// Off by default: RateTray makes no network call you did not ask for. Turn it on — in the About
    /// dialog — to check GitHub for a newer release on start-up, at most once a day. The manual
    /// "check for updates" button in that dialog works either way.
    /// </summary>
    public bool AutoUpdateCheck { get; set; } = false;

    /// <summary>When the automatic check last ran, so it is not repeated on every launch. Null
    /// until the first check.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }


    public ThresholdOptions Thresholds { get; set; } = new();
    public NotificationOptions Notifications { get; set; } = new();
    public ClaudeOptions Claude { get; set; } = new();
    public CodexOptions Codex { get; set; } = new();

    /// <summary>Fork: cota do Kimi Code. A chave mora no Gerenciador de Credenciais, não aqui.</summary>
    public KimiOptions Kimi { get; set; } = new();

    /// <summary>Fork: cota do Google AI Pro lida pelo agy local. Sem caminho configurável.</summary>
    public AntigravityOptions Antigravity { get; set; } = new();

    /// <summary>Fork: saldo e gasto do OpenRouter. A chave mora no Gerenciador de Credenciais, não aqui.</summary>
    public OpenRouterOptions OpenRouter { get; set; } = new();

    /// <summary>Fork: gasto e uso da API da OpenAI (chave Admin). Trilho de API do fornecedor OpenAI.</summary>
    public ApiProviderOptions OpenAIApi { get; set; } = new();

    /// <summary>Fork: gasto e uso da API da Anthropic (chave Admin). Trilho de API do fornecedor Claude.</summary>
    public ApiProviderOptions AnthropicApi { get; set; } = new();

    /// <summary>Fork: saldo da plataforma Kimi (chave da plataforma, não a do Kimi Code).</summary>
    public ApiProviderOptions KimiApi { get; set; } = new() { MinIntervalSeconds = 300 };

    /// <summary>Fork: saldo da API da DeepSeek.</summary>
    public ApiProviderOptions DeepSeek { get; set; } = new() { MinIntervalSeconds = 300 };

    /// <summary>Fork: faixa flutuante sempre visível, complementar aos ícones da bandeja.</summary>
    public WidgetOptions Widget { get; set; } = new();

    /// <summary>
    /// Fork: um ícone por serviço na bandeja (o limite que trava primeiro), com o detalhe de cada
    /// janela no cartão do mouse. Desligado, volta ao comportamento do upstream: um por limite.
    /// </summary>
    public bool GroupByService { get; set; } = true;

    /// <summary>
    /// Makes a hand-edited settings.json safe to use. Syntactically valid JSON still
    /// deserialises into nulls and absurd numbers — <c>"icons": null</c>, <c>"theme": null</c>,
    /// <c>"refreshSeconds": 3000000</c> — and each of those used to surface as a crash during
    /// start-up rather than as a bad setting. One pass here keeps that knowledge in a single
    /// place instead of a null check at every use, and <see cref="ConfigStore.Load"/> writes the
    /// repaired file back so the next start is clean.
    /// </summary>
    public AppConfig Normalize()
    {
        // The floor is the same reason the poll timer has one: the usage endpoint rate-limits.
        // The ceiling only has to keep seconds * 1000 inside the int the timer takes.
        RefreshSeconds = Math.Clamp(RefreshSeconds, 30, 86_400);
        MaxBackoffMinutes = Math.Clamp(MaxBackoffMinutes, 1, 1_440);

        Theme = Sane.Text(Theme, ThemeDefault);
        Language = Sane.Text(Language, LanguageDefault);
        FontFamily = Sane.Text(FontFamily, FontDefault);

        // A null id would throw on the first comparison in the icons menu.
        Icons = Icons is null
            ? []
            : Icons.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToList();

        // Fork: WidgetIcons nulo é intencional (migração pendente); só limpa se vier não-nulo.
        WidgetIcons = WidgetIcons is null
            ? null
            : WidgetIcons.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToList();
        KnownReadingIds = (KnownReadingIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        KnownGroups = (KnownGroups ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Colors = (Colors ?? new()).Normalize();
        Thresholds = (Thresholds ?? new()).Normalize();
        Notifications = (Notifications ?? new()).Normalize();
        Claude = (Claude ?? new()).Normalize();
        Codex = (Codex ?? new()).Normalize();
        Kimi = (Kimi ?? new()).Normalize();
        Antigravity = (Antigravity ?? new()).Normalize();
        OpenRouter = (OpenRouter ?? new()).Normalize();
        OpenAIApi = (OpenAIApi ?? new()).Normalize();
        AnthropicApi = (AnthropicApi ?? new()).Normalize();
        KimiApi = (KimiApi ?? new() { MinIntervalSeconds = 300 }).Normalize();
        DeepSeek = (DeepSeek ?? new() { MinIntervalSeconds = 300 }).Normalize();
        Widget = (Widget ?? new()).Normalize();

        return this;
    }
}

public sealed class ThresholdOptions
{
    /// <summary>At or above this percentage the value turns amber.</summary>
    public int Warn { get; set; } = 75;

    /// <summary>At or above this percentage the value turns red.</summary>
    public int Critical { get; set; } = 90;

    /// <summary>
    /// Clamped, not reordered: warn above critical is a strange setting but a legible one —
    /// every value simply turns red a step earlier. Silently swapping the two would overrule
    /// someone who meant it.
    /// </summary>
    internal ThresholdOptions Normalize()
    {
        Warn = Math.Clamp(Warn, 0, 100);
        Critical = Math.Clamp(Critical, 0, 100);
        return this;
    }
}

/// <summary>
/// Hex colours (#RRGGBB). Below the warn threshold a value is drawn in its service colour,
/// which is what tells the tray icons apart; from the warn threshold on, the severity colour
/// takes over for both services so a warning never depends on knowing the service palette.
/// </summary>
public sealed class ColorOptions
{
    internal const string ClaudeDefault = "#D97757";
    internal const string CodexDefault = "#10A37F";

    // Fork: serviços do fork. Entram na aba de cores como Claude e Codex, mas não no harmonizador
    // (âmbar e vermelho continuam derivados só dos dois originais).
    internal const string KimiDefault = "#607DFF";
    internal const string OpenRouterDefault = "#A076FA";
    internal const string AntigravityDefault = "#4285F4";
    internal const string OpenAIApiDefault = "#19C37D";
    internal const string AnthropicApiDefault = "#C15F3C";
    internal const string KimiApiDefault = "#8FA3FF";
    internal const string DeepSeekDefault = "#4D6BFE";
    internal const double ShadeSpreadDefault = 0.15;

    /// <summary>Claude's terracotta accent.</summary>
    public string Claude { get; set; } = ClaudeDefault;

    /// <summary>OpenAI's green accent.</summary>
    public string Codex { get; set; } = CodexDefault;

    public string Kimi { get; set; } = KimiDefault;

    public string OpenRouter { get; set; } = OpenRouterDefault;

    public string Antigravity { get; set; } = AntigravityDefault;

    public string OpenAIApi { get; set; } = OpenAIApiDefault;

    public string AnthropicApi { get; set; } = AnthropicApiDefault;

    public string KimiApi { get; set; } = KimiApiDefault;

    public string DeepSeek { get; set; } = DeepSeekDefault;

    /// <summary>
    /// Hue of the warning colour in degrees (48 = amber). The colour itself is built from this
    /// hue plus the shared saturation and lightness of the two service colours, so it stays in
    /// tune with them when they are changed.
    /// </summary>
    public int WarnHue { get; set; } = 48;

    /// <summary>Hue of the critical colour in degrees (352 = crimson).</summary>
    public int CriticalHue { get; set; } = 352;

    /// <summary>Set to a hex value to override the derived warning colour; null derives it.</summary>
    public string? Warn { get; set; }

    /// <summary>Set to a hex value to override the derived critical colour; null derives it.</summary>
    public string? Critical { get; set; }

    /// <summary>Colour for a limit with no value, e.g. after a failed poll. Null derives it.</summary>
    public string? Unknown { get; set; }

    /// <summary>
    /// How far apart limits of the same service are shaded, as a lightness range (0.15 = 15
    /// percentage points from the first limit to the last). 0 turns shading off and gives every
    /// limit of a service the identical colour. Only applies below the warning threshold.
    ///
    /// Kept modest on purpose: wide enough to tell three tray icons apart at a glance, narrow
    /// enough that the last shade still reads as the service's colour rather than a pale tint.
    /// </summary>
    public double ShadeSpread { get; set; } = ShadeSpreadDefault;

    internal ColorOptions Normalize()
    {
        Claude = Sane.Text(Claude, ClaudeDefault);
        Codex = Sane.Text(Codex, CodexDefault);
        Kimi = Sane.Text(Kimi, KimiDefault);
        OpenRouter = Sane.Text(OpenRouter, OpenRouterDefault);
        Antigravity = Sane.Text(Antigravity, AntigravityDefault);
        OpenAIApi = Sane.Text(OpenAIApi, OpenAIApiDefault);
        AnthropicApi = Sane.Text(AnthropicApi, AnthropicApiDefault);
        KimiApi = Sane.Text(KimiApi, KimiApiDefault);
        DeepSeek = Sane.Text(DeepSeek, DeepSeekDefault);
        WarnHue = Math.Clamp(WarnHue, 0, 359);
        CriticalHue = Math.Clamp(CriticalHue, 0, 359);

        // Null means "derive it", so only blanks and non-numbers are corrected here.
        Warn = Sane.Optional(Warn);
        Critical = Sane.Optional(Critical);
        Unknown = Sane.Optional(Unknown);
        ShadeSpread = double.IsFinite(ShadeSpread) ? Math.Clamp(ShadeSpread, 0, 1) : ShadeSpreadDefault;

        return this;
    }
}

public sealed class NotificationOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Toast fires once per window when usage crosses this percentage.</summary>
    public int AtPercent { get; set; } = 80;

    internal NotificationOptions Normalize()
    {
        AtPercent = Math.Clamp(AtPercent, 0, 100);
        return this;
    }
}

public sealed class ClaudeOptions
{
    internal const string UsageUrlDefault = "https://api.anthropic.com/api/oauth/usage";
    internal const string TokenUrlDefault = "https://console.anthropic.com/v1/oauth/token";
    internal const string ClientIdDefault = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    public bool Enabled { get; set; } = true;

    /// <summary>Defaults to %USERPROFILE%\.claude\.credentials.json.</summary>
    public string? CredentialsPath { get; set; }

    public string UsageUrl { get; set; } = UsageUrlDefault;

    /// <summary>How long to wait for the usage endpoint before giving up on a poll.</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Shortest gap between two requests to the usage endpoint, whatever the refresh interval
    /// is set to. The endpoint has a request quota and the numbers behind it move in hours, so
    /// asking it every ninety seconds spends the allowance without ever learning anything new —
    /// and the 429 that follows costs the display for a quarter of an hour. 0 removes the floor.
    /// </summary>
    public int MinIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Off by default: while Claude Code runs it keeps the token fresh on disk and we
    /// simply re-read it. Turning this on lets the tray refresh the OAuth token itself
    /// (and rewrite .credentials.json) so it keeps working when Claude Code is closed.
    /// </summary>
    public bool AutoRefreshToken { get; set; }

    public string TokenUrl { get; set; } = TokenUrlDefault;

    /// <summary>Configurable so a changed client id can be fixed without a rebuild.</summary>
    public string ClientId { get; set; } = ClientIdDefault;

    internal ClaudeOptions Normalize()
    {
        CredentialsPath = Sane.Optional(CredentialsPath);
        UsageUrl = Sane.Text(UsageUrl, UsageUrlDefault);
        TokenUrl = Sane.Text(TokenUrl, TokenUrlDefault);
        ClientId = Sane.Text(ClientId, ClientIdDefault);

        // FORK-1: o destino do token não sai do arquivo de ajustes. O upstream aceita outro host
        // e apenas avisa na tela; aqui qualquer host diferente do oficial volta ao padrão, então
        // um settings.json adulterado não consegue desviar a credencial do Claude Code.
        if (Endpoint.ForeignHost(UsageUrl, UsageUrlDefault) is not null) UsageUrl = UsageUrlDefault;
        if (Endpoint.ForeignHost(TokenUrl, TokenUrlDefault) is not null) TokenUrl = TokenUrlDefault;

        // FORK-3 (revisto): a renovação automática fica PERMITIDA.
        // Quem usa o aplicativo Claude, e não o Claude Code de linha de comando, não tem quem
        // renove ~/.claude/.credentials.json e o número ficaria "expirado" para sempre. A renovação
        // só roda com o token vencido e grava de forma atômica; o que continua travado é o destino
        // (FORK-1 acima), que agora protege também o refresh token.

        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 300);
        MinIntervalSeconds = Math.Clamp(MinIntervalSeconds, 0, 3_600);
        return this;
    }
}

/// <summary>Modo de apresentação da faixa.</summary>
public enum ModoApresentacao
{
    /// <summary>Flutuante, com margem e sombra — o comportamento original.</summary>
    Flutuante,

    /// <summary>Encaixada rente à borda da tela, como um notch.</summary>
    Notch,
}

/// <summary>Borda da tela à qual a faixa se cola no modo Notch.</summary>
public enum BordaTela
{
    Esquerda,
    Direita,
    Topo,
    Base,
}

/// <summary>
/// Fork: a faixa flutuante. O canto é guardado como nome, não como coordenada:
/// resolução muda, monitor é desligado, e uma posição em pixels levaria a faixa para fora da
/// tela. Com o canto e o nome do monitor, ela sempre nasce colada num lugar que existe.
///
/// Fork: modo Notch — a faixa encaixa rente a uma borda da tela, com quinas
/// côncavas onde encontra a borda, parecendo escavada no monitor. A orientação passa a ser
/// derivada da borda: Esquerda/Direita → vertical, Topo/Base → horizontal.
/// </summary>
public sealed class WidgetOptions
{
    internal const string CornerDefault = "bottomRight";
    internal const string OrientationDefault = "vertical";
    internal const double ScaleMin = 0.6;
    internal const double ScaleMax = 1.8;
    internal const ModoApresentacao ModoDefault = ModoApresentacao.Flutuante;
    internal const BordaTela BordaDefault = BordaTela.Direita;

    public bool Enabled { get; set; }

    /// <summary>topLeft, topRight, bottomLeft, bottomRight, topCenter ou bottomCenter.</summary>
    public string Corner { get; set; } = CornerDefault;

    /// <summary>vertical ou horizontal. No modo Notch, é derivado de <see cref="Borda"/>.</summary>
    public string Orientation { get; set; } = OrientationDefault;

    public double Scale { get; set; } = 1.0;

    /// <summary>Nome do dispositivo do monitor (\\.\DISPLAY1). Vazio = monitor principal.</summary>
    public string? Monitor { get; set; }

    /// <summary>Modo de apresentação: Flutuante (padrão) ou Notch.</summary>
    public ModoApresentacao Modo { get; set; } = ModoDefault;

    /// <summary>Borda à qual a faixa se cola no modo Notch.</summary>
    public BordaTela Borda { get; set; } = BordaDefault;

    /// <summary>Posição ao longo da borda, de 0 a 1. 0.5 = centralizada.</summary>
    public double FracaoBorda { get; set; } = 0.5;

    /// <summary>Mostrar a alça de arrasto na face externa da faixa.</summary>
    public bool MostrarAlca { get; set; } = true;

    /// <summary>
    /// Se true, o campo <see cref="Orientation"/> antigo era "horizontal". Usado uma vez na migração
    /// para derivar <see cref="Borda"/> e então zerado. Nunca exportado no JSON novo.
    /// </summary>
    internal bool _migradoHorizontal;

    /// <summary>Orientação efetiva, derivada da borda no modo Notch.</summary>
    public string OrientacaoEfetiva => Modo == ModoApresentacao.Notch
        ? (Borda is BordaTela.Esquerda or BordaTela.Direita ? "vertical" : "horizontal")
        : Orientation;

    /// <summary>True se a orientação efetiva é horizontal.</summary>
    public bool IsHorizontal => OrientacaoEfetiva.Equals("horizontal", StringComparison.OrdinalIgnoreCase);

    internal WidgetOptions Normalize()
    {
        Corner = Sane.Text(Corner, CornerDefault);

        string[] cantos = ["topLeft", "topRight", "bottomLeft", "bottomRight", "topCenter", "bottomCenter"];
        Corner = cantos.FirstOrDefault(c => c.Equals(Corner, StringComparison.OrdinalIgnoreCase))
                 ?? CornerDefault;

        Orientation = Sane.Text(Orientation, OrientationDefault);
        if (!Orientation.Equals("vertical", StringComparison.OrdinalIgnoreCase) &&
            !Orientation.Equals("horizontal", StringComparison.OrdinalIgnoreCase))
        {
            Orientation = OrientationDefault;
        }
        else
        {
            Orientation = Orientation.ToLowerInvariant();
        }

        if (double.IsNaN(Scale) || double.IsInfinity(Scale))
            Scale = 1.0;
        else
            Scale = Math.Clamp(Scale, ScaleMin, ScaleMax);

        Monitor = Sane.Optional(Monitor);

        // Normaliza enum Modo
        if (!Enum.IsDefined(Modo)) Modo = ModoDefault;

        // Normaliza enum Borda
        if (!Enum.IsDefined(Borda)) Borda = BordaDefault;

        // Migração: Horizontal antigo → Borda
        if (_migradoHorizontal)
        {
            Borda = BordaTela.Base;
            _migradoHorizontal = false;
        }

        // FracaoBorda: clamp 0–1
        if (double.IsNaN(FracaoBorda) || double.IsInfinity(FracaoBorda))
            FracaoBorda = 0.5;
        else
            FracaoBorda = Math.Clamp(FracaoBorda, 0.0, 1.0);

        return this;
    }

    /// <summary>
    /// Migra a flag <see cref="Orientation"/> "horizontal" para <see cref="Borda"/>.
    /// Chamado uma vez durante a carga do settings.json.
    /// Horizontal = true → Borda.Base; Horizontal = false → Borda.Direita.
    /// </summary>
    public static WidgetOptions MigrateOrientation(WidgetOptions opt)
    {
        // Se Modo já é Notch, a migração já aconteceu.
        if (opt.Modo == ModoApresentacao.Notch) return opt;

        // Migração não toca o Modo: ele continua Flutuante. Só prepara a Borda para quando
        // o usuário trocar para Notch.
        if (opt.Orientation.Equals("horizontal", StringComparison.OrdinalIgnoreCase))
            opt.Borda = BordaTela.Base;
        else
            opt.Borda = BordaTela.Direita;

        return opt;
    }
}

public sealed class CodexOptions
{
    public bool Enabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 30;

    internal CodexOptions Normalize()
    {
        // FORK-2: não existe mais "executablePath". O executável é sempre descoberto pelo próprio
        // app (pasta do Codex e PATH). No upstream o campo aceitava qualquer caminho, e o programa
        // apontado era iniciado a cada consulta — o pior caminho do arquivo de ajustes. Um
        // settings.json antigo que ainda o traga é lido normalmente; o campo só é ignorado.
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 300);
        return this;
    }
}

/// <summary>
/// Fork: Kimi Code. De propósito não há campo de URL nem de chave. A URL é fixa no
/// provedor pelo mesmo princípio da FORK-1 — um settings.json adulterado não pode mandar a chave
/// para outro host — e a chave fica no Gerenciador de Credenciais do Windows (ver
/// <see cref="CredentialVault"/>), porque este arquivo é texto puro que qualquer processo do
/// usuário lê.
/// </summary>
public sealed class KimiOptions
{
    public bool Enabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>A cota anda em horas; perguntar a cada 90 s só gasta requisição.</summary>
    public int MinIntervalSeconds { get; set; } = 300;

    internal KimiOptions Normalize()
    {
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 300);
        MinIntervalSeconds = Math.Clamp(MinIntervalSeconds, 0, 3_600);
        return this;
    }
}

/// <summary>
/// Fork: OpenRouter. Mesmas regras do <see cref="KimiOptions"/>: base da API fixa no
/// código, chave só no cofre.
/// </summary>
public sealed class OpenRouterOptions
{
    public bool Enabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Saldo muda com o uso, mas não a ponto de justificar consulta a cada 90 s.</summary>
    public int MinIntervalSeconds { get; set; } = 300;

    internal OpenRouterOptions Normalize()
    {
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 300);
        MinIntervalSeconds = Math.Clamp(MinIntervalSeconds, 0, 3_600);
        return this;
    }
}

/// <summary>
/// Fork: opções de um provedor do trilho de API (gasto, uso ou saldo lidos por chave). Endereço
/// fixo no código e chave só no cofre, como no OpenRouter. O intervalo mínimo padrão é de 15 min
/// porque gasto e uso chegam em baldes diários — consultar a cada 90 s só gastaria cota da API.
/// </summary>
public sealed class ApiProviderOptions
{
    public bool Enabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 20;

    public int MinIntervalSeconds { get; set; } = 900;

    internal ApiProviderOptions Normalize()
    {
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 300);
        MinIntervalSeconds = Math.Clamp(MinIntervalSeconds, 60, 86_400);
        return this;
    }
}

/// <summary>
/// The two string repairs every <c>Normalize</c> above needs. Deliberately file-local: this is
/// about surviving a hand-edited file, not an API anything else should reach for.
/// </summary>
file static class Sane
{
    /// <summary>A blank value means the setting was never really set, so the default applies.</summary>
    public static string Text(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>For settings where null is meaningful — blank collapses to it rather than past it.</summary>
    public static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Fork: Antigravity (Google AI Pro). Não há campo de executável: o caminho do agy é
/// fixo no provedor, pela mesma razão da FORK-2.
/// </summary>
public sealed class AntigravityOptions
{
    public bool Enabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Cada consulta abre um processo; a cota anda em horas.</summary>
    public int MinIntervalSeconds { get; set; } = 300;

    internal AntigravityOptions Normalize()
    {
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 300);
        MinIntervalSeconds = Math.Clamp(MinIntervalSeconds, 0, 3_600);
        return this;
    }
}
