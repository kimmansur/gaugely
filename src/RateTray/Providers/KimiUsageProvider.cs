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
/// Fork: cota do Kimi Code — a janela de 5 h e a semanal — pelo endpoint de uso da
/// própria assinatura. A chave vem do Gerenciador de Credenciais (<see cref="CredentialVault"/>)
/// e é relida a cada consulta, então colar ou remover a chave nos ajustes vale na hora.
/// </summary>
public sealed class KimiUsageProvider(KimiOptions options) : IUsageProvider
{
    /// <summary>
    /// URL fixa no código, sem campo no settings.json: é o mesmo princípio da FORK-1. Um arquivo
    /// de ajustes adulterado não pode apontar a chave para outro host, porque não há onde apontar.
    /// </summary>
    internal const string UsageUrl = "https://api.kimi.com/coding/v1/usages";

    internal const string GroupName = "Kimi";

    /// <summary>
    /// Compartilhado e sem timeout próprio, como no <see cref="ClaudeUsageProvider"/>: o prazo de
    /// cada consulta vem de <see cref="KimiOptions.TimeoutSeconds"/> por um token vinculado.
    /// </summary>
    // Fork: SocketsHttpHandler previne exhaustion e DNS stale mantendo Timeout.InfiniteTimeSpan
    private static readonly HttpClient Http = SecureHttp.Create();   // Fork: sem redirecionamento, com teto de tamanho

    public string Group => GroupName;

    /// <summary>
    /// Sem chave no cofre o provedor fica desligado, e não em erro: quem não usa Kimi não deve
    /// ganhar um ícone de "?" na bandeja. Propriedade, não campo — relida a cada ciclo.
    /// </summary>
    public bool Enabled => options.Enabled && CredentialVault.Has(CredentialVault.Kimi);

    public TimeSpan MinInterval => TimeSpan.FromSeconds(Math.Max(0, options.MinIntervalSeconds));

    public async Task<ProviderResult> ReadAsync(CancellationToken ct)
    {
        var key = CredentialVault.Read(CredentialVault.Kimi);
        if (key is not { Length: > 0 })
        {
            return ProviderResult.Failed(Group, Loc.T("error.kimi.noKey")) with
            {
                Auth = new AuthStatus { Group = Group, IsValid = false, Detail = Loc.T("auth.noKey") },
            };
        }

        var auth = new AuthStatus { Group = Group, IsValid = true, Detail = "Kimi Code" };

        // Redundante com a constante acima, mas barato: se alguém um dia trocar a URL por http,
        // a chave não sai em claro.
        if (!Endpoint.IsSecure(UsageUrl))
            return ProviderResult.Failed(Group, Loc.T("error.insecureUrl", UsageUrl)) with { Auth = auth };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("Gaugely");

            using var response = await Http.SendAsync(request, deadline.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return ProviderResult.Failed(Group, Loc.T("error.kimi.rejected")) with { Auth = auth with { IsValid = false } };

            // 403 não é chave inválida: a chave foi aceita, mas o plano não cobre o endpoint ou a
            // cota acabou. Marcar o login como inválido mandaria o usuário trocar uma chave boa.
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return ProviderResult.Failed(Group, Loc.T("error.kimi.forbidden")) with { Auth = auth };

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ProviderResult.Failed(Group, Loc.T("error.kimi.rateLimited")) with
                {
                    Auth = auth,
                    RetryAfter = ClaudeUsageProvider.RetryAfterOf(response),
                    RateLimited = true,
                };
            }

            if (!response.IsSuccessStatusCode)
                return ProviderResult.Failed(Group, Loc.T("error.kimi.http", (int)response.StatusCode)) with { Auth = auth };

            var json = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            var readings = Parse(json, out var plan);
            auth = auth with { Detail = plan is { Length: > 0 } ? $"Kimi Code · {plan}" : "Kimi Code" };

            return readings.Count == 0
                ? ProviderResult.Failed(Group, Loc.T("error.kimi.noLimits")) with { Auth = auth }
                : ProviderResult.Success(Group, readings) with { Auth = auth };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;                                  // encerrando, não é falha
        }
        catch (OperationCanceledException)
        {
            return ProviderResult.Failed(Group, Loc.T("error.kimi.timeout", options.TimeoutSeconds)) with { Auth = auth };
        }
        catch (Exception ex)
        {
            return ProviderResult.Failed(Group, Loc.T("error.fetchFailed", SecureHttp.Describe(ex))) with { Auth = auth };
        }
    }

    /// <summary>
    /// Monta as leituras a partir da resposta do <c>/usages</c>. Tolerante de propósito: a API
    /// devolve números ora como string ("100"), ora como número; <c>used</c> às vezes falta e sai
    /// de <c>limit - remaining</c>; e o reset já apareceu com quatro grafias.
    /// </summary>
    internal static List<LimitReading> Parse(string json, out string? plan)
    {
        plan = null;
        var readings = new List<LimitReading>();

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return readings;
        }

        if (root is null) return readings;

        plan = PlanName(root["user"]?["membership"]?["level"]);

        // Só a primeira janela curta: é a de 5 h que a assinatura anuncia. Se a API passar a
        // mandar outras, ficam de fora até ganharem id e rótulo próprios — um "kimi.5h" que
        // trocasse de significado conforme a ordem do array quebraria a lista de ícones salva.
        if (root["limits"] is JsonArray limits && limits.OfType<JsonObject>().FirstOrDefault() is { } first
            && first["detail"] is JsonObject detail
            && PercentOf(detail) is { } shortPercent)
        {
            var window = WindowOf(first["window"] as JsonObject);
            readings.Add(new LimitReading
            {
                Id = "kimi.5h",
                Label = window is { } w ? LimitReading.FormatWindow(w) : Loc.T("label.kimi.session"),
                Group = GroupName,
                Percent = shortPercent,
                ResetsAt = ResetOf(detail),
                Window = window ?? TimeSpan.FromHours(5),
                IsActive = true,
            });
        }

        if (root["usage"] is JsonObject usage && PercentOf(usage) is { } weekPercent)
        {
            readings.Add(new LimitReading
            {
                Id = "kimi.week",
                Label = Loc.T("window.week"),
                Group = GroupName,
                Percent = weekPercent,
                ResetsAt = ResetOf(usage),
                Window = TimeSpan.FromDays(7),
            });
        }

        return readings
            .Select((reading, index) => reading with { Variant = index, VariantCount = readings.Count })
            .ToList();
    }

    /// <summary>Nome comercial do plano. Nível desconhecido aparece cru, para não esconder um plano novo.</summary>
    private static string? PlanName(JsonNode? node) => Text(node) switch
    {
        null or "" => null,
        "LEVEL_FREE" => "Adagio",
        "LEVEL_TRIAL" => "Andante",
        "LEVEL_BASIC" => "Moderato",
        "LEVEL_INTERMEDIATE" => "Allegretto",
        "LEVEL_ADVANCED" => "Allegro",
        var other => other,
    };

    /// <summary>
    /// clamp(100 * used / limit). Sem limite positivo não há percentual — devolve null e a
    /// leitura não é criada, em vez de um 0 % que pareceria folga.
    /// </summary>
    private static double? PercentOf(JsonObject node)
    {
        if (Number(node["limit"]) is not { } limit || limit <= 0) return null;

        double used;
        if (Number(node["used"]) is { } reported) used = reported;
        else if (Number(node["remaining"]) is { } remaining) used = limit - remaining;
        else return null;

        return Math.Clamp(100.0 * used / limit, 0, 100);
    }

    private static DateTimeOffset? ResetOf(JsonObject node)
    {
        foreach (var name in (string[])["resetTime", "resetAt", "reset_time", "reset_at"])
        {
            if (node[name] is not { } value) continue;

            if (Text(value) is { Length: > 0 } raw &&
                DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;

            // Epoch em número: segundos, ou milissegundos quando grande demais para segundos.
            if (Number(value) is { } epoch && epoch > 0)
            {
                return epoch > 1e11
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)epoch)
                    : DateTimeOffset.FromUnixTimeSeconds((long)epoch);
            }
        }

        return null;
    }

    private static TimeSpan? WindowOf(JsonObject? window)
    {
        if (window is null || Number(window["duration"]) is not { } duration || duration <= 0) return null;

        return Text(window["timeUnit"]) switch
        {
            "TIME_UNIT_SECOND" => TimeSpan.FromSeconds(duration),
            "TIME_UNIT_MINUTE" => TimeSpan.FromMinutes(duration),
            "TIME_UNIT_HOUR" => TimeSpan.FromHours(duration),
            "TIME_UNIT_DAY" => TimeSpan.FromDays(duration),
            _ => null,
        };
    }

    /// <summary>Número vindo como número JSON ou como string com ponto decimal.</summary>
    internal static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;

        if (value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var number))
            return double.IsFinite(number) ? number : null;

        if (value.GetValueKind() == JsonValueKind.String && value.TryGetValue<string>(out var text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return double.IsFinite(parsed) ? parsed : null;

        return null;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue<string>(out var text)
            ? text
            : null;
}
