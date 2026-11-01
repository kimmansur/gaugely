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

        // Sem saldo, o ícone mostraria o gasto do dia como se fosse saldo. Só a falha volta, para a
        // bandeja manter a última leitura boa e tratar a causa real (429, chave recusada, prazo).
        return credits.Result.Json is null
            ? [credits.Result]
            : [credits.Result with { Kind = CreditsKind }, key.Result with { Kind = KeyKind }];
    }

    protected override List<LimitReading> Parse(IReadOnlyList<Fetch> fetches, DateTimeOffset now)
    {
        var readings = new List<LimitReading>();

        if (DataOf(fetches, CreditsKind) is not { } credits ||
            Number(credits["total_credits"]) is not { } total ||
            Number(credits["total_usage"]) is not { } used)
        {
            return readings;
        }

        readings.Add(Money("experiential.balance", Loc.T("label.api.balance"), Math.Max(0m, total - used)));

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

    /// <summary>Teste: as chamadas do ciclo com respostas falsas.</summary>
    internal Task<IReadOnlyList<Fetch>> FetchAllForTest(Func<string, Task<Fetch>> get) => FetchAllAsync(get);

    /// <summary>Teste: o parser sobre as respostas cruas, sem rede; null em /key simula a chamada que falhou.</summary>
    internal static List<LimitReading> ParseForTest(string creditsJson, string? keyJson)
    {
        var fetches = new List<Fetch> { Fetch.Ok(creditsJson, CreditsKind) };
        if (keyJson is not null) fetches.Add(Fetch.Ok(keyJson, KeyKind));
        return new ExperientialUsageProvider(new ApiProviderOptions()).Parse(fetches, DateTimeOffset.UtcNow);
    }
}
