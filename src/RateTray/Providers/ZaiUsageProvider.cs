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
/// Fork: cota do GLM Coding Plan da Z.ai — a janela de 5 h e a semanal — pelo medidor da própria
/// assinatura. Mesmo molde do <see cref="KimiUsageProvider"/>: chave no Gerenciador de Credenciais,
/// relida a cada consulta, e URL fixa no código.
/// </summary>
public sealed class ZaiUsageProvider(ZaiOptions options) : IUsageProvider
{
    /// <summary>Fixa no código pelo princípio da FORK-1: um settings.json adulterado não tem onde apontar a chave.</summary>
    internal const string UsageUrl = "https://api.z.ai/api/monitor/usage/quota/limit";

    internal const string GroupName = "Z.ai";

    /// <summary>
    /// Códigos de autenticação do medidor. Ele responde HTTP 200 mesmo com chave errada e põe a
    /// recusa no corpo: 1000 = chave não aceita, 1001 = cabeçalho sem chave.
    /// </summary>
    private static readonly int[] AuthCodes = [1000, 1001, 1002, 1003];

    // Fork: sem redirecionamento, com teto de tamanho; prazo por consulta via token vinculado
    private static readonly HttpClient Http = SecureHttp.Create();

    public string Group => GroupName;

    /// <summary>Sem chave no cofre o provedor fica desligado, e não em erro, como no Kimi.</summary>
    public bool Enabled => options.Enabled && CredentialVault.Has(CredentialVault.Zai);

    public TimeSpan MinInterval => TimeSpan.FromSeconds(Math.Max(0, options.MinIntervalSeconds));

    public async Task<ProviderResult> ReadAsync(CancellationToken ct)
    {
        var key = CredentialVault.Read(CredentialVault.Zai);
        if (key is not { Length: > 0 })
        {
            return ProviderResult.Failed(Group, Loc.T("error.api.noKey", GroupName)) with
            {
                Auth = new AuthStatus { Group = Group, IsValid = false, Detail = Loc.T("auth.noKey") },
            };
        }

        var auth = new AuthStatus { Group = Group, IsValid = true, Detail = "GLM Coding Plan" };

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
                return ProviderResult.Failed(Group, Loc.T("error.api.rejected", GroupName)) with { Auth = auth with { IsValid = false } };

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ProviderResult.Failed(Group, Loc.T("error.api.rateLimited", GroupName)) with
                {
                    Auth = auth,
                    RetryAfter = ClaudeUsageProvider.RetryAfterOf(response),
                    RateLimited = true,
                };
            }

            if (!response.IsSuccessStatusCode)
                return ProviderResult.Failed(Group, Loc.T("error.api.http", (int)response.StatusCode, GroupName)) with { Auth = auth };

            var json = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            var readings = Parse(json, out var plan, out var refusal);

            if (refusal is { } code)
            {
                return AuthCodes.Contains(code)
                    ? ProviderResult.Failed(Group, Loc.T("error.api.rejected", GroupName)) with { Auth = auth with { IsValid = false } }
                    : ProviderResult.Failed(Group, Loc.T("error.zai.refused", code)) with { Auth = auth };
            }

            auth = auth with { Detail = plan is { Length: > 0 } ? $"GLM Coding Plan · {plan}" : "GLM Coding Plan" };

            return readings.Count == 0
                ? ProviderResult.Failed(Group, Loc.T("error.zai.noLimits")) with { Auth = auth }
                : ProviderResult.Success(Group, readings) with { Auth = auth };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;                                  // encerrando, não é falha
        }
        catch (OperationCanceledException)
        {
            return ProviderResult.Failed(Group, Loc.T("error.api.timeout", GroupName, options.TimeoutSeconds)) with { Auth = auth };
        }
        catch (Exception ex)
        {
            return ProviderResult.Failed(Group, Loc.T("error.fetchFailed", SecureHttp.Describe(ex))) with { Auth = auth };
        }
    }

    /// <summary>
    /// Monta as leituras a partir de <c>data.limits</c>. Cada limite traz o teto (<c>usage</c>), o
    /// consumido (<c>currentValue</c>), o percentual arredondado, o próximo reset em epoch ms e a
    /// janela como <c>unit</c> + <c>number</c> (3 = horas, 6 = semanas; 5 h e 1 semana no plano Lite).
    ///
    /// Só entram a primeira janela de até um dia e a primeira de uma semana ou mais, com id fixo:
    /// um "zai.5h" que trocasse de sentido conforme a ordem do array quebraria a lista de ícones.
    /// Limites de outro tipo (a cota mensal de ferramentas MCP dos planos antigos) ficam de fora.
    /// </summary>
    /// <param name="refusal">Código do corpo quando <c>success</c> é falso; null quando a resposta é válida.</param>
    internal static List<LimitReading> Parse(string json, out string? plan, out int? refusal)
    {
        plan = null;
        refusal = null;
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

        if (root["success"] is JsonValue ok && ok.GetValueKind() == JsonValueKind.False)
        {
            refusal = (int?)KimiUsageProvider.Number(root["code"]) ?? 0;
            return readings;
        }

        if (root["data"] is not JsonObject data) return readings;

        plan = PlanName(data["level"]);

        if (data["limits"] is not JsonArray limits) return readings;

        LimitReading? shortWindow = null, week = null;
        foreach (var limit in limits.OfType<JsonObject>())
        {
            if (Text(limit["type"]) is not ("CREDIT_LIMIT" or "TOKENS_LIMIT")) continue;
            if (WindowOf(limit) is not { } window || PercentOf(limit) is not { } percent) continue;

            if (window <= TimeSpan.FromDays(1) && shortWindow is null)
            {
                shortWindow = new LimitReading
                {
                    Id = "zai.5h",
                    Label = LimitReading.FormatWindow(window),
                    Group = GroupName,
                    Percent = percent,
                    ResetsAt = ResetOf(limit),
                    Window = window,
                    IsActive = true,
                };
            }
            else if (window >= TimeSpan.FromDays(7) && week is null)
            {
                week = new LimitReading
                {
                    Id = "zai.week",
                    Label = window == TimeSpan.FromDays(7) ? Loc.T("window.week") : LimitReading.FormatWindow(window),
                    Group = GroupName,
                    Percent = percent,
                    ResetsAt = ResetOf(limit),
                    Window = window,
                };
            }
        }

        if (shortWindow is not null) readings.Add(shortWindow);
        if (week is not null) readings.Add(week);

        return readings
            .Select((reading, index) => reading with { Variant = index, VariantCount = readings.Count })
            .ToList();
    }

    /// <summary>"lite" → "Lite". Nível desconhecido aparece com a mesma regra, para não esconder um plano novo.</summary>
    private static string? PlanName(JsonNode? node) =>
        Text(node) is { Length: > 0 } level
            ? char.ToUpperInvariant(level[0]) + level[1..].ToLowerInvariant()
            : null;

    /// <summary>
    /// clamp(100 * currentValue / usage). O campo <c>percentage</c> vem arredondado para inteiro,
    /// então só é usado quando falta o par teto/consumido. Sem teto positivo não há leitura.
    /// </summary>
    private static double? PercentOf(JsonObject limit)
    {
        if (KimiUsageProvider.Number(limit["usage"]) is { } cap && cap > 0)
        {
            if (KimiUsageProvider.Number(limit["currentValue"]) is { } used)
                return Math.Clamp(100.0 * used / cap, 0, 100);
            if (KimiUsageProvider.Number(limit["remaining"]) is { } remaining)
                return Math.Clamp(100.0 * (cap - remaining) / cap, 0, 100);
        }

        return KimiUsageProvider.Number(limit["percentage"]) is { } reported ? Math.Clamp(reported, 0, 100) : null;
    }

    /// <summary>Unidades vistas no medidor: 1 = dias, 3 = horas, 5 = minutos, 6 = semanas. Outra unidade não vira janela.</summary>
    private static TimeSpan? WindowOf(JsonObject limit)
    {
        if (KimiUsageProvider.Number(limit["number"]) is not { } n || n <= 0) return null;

        return KimiUsageProvider.Number(limit["unit"]) switch
        {
            1 => TimeSpan.FromDays(n),
            3 => TimeSpan.FromHours(n),
            5 => TimeSpan.FromMinutes(n),
            6 => TimeSpan.FromDays(7 * n),
            _ => null,
        };
    }

    private static DateTimeOffset? ResetOf(JsonObject limit)
    {
        if (KimiUsageProvider.Number(limit["nextResetTime"]) is not { } epoch || epoch <= 0) return null;

        // Epoch em milissegundos; um valor pequeno demais para isso só pode estar em segundos.
        return epoch > 1e11
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)epoch)
            : DateTimeOffset.FromUnixTimeSeconds((long)epoch);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue<string>(out var text)
            ? text
            : null;
}
