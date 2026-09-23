using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;

namespace RateTray.Providers;

/// <summary>
/// Fork: trilho de API da Kimi — saldo da plataforma (<c>/v1/users/me/balance</c>). A chave é a
/// da <b>plataforma</b>; a do Kimi Code, que alimenta a assinatura, é recusada aqui com 401
/// (medido em 23/09/2026). Valores em US$: disponível = dinheiro + voucher; o dinheiro pode ficar
/// negativo quando há dívida.
/// </summary>
public sealed class KimiApiUsageProvider(ApiProviderOptions options) : ApiKeyProvider(options)
{
    internal const string GroupName = "Kimi API";

    public override string Group => GroupName;

    protected override string VaultService => CredentialVault.KimiPlatform;

    protected override string Vendor => "Kimi";

    protected override string BaseUrl => "https://api.moonshot.ai/v1";

    protected override void Authorize(HttpRequestMessage request, string key) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

    protected override async Task<IReadOnlyList<Fetch>> FetchAllAsync(Func<string, Task<Fetch>> get) =>
        [await get("/users/me/balance").ConfigureAwait(false)];

    protected override List<LimitReading> Parse(IReadOnlyList<Fetch> fetches, DateTimeOffset now)
    {
        var readings = new List<LimitReading>();
        foreach (var fetch in fetches)
        {
            if (ParseJson(fetch.Json) is not JsonObject body || body["data"] is not JsonObject data) continue;
            if (Number(data["available_balance"]) is not { } available) continue;

            var voucher = Number(data["voucher_balance"]);
            var cash = Number(data["cash_balance"]);
            readings.Add(Money("kimiapi.balance", Loc.T("label.api.balance"), Math.Max(0m, available)) with
            {
                Note = voucher is not null && cash is not null
                    ? Loc.T("note.kimiapi.split", Format(voucher.Value), Format(cash.Value))
                    : null,
            });
        }

        return readings;
    }

    private static string Format(decimal value) => $"{Dollar} {value.ToString("0.00", Loc.Culture)}";

    /// <summary>Teste: o parser sobre a resposta crua, sem rede.</summary>
    internal static List<LimitReading> ParseForTest(string json) =>
        new KimiApiUsageProvider(new ApiProviderOptions()).Parse([Fetch.Ok(json)], DateTimeOffset.UtcNow);
}
