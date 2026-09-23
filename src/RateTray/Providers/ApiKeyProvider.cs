using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;

namespace RateTray.Providers;

/// <summary>
/// Fork: base dos provedores do trilho de API — gasto, uso ou saldo lidos com uma chave do cofre.
/// Cada provedor diz só o próprio endereço, como se autentica e como ler a resposta; o resto é
/// igual para todos e mora aqui: chave do cofre relida a cada ciclo, HTTPS obrigatório, um prazo
/// para todas as chamadas do ciclo e os erros traduzidos para o que o usuário pode resolver.
/// </summary>
public abstract class ApiKeyProvider(ApiProviderOptions options) : IUsageProvider
{
    // Um cliente para todos os provedores de API; sem redirecionamento e com teto de tamanho.
    private static readonly HttpClient Http = SecureHttp.Create();

    internal const string Dollar = "US$";

    public abstract string Group { get; }

    /// <summary>Nome do serviço no cofre (<see cref="CredentialVault"/>).</summary>
    protected abstract string VaultService { get; }

    /// <summary>Nome do fornecedor nas mensagens de erro ("OpenAI", "Anthropic"...).</summary>
    protected abstract string Vendor { get; }

    /// <summary>Base fixa no código — mesmo princípio da FORK-1: o settings.json não a muda.</summary>
    protected abstract string BaseUrl { get; }

    /// <summary>Sem chave no cofre o provedor fica desligado, e não em erro.</summary>
    public bool Enabled => options.Enabled && CredentialVault.Has(VaultService);

    public TimeSpan MinInterval => TimeSpan.FromSeconds(Math.Max(0, options.MinIntervalSeconds));

    /// <summary>Põe a chave no pedido, no formato que o fornecedor exige.</summary>
    protected abstract void Authorize(HttpRequestMessage request, string key);

    /// <summary>
    /// Faz as chamadas do ciclo por <paramref name="get"/> e devolve as leituras. Uma resposta que
    /// falhou chega como <see cref="Fetch"/> sem JSON; a implementação decide se o resto basta.
    /// </summary>
    protected abstract Task<IReadOnlyList<Fetch>> FetchAllAsync(Func<string, Task<Fetch>> get);

    /// <summary>Leituras a partir das respostas que chegaram; lista vazia se nenhuma serviu.</summary>
    protected abstract List<LimitReading> Parse(IReadOnlyList<Fetch> fetches, DateTimeOffset now);

    public async Task<ProviderResult> ReadAsync(CancellationToken ct)
    {
        var key = CredentialVault.Read(VaultService);
        if (key is not { Length: > 0 })
        {
            return ProviderResult.Failed(Group, Loc.T("error.api.noKey", Vendor)) with
            {
                Auth = new AuthStatus { Group = Group, IsValid = false, Detail = Loc.T("auth.noKey") },
            };
        }

        var auth = new AuthStatus { Group = Group, IsValid = true, Detail = Loc.T("auth.apiKey") };

        if (!Endpoint.IsSecure(BaseUrl))
            return ProviderResult.Failed(Group, Loc.T("error.insecureUrl", BaseUrl)) with { Auth = auth };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));

        var fetches = await FetchAllAsync(path => FetchAsync(path, key, ct, deadline.Token)).ConfigureAwait(false);

        if (fetches.Any(f => f.Json is not null))
        {
            var readings = Parse(fetches, DateTimeOffset.UtcNow);
            if (readings.Count > 0)
            {
                readings = readings
                    .Select((reading, index) => reading with { Variant = index, VariantCount = readings.Count })
                    .ToList();
                return ProviderResult.Success(Group, readings) with { Auth = auth };
            }

            return ProviderResult.Failed(Group, Loc.T("error.api.noData", Vendor)) with { Auth = auth };
        }

        // Nenhuma chamada serviu. A chave recusada vence as outras causas: é a única que o usuário resolve.
        if (fetches.Any(f => f.Status == HttpStatusCode.Unauthorized))
            return ProviderResult.Failed(Group, Loc.T("error.api.rejected", Vendor)) with { Auth = auth with { IsValid = false } };

        if (fetches.Any(f => f.RateLimited))
        {
            return ProviderResult.Failed(Group, Loc.T("error.api.rateLimited", Vendor)) with
            {
                Auth = auth,
                RetryAfter = fetches.Max(f => f.RetryAfter),
                RateLimited = true,
            };
        }

        var first = fetches.FirstOrDefault(f => f.Error is not null);
        return ProviderResult.Failed(Group, first?.Error ?? Loc.T("error.api.noData", Vendor)) with { Auth = auth };
    }

    /// <summary>Resultado de uma chamada: o JSON quando deu certo, ou o motivo já localizado.</summary>
    protected internal sealed record Fetch(string? Json, HttpStatusCode? Status, string? Error)
    {
        public bool RateLimited { get; init; }

        public TimeSpan? RetryAfter { get; init; }

        /// <summary>De qual endpoint veio a resposta, quando o provedor chama mais de um.</summary>
        public string? Kind { get; init; }

        /// <summary>Resposta de teste, sem rede.</summary>
        internal static Fetch Ok(string json, string? kind = null) => new(json, HttpStatusCode.OK, null) { Kind = kind };
    }

    /// <summary>
    /// Segue a paginação por cursor (<c>has_more</c> + <c>next_page</c>, formato comum a OpenAI e
    /// Anthropic) até acabar ou até <paramref name="maxPages"/>, marcando cada página com
    /// <paramref name="kind"/>. O cursor vai escapado dentro de <c>page=</c>: muda a página, nunca
    /// o host nem o caminho.
    ///
    /// Um relatório só vale inteiro. Se uma página falha no meio, ou ainda há páginas depois do
    /// teto, somar o que chegou mostraria um gasto menor que o real, como se estivesse completo.
    /// Nesses casos o resultado é só a falha — e a bandeja segue com a última leitura boa.
    /// </summary>
    protected static async Task<List<Fetch>> FetchPagesAsync(Func<string, Task<Fetch>> get, string path, string kind, string vendor, int maxPages = 5)
    {
        var pages = new List<Fetch>();
        var next = path;
        for (var i = 0; i < maxPages && next is not null; i++)
        {
            var fetch = (await get(next).ConfigureAwait(false)) with { Kind = kind };
            if (fetch.Json is null) return [fetch];

            // Página só vale com a forma de página: objeto com a lista "data". Um {"error": ...}
            // com 200, ou HTML de proxy, é falha — não uma página vazia que some da soma.
            if (ParseJson(fetch.Json) is not JsonObject body || body["data"] is not JsonArray)
                return [new Fetch(null, fetch.Status, Loc.T("error.api.noData", vendor)) { Kind = kind }];

            pages.Add(fetch);
            if (!Flag(body["has_more"])) return pages;

            // Mais páginas declaradas e nenhum cursor para buscá-las: o relatório está incompleto.
            if (Text(body["next_page"]) is not { Length: > 0 } cursor)
                return [new Fetch(null, fetch.Status, Loc.T("error.api.incomplete", vendor)) { Kind = kind }];

            next = $"{path}&page={Uri.EscapeDataString(cursor)}";
        }

        // O laço só termina aqui quando o teto foi atingido com páginas ainda por vir.
        return [new Fetch(null, null, Loc.T("error.api.incomplete", vendor)) { Kind = kind }];
    }

    internal static bool Flag(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() is JsonValueKind.True;

    private async Task<Fetch> FetchAsync(string path, string key, CancellationToken shutdown, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            Authorize(request, key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("Gaugely");

            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            var status = response.StatusCode;

            if (status == HttpStatusCode.Unauthorized)
                return new Fetch(null, status, Loc.T("error.api.rejected", Vendor));

            if (status == HttpStatusCode.Forbidden)
                return new Fetch(null, status, Loc.T("error.api.forbidden", Vendor));

            if (status == HttpStatusCode.TooManyRequests)
            {
                return new Fetch(null, status, Loc.T("error.api.rateLimited", Vendor))
                {
                    RateLimited = true,
                    RetryAfter = ClaudeUsageProvider.RetryAfterOf(response),
                };
            }

            if (!response.IsSuccessStatusCode)
                return new Fetch(null, status, Loc.T("error.api.http", (int)status, Vendor));

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return new Fetch(json, status, null);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            throw;                                  // encerrando, não é falha
        }
        catch (OperationCanceledException)
        {
            return new Fetch(null, null, Loc.T("error.api.timeout", Vendor, options.TimeoutSeconds));
        }
        catch (Exception ex)
        {
            return new Fetch(null, null, Loc.T("error.fetchFailed", SecureHttp.Describe(ex)));
        }
    }

    // ------------------------------------------------------------------ ajuda para os parsers

    /// <summary>Leitura em dinheiro: informa, não trava nada (<see cref="LimitReading.IsInformational"/>).</summary>
    protected LimitReading Money(string id, string label, decimal amount, string unit = Dollar) => new()
    {
        Id = id,
        Label = label,
        Group = Group,
        Amount = amount,
        AmountUnit = unit,
    };

    /// <summary>Primeiro instante do mês corrente em UTC — os fornecedores fecham baldes em UTC.</summary>
    internal static DateTimeOffset MonthStart(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    internal static DateTimeOffset DayStart(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
    }

    internal static JsonNode? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Número JSON ou texto numérico; decimal para não ganhar centavos fantasmas de double.</summary>
    internal static decimal? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;

        try
        {
            if (value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<decimal>(out var number))
                return number;

            if (value.GetValueKind() == JsonValueKind.String && value.TryGetValue<string>(out var text) &&
                decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException)
        {
            // Fora do alcance de decimal: melhor sem a leitura que com um valor errado.
        }

        return null;
    }

    internal static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue<string>(out var text)
            ? text
            : null;

    /// <summary>Tokens em forma curta para a nota: 1,2 M · 850 mil · 900.</summary>
    internal static string CompactCount(long value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000d).ToString("0.#", Loc.Culture) + " G",
        >= 1_000_000 => (value / 1_000_000d).ToString("0.#", Loc.Culture) + " M",
        >= 10_000 => (value / 1_000d).ToString("0", Loc.Culture) + " k",
        _ => value.ToString("N0", Loc.Culture),
    };
}
