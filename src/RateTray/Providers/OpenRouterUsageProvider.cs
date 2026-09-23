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
/// Fork: OpenRouter não tem cota de assinatura — é crédito pré-pago. O que interessa
/// é quanto sobra (<c>/credits</c>) e quanto se gastou no dia, na semana e no mês (<c>/key</c>).
/// Esses números saem como leituras informativas (<see cref="LimitReading.Amount"/>), que não
/// disputam o "limite que trava primeiro". Percentual só existe quando a própria chave tem teto.
/// </summary>
public sealed class OpenRouterUsageProvider(OpenRouterOptions options) : IUsageProvider
{
    /// <summary>Base fixa no código, sem campo no settings.json — mesmo princípio da FORK-1.</summary>
    internal const string BaseUrl = "https://openrouter.ai/api/v1";

    internal const string GroupName = "OpenRouter";

    private const string Dollar = "US$";

    // Fork: SocketsHttpHandler previne exhaustion e DNS stale mantendo Timeout.InfiniteTimeSpan
    private static readonly HttpClient Http = SecureHttp.Create();   // Fork: sem redirecionamento, com teto de tamanho

    public string Group => GroupName;

    /// <summary>
    /// Sem chave no cofre o provedor fica desligado, e não em erro — relido a cada ciclo, para a
    /// chave colada nos ajustes valer sem reiniciar.
    /// </summary>
    public bool Enabled => options.Enabled && CredentialVault.Has(CredentialVault.OpenRouter);

    public TimeSpan MinInterval => TimeSpan.FromSeconds(Math.Max(0, options.MinIntervalSeconds));

    public async Task<ProviderResult> ReadAsync(CancellationToken ct)
    {
        var key = CredentialVault.Read(CredentialVault.OpenRouter);
        if (key is not { Length: > 0 })
        {
            return ProviderResult.Failed(Group, Loc.T("error.openrouter.noKey")) with
            {
                Auth = new AuthStatus { Group = Group, IsValid = false, Detail = Loc.T("auth.noKey") },
            };
        }

        var auth = new AuthStatus { Group = Group, IsValid = true, Detail = Loc.T("auth.apiKey") };

        if (!Endpoint.IsSecure(BaseUrl))
            return ProviderResult.Failed(Group, Loc.T("error.insecureUrl", BaseUrl)) with { Auth = auth };

        // Um prazo só para as duas chamadas, que correm em paralelo.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));

        var creditsTask = FetchAsync("/credits", key, ct, deadline.Token);
        var keyTask = FetchAsync("/key", key, ct, deadline.Token);
        var fetches = await Task.WhenAll(creditsTask, keyTask).ConfigureAwait(false);
        var credits = fetches[0];
        var keyInfo = fetches[1];

        // Cada chamada pode falhar sozinha: /credits exige permissão que uma chave restrita pode
        // não ter, e o saldo sem o gasto (ou o contrário) ainda vale mais que nada.
        if (credits.Json is not null || keyInfo.Json is not null)
        {
            var readings = Parse(credits.Json, keyInfo.Json);
            if (readings.Count > 0) return ProviderResult.Success(Group, readings) with { Auth = auth };
            return ProviderResult.Failed(Group, Loc.T("error.openrouter.noData")) with { Auth = auth };
        }

        // As duas falharam. A recusa da chave vence as outras causas: é a única que o usuário resolve.
        if (credits.Status == HttpStatusCode.Unauthorized || keyInfo.Status == HttpStatusCode.Unauthorized)
            return ProviderResult.Failed(Group, Loc.T("error.openrouter.rejected")) with { Auth = auth with { IsValid = false } };

        if (credits.RateLimited || keyInfo.RateLimited)
        {
            return ProviderResult.Failed(Group, Loc.T("error.openrouter.rateLimited")) with
            {
                Auth = auth,
                RetryAfter = MaxOf(credits.RetryAfter, keyInfo.RetryAfter),
                RateLimited = true,
            };
        }

        return ProviderResult.Failed(Group, credits.Error ?? keyInfo.Error ?? Loc.T("error.openrouter.noData")) with { Auth = auth };
    }

    private static TimeSpan? MaxOf(TimeSpan? a, TimeSpan? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);

    /// <summary>Resultado de uma chamada: o JSON quando deu certo, ou o motivo já localizado.</summary>
    private sealed record Fetch(string? Json, HttpStatusCode? Status, string? Error)
    {
        public bool RateLimited { get; init; }

        public TimeSpan? RetryAfter { get; init; }
    }

    private async Task<Fetch> FetchAsync(string path, string key, CancellationToken shutdown, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("Gaugely");

            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            var status = response.StatusCode;

            if (status == HttpStatusCode.Unauthorized)
                return new Fetch(null, status, Loc.T("error.openrouter.rejected"));

            if (status == HttpStatusCode.Forbidden)
                return new Fetch(null, status, Loc.T("error.openrouter.forbidden"));

            if (status == HttpStatusCode.TooManyRequests)
            {
                return new Fetch(null, status, Loc.T("error.openrouter.rateLimited"))
                {
                    RateLimited = true,
                    RetryAfter = ClaudeUsageProvider.RetryAfterOf(response),
                };
            }

            if (!response.IsSuccessStatusCode)
                return new Fetch(null, status, Loc.T("error.openrouter.http", (int)status));

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return new Fetch(json, status, null);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            throw;                                  // encerrando, não é falha
        }
        catch (OperationCanceledException)
        {
            return new Fetch(null, null, Loc.T("error.openrouter.timeout", options.TimeoutSeconds));
        }
        catch (Exception ex)
        {
            return new Fetch(null, null, Loc.T("error.fetchFailed", SecureHttp.Describe(ex)));
        }
    }

    /// <summary>
    /// Leituras a partir das duas respostas; qualquer uma pode ser null (chamada que falhou) ou
    /// ilegível, e a outra continua valendo.
    /// </summary>
    internal static List<LimitReading> Parse(string? creditsJson, string? keyJson)
    {
        var readings = new List<LimitReading>();

        if (DataOf(creditsJson) is { } credits &&
            Money(credits["total_credits"]) is { } total &&
            Money(credits["total_usage"]) is { } used)
        {
            // Nunca negativo: o OpenRouter deixa o uso passar um pouco do crédito antes de cortar,
            // e "-0,03" num ícone parece defeito, não saldo.
            readings.Add(Informational("openrouter.balance", Loc.T("label.openrouter.balance"), Math.Max(0m, total - used)));
        }

        if (DataOf(keyJson) is { } key)
        {
            if (Money(key["usage_daily"]) is { } today)
                readings.Add(Informational("openrouter.spend_today", Loc.T("label.openrouter.spendToday"), today) with
                {
                    Window = TimeSpan.FromDays(1),
                });

            if (Money(key["usage_weekly"]) is { } week)
                readings.Add(Informational("openrouter.spend_week", Loc.T("label.openrouter.spendWeek"), week) with
                {
                    Window = TimeSpan.FromDays(7),
                });

            if (Money(key["usage_monthly"]) is { } month)
                readings.Add(Informational("openrouter.spend_month", Loc.T("label.openrouter.spendMonth"), month) with
                {
                    Window = TimeSpan.FromDays(30),
                });

            // IMPORTANTE: teto da chave NÃO é saldo. É o quanto esta chave pode gastar (por dia,
            // semana, mês ou para sempre) antes de o OpenRouter recusá-la — mesmo com crédito
            // sobrando na conta. Por isso é a única leitura com percentual: é a que trava.
            // Sem teto (limit null ou 0) não há o que travar, e nada é criado.
            if (Money(key["limit"]) is { } limit && limit > 0 &&
                Money(key["limit_remaining"]) is { } remaining)
            {
                var left = Math.Clamp(remaining, 0m, limit);
                var reset = Text(key["limit_reset"]);

                readings.Add(new LimitReading
                {
                    Id = "openrouter.key_limit",
                    Label = Loc.T("label.openrouter.keyLimit"),
                    Group = GroupName,
                    Percent = Math.Clamp((double)(100m * (limit - left) / limit), 0, 100),
                    Window = reset switch
                    {
                        "daily" => TimeSpan.FromDays(1),
                        "weekly" => TimeSpan.FromDays(7),
                        _ => null,
                    },
                    Note = reset switch
                    {
                        "daily" => Loc.T("note.openrouter.resetDaily"),
                        "weekly" => Loc.T("note.openrouter.resetWeekly"),
                        "monthly" => Loc.T("note.openrouter.resetMonthly"),
                        _ => Loc.T("note.openrouter.resetNever"),
                    },
                    IsActive = true,
                });
            }
        }

        return readings
            .Select((reading, index) => reading with { Variant = index, VariantCount = readings.Count })
            .ToList();
    }

    private static LimitReading Informational(string id, string label, decimal amount) => new()
    {
        Id = id,
        Label = label,
        Group = GroupName,
        Amount = amount,
        AmountUnit = Dollar,
    };

    /// <summary>O objeto <c>data</c> da resposta, ou null quando a resposta falta ou não é JSON.</summary>
    private static JsonObject? DataOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return (JsonNode.Parse(json) as JsonObject)?["data"] as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Valor em dólar como número JSON ou string; decimal para não ganhar centavos fantasmas de double.</summary>
    private static decimal? Money(JsonNode? node)
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
            // Número fora do alcance de decimal (1e400): melhor sem a leitura que com um valor errado.
        }

        return null;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue<string>(out var text)
            ? text
            : null;
}
