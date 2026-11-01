using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;

namespace RateTray.Providers;

/// <summary>
/// Fork: crédito e gasto da Experiential Labs. O saldo é da conta (<c>/credits</c>); o gasto do
/// dia, da semana e do mês é da chave usada na consulta (<c>/key</c>).
/// </summary>
public sealed class ExperientialUsageProvider(ApiProviderOptions options) : ApiKeyProvider(options)
{
    internal const string GroupName = "Experiential";

    private const string CreditsKind = "credits";
    private const string KeyKind = "key";

    public override string Group => GroupName;

    protected override string VaultService => CredentialVault.Experiential;

    protected override string Vendor => "Experiential";

    protected override string BaseUrl => "https://api.experientiallabs.ai/api/v1";

    protected override void Authorize(HttpRequestMessage request, string key) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

    protected override async Task<IReadOnlyList<Fetch>> FetchAllAsync(Func<string, Task<Fetch>> get)
    {
        var credits = get("/credits");
        var key = get("/key");
        await Task.WhenAll(credits, key).ConfigureAwait(false);
        return [credits.Result with { Kind = CreditsKind }, key.Result with { Kind = KeyKind }];
    }

    protected override List<LimitReading> Parse(IReadOnlyList<Fetch> fetches, DateTimeOffset now)
    {
        var readings = new List<LimitReading>();

        if (DataOf(fetches, CreditsKind) is { } credits &&
            Number(credits["total_credits"]) is { } total &&
            Number(credits["total_usage"]) is { } used)
        {
            readings.Add(Money("experiential.balance", Loc.T("label.api.balance"), Math.Max(0m, total - used)));
        }
        else if (fetches.FirstOrDefault(f => f.Kind == CreditsKind)?.Status != HttpStatusCode.Forbidden)
        {
            // Sem saldo por falha passageira, o ícone mostraria o gasto do dia como se fosse saldo.
            // Vazio mantém a última leitura boa. O 403 é fixo: chave presa a um plano não lê a carteira.
            return readings;
        }

        if (DataOf(fetches, KeyKind) is { } key)
        {
            AddSpend(readings, "experiential.spend_today", "label.api.spendToday", key["usage_daily"]);
            AddSpend(readings, "experiential.spend_week", "label.openrouter.spendWeek", key["usage_weekly"]);
            AddSpend(readings, "experiential.spend_month", "label.api.spendMonth", key["usage_monthly"]);
        }

        return readings;
    }

    private void AddSpend(List<LimitReading> readings, string id, string labelKey, JsonNode? value)
    {
        if (Number(value) is { } amount)
            readings.Add(Money(id, Loc.T(labelKey), amount));
    }

    private static JsonObject? DataOf(IReadOnlyList<Fetch> fetches, string kind) =>
        ParseJson(fetches.FirstOrDefault(f => f.Kind == kind)?.Json) is JsonObject body
            ? body["data"] as JsonObject
            : null;

    /// <summary>Teste: o parser sobre as respostas cruas, sem rede; null simula a chamada que falhou.</summary>
    internal static List<LimitReading> ParseForTest(string? creditsJson, string? keyJson, HttpStatusCode creditsFailure = HttpStatusCode.ServiceUnavailable)
    {
        var fetches = new List<Fetch>
        {
            creditsJson is not null ? Fetch.Ok(creditsJson, CreditsKind) : new Fetch(null, creditsFailure, "falha") { Kind = CreditsKind },
        };
        if (keyJson is not null) fetches.Add(Fetch.Ok(keyJson, KeyKind));
        return new ExperientialUsageProvider(new ApiProviderOptions()).Parse(fetches, DateTimeOffset.UtcNow);
    }
}
