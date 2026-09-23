using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;

namespace RateTray.Providers;

/// <summary>
/// Fork: trilho de API da OpenAI — gasto e uso do mês pela Admin API
/// (<c>/v1/organization/costs</c> e <c>/v1/organization/usage/completions</c>). Exige chave
/// <b>Admin</b>; uma chave comum recebe 401/403. O saldo pré-pago não é exposto oficialmente, então
/// não há leitura de saldo: o que se mostra é quanto foi gasto, nunca quanto sobra.
/// </summary>
public sealed class OpenAIApiUsageProvider(ApiProviderOptions options) : ApiKeyProvider(options)
{
    internal const string GroupName = "OpenAI API";

    private const string Costs = "costs";
    private const string Usage = "usage";

    public override string Group => GroupName;

    protected override string VaultService => CredentialVault.OpenAIAdmin;

    protected override string Vendor => "OpenAI";

    protected override string BaseUrl => "https://api.openai.com/v1/organization";

    protected override void Authorize(HttpRequestMessage request, string key) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

    protected override async Task<IReadOnlyList<Fetch>> FetchAllAsync(Func<string, Task<Fetch>> get)
    {
        // limit=31: o padrão da API é 7 baldes, e um mês tem até 31 dias.
        var since = MonthStart(DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var pages = await Task.WhenAll(
            FetchPagesAsync(get, $"/costs?start_time={since}&bucket_width=1d&limit=31", Costs, Vendor),
            FetchPagesAsync(get, $"/usage/completions?start_time={since}&bucket_width=1d&limit=31", Usage, Vendor)).ConfigureAwait(false);
        return pages.SelectMany(p => p).ToList();
    }

    protected override List<LimitReading> Parse(IReadOnlyList<Fetch> fetches, DateTimeOffset now)
    {
        var today = DayStart(now).ToUnixTimeSeconds();
        decimal? month = null, day = null;
        long tokens = 0, requests = 0;
        var sawUsage = false;

        foreach (var fetch in fetches)
        {
            if (ParseJson(fetch.Json) is not JsonObject body || body["data"] is not JsonArray buckets) continue;
            if (fetch.Kind == Costs) month ??= 0m;          // relatório válido e vazio = nada gasto no mês

            foreach (var bucket in buckets.OfType<JsonObject>())
            {
                // O esquema formal diz "result"; os exemplos do mesmo documento e a API real dizem
                // "results". Os dois valem.
                if ((bucket["results"] ?? bucket["result"]) is not JsonArray results) continue;
                var startsToday = Number(bucket["start_time"]) is { } start && (long)start >= today;

                foreach (var item in results.OfType<JsonObject>())
                {
                    if (fetch.Kind == Costs)
                    {
                        if (item["amount"] is not JsonObject amount || Number(amount["value"]) is not { } value) continue;
                        if (Text(amount["currency"]) is { } currency && !currency.Equals("usd", StringComparison.OrdinalIgnoreCase)) continue;
                        month = (month ?? 0m) + value;
                        if (startsToday) day = (day ?? 0m) + value;
                    }
                    else if (fetch.Kind == Usage)
                    {
                        sawUsage = true;
                        tokens += (long)(Number(item["input_tokens"]) ?? 0m) + (long)(Number(item["output_tokens"]) ?? 0m);
                        requests += (long)(Number(item["num_model_requests"]) ?? 0m);
                    }
                }
            }
        }

        var readings = new List<LimitReading>();
        if (month is not { } spent) return readings;

        readings.Add(Money("openaiapi.spend_month", Loc.T("label.api.spendMonth"), spent) with
        {
            Window = TimeSpan.FromDays(30),
            Note = sawUsage ? Loc.T("note.api.tokensRequests", CompactCount(tokens), CompactCount(requests)) : null,
        });
        readings.Add(Money("openaiapi.spend_today", Loc.T("label.api.spendToday"), day ?? 0m) with
        {
            Window = TimeSpan.FromDays(1),
        });
        return readings;
    }

    /// <summary>Teste: o parser sobre respostas cruas, sem rede.</summary>
    internal static List<LimitReading> ParseForTest(IEnumerable<string> costPages, IEnumerable<string> usagePages, DateTimeOffset now) =>
        new OpenAIApiUsageProvider(new ApiProviderOptions()).Parse(
            costPages.Select(j => Fetch.Ok(j, Costs)).Concat(usagePages.Select(j => Fetch.Ok(j, Usage))).ToList(), now);
}
