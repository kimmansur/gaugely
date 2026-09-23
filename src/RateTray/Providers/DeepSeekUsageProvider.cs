using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;

namespace RateTray.Providers;

/// <summary>
/// Fork: saldo da API da DeepSeek (<c>/user/balance</c>). A resposta traz uma entrada por moeda
/// (USD e/ou CNY), com o saldo total, o bônus concedido e o recarregado — tudo como texto.
/// Prefere US$; sem ele, mostra o saldo em yuan.
/// </summary>
public sealed class DeepSeekUsageProvider(ApiProviderOptions options) : ApiKeyProvider(options)
{
    internal const string GroupName = "DeepSeek";

    private const string Yuan = "¥";

    public override string Group => GroupName;

    protected override string VaultService => CredentialVault.DeepSeek;

    protected override string Vendor => "DeepSeek";

    protected override string BaseUrl => "https://api.deepseek.com";

    protected override void Authorize(HttpRequestMessage request, string key) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

    protected override async Task<IReadOnlyList<Fetch>> FetchAllAsync(Func<string, Task<Fetch>> get) =>
        [await get("/user/balance").ConfigureAwait(false)];

    protected override List<LimitReading> Parse(IReadOnlyList<Fetch> fetches, DateTimeOffset now)
    {
        var readings = new List<LimitReading>();
        foreach (var fetch in fetches)
        {
            if (ParseJson(fetch.Json) is not JsonObject body || body["balance_infos"] is not JsonArray infos) continue;

            var entries = infos.OfType<JsonObject>().ToList();
            var chosen = entries.FirstOrDefault(e => string.Equals(Text(e["currency"]), "USD", StringComparison.OrdinalIgnoreCase))
                         ?? entries.FirstOrDefault(e => string.Equals(Text(e["currency"]), "CNY", StringComparison.OrdinalIgnoreCase));
            if (chosen is null || Number(chosen["total_balance"]) is not { } total) continue;

            var unit = string.Equals(Text(chosen["currency"]), "USD", StringComparison.OrdinalIgnoreCase) ? Dollar : Yuan;
            var granted = Number(chosen["granted_balance"]);
            var toppedUp = Number(chosen["topped_up_balance"]);
            readings.Add(Money("deepseek.balance", Loc.T("label.api.balance"), Math.Max(0m, total), unit) with
            {
                Note = granted is not null && toppedUp is not null
                    ? Loc.T("note.deepseek.split", $"{unit} {granted.Value.ToString("0.00", Loc.Culture)}", $"{unit} {toppedUp.Value.ToString("0.00", Loc.Culture)}")
                    : null,
            });
        }

        return readings;
    }

    /// <summary>Teste: o parser sobre a resposta crua, sem rede.</summary>
    internal static List<LimitReading> ParseForTest(string json) =>
        new DeepSeekUsageProvider(new ApiProviderOptions()).Parse([Fetch.Ok(json)], DateTimeOffset.UtcNow);
}
