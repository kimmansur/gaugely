using System.Globalization;
using System.Text.Json.Nodes;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;

namespace RateTray.Providers;

/// <summary>
/// Fork: trilho de API da Anthropic — gasto e uso do mês pelo Usage &amp; Cost Admin API
/// (<c>/v1/organizations/cost_report</c> e <c>/usage_report/messages</c>). Exige chave
/// <b>Admin</b>, e a documentação avisa que o Admin API não existe para conta individual: é preciso
/// uma organização no Console. Os custos chegam em <b>centavos, como texto decimal</b> —
/// <c>"123.78912"</c> são US$ 1,24.
/// </summary>
public sealed class AnthropicApiUsageProvider(ApiProviderOptions options) : ApiKeyProvider(options)
{
    internal const string GroupName = "Anthropic API";

    private const string Costs = "costs";
    private const string Usage = "usage";

    public override string Group => GroupName;

    protected override string VaultService => CredentialVault.AnthropicAdmin;

    protected override string Vendor => "Anthropic";

    protected override string BaseUrl => "https://api.anthropic.com/v1/organizations";

    protected override void Authorize(HttpRequestMessage request, string key)
    {
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", "2023-06-01");
    }

    protected override async Task<IReadOnlyList<Fetch>> FetchAllAsync(Func<string, Task<Fetch>> get)
    {
        // limit=31 é o máximo com baldes diários; o padrão devolveria só 7 dias.
        var since = Uri.EscapeDataString(MonthStart(DateTimeOffset.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        var pages = await Task.WhenAll(
            FetchPagesAsync(get, $"/cost_report?starting_at={since}&bucket_width=1d&limit=31", Costs),
            FetchPagesAsync(get, $"/usage_report/messages?starting_at={since}&bucket_width=1d&limit=31", Usage)).ConfigureAwait(false);
        return pages.SelectMany(p => p).ToList();
    }

    protected override List<LimitReading> Parse(IReadOnlyList<Fetch> fetches, DateTimeOffset now)
    {
        var today = DayStart(now);
        decimal? monthCents = null, dayCents = null;
        long tokens = 0;
        var sawUsage = false;

        foreach (var fetch in fetches)
        {
            if (ParseJson(fetch.Json) is not JsonObject body || body["data"] is not JsonArray buckets) continue;

            foreach (var bucket in buckets.OfType<JsonObject>())
            {
                if (bucket["results"] is not JsonArray results) continue;
                var startsToday = Text(bucket["starting_at"]) is { } start &&
                                  DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at >= today;

                foreach (var item in results.OfType<JsonObject>())
                {
                    if (fetch.Kind == Costs)
                    {
                        if (Number(item["amount"]) is not { } cents) continue;
                        if (Text(item["currency"]) is { } currency && !currency.Equals("USD", StringComparison.OrdinalIgnoreCase)) continue;
                        monthCents = (monthCents ?? 0m) + cents;
                        if (startsToday) dayCents = (dayCents ?? 0m) + cents;
                    }
                    else if (fetch.Kind == Usage)
                    {
                        sawUsage = true;
                        var cache = item["cache_creation"] as JsonObject;
                        tokens += (long)((Number(item["uncached_input_tokens"]) ?? 0m) +
                                         (Number(item["cache_read_input_tokens"]) ?? 0m) +
                                         (Number(cache?["ephemeral_1h_input_tokens"]) ?? 0m) +
                                         (Number(cache?["ephemeral_5m_input_tokens"]) ?? 0m) +
                                         (Number(item["output_tokens"]) ?? 0m));
                    }
                }
            }
        }

        var readings = new List<LimitReading>();
        if (monthCents is not { } month) return readings;

        readings.Add(Money("anthropicapi.spend_month", Loc.T("label.api.spendMonth"), Math.Round(month / 100m, 2)) with
        {
            Window = TimeSpan.FromDays(30),
            Note = sawUsage ? Loc.T("note.api.tokens", CompactCount(tokens)) : null,
        });
        readings.Add(Money("anthropicapi.spend_today", Loc.T("label.api.spendToday"), Math.Round((dayCents ?? 0m) / 100m, 2)) with
        {
            Window = TimeSpan.FromDays(1),
        });
        return readings;
    }

    /// <summary>Teste: o parser sobre respostas cruas, sem rede.</summary>
    internal static List<LimitReading> ParseForTest(IEnumerable<string> costPages, IEnumerable<string> usagePages, DateTimeOffset now) =>
        new AnthropicApiUsageProvider(new ApiProviderOptions()).Parse(
            costPages.Select(j => Fetch.Ok(j, Costs)).Concat(usagePages.Select(j => Fetch.Ok(j, Usage))).ToList(), now);
}
