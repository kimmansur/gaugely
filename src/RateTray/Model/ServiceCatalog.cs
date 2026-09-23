using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using RateTray.Configuration;

namespace RateTray.Model;

/// <summary>
/// Fork: um registro por serviço; é a única lista de serviços do app. <see cref="Vendor"/> junta
/// os trilhos de um mesmo fornecedor no mesmo cartão dos ajustes — a assinatura (cota que enche
/// e zera) e a API (gasto, uso e saldo lidos por chave).
/// </summary>
public record ServiceRecord(
    string Group,
    string IdPrefix,
    string LogoKey,
    bool HasDarkVariant,
    Func<ColorOptions, string?> ColorSetting,
    Color FallbackColor,
    string Vendor,
    ServiceTrack Track
);

/// <summary>Fork: assinatura (janelas de cota) ou API (dinheiro e tokens por chave).</summary>
public enum ServiceTrack { Subscription, Api }

public static class ServiceCatalog
{
    // Fork: Item 10 - Catálogo único de serviços
    public static readonly IReadOnlyList<ServiceRecord> Services =
    [
        new("Claude", "claude.", "claude", false, c => c.Claude, Color.FromArgb(217, 119, 87), Vendors.Anthropic, ServiceTrack.Subscription),
        new("Anthropic API", "anthropicapi.", "anthropic", true, c => c.AnthropicApi, Color.FromArgb(193, 95, 60), Vendors.Anthropic, ServiceTrack.Api),
        new("Codex", "codex.", "codex", false, c => c.Codex, Color.FromArgb(16, 163, 127), Vendors.OpenAI, ServiceTrack.Subscription),
        new("OpenAI API", "openaiapi.", "openai", true, c => c.OpenAIApi, Color.FromArgb(25, 195, 125), Vendors.OpenAI, ServiceTrack.Api),
        new("Antigravity", "antigravity.", "antigravity", false, c => c.Antigravity, Color.FromArgb(66, 133, 244), Vendors.Google, ServiceTrack.Subscription),
        new("Kimi", "kimi.", "kimi", true, c => c.Kimi, Color.FromArgb(96, 125, 255), Vendors.Kimi, ServiceTrack.Subscription),
        new("Kimi API", "kimiapi.", "kimi", true, c => c.KimiApi, Color.FromArgb(143, 163, 255), Vendors.Kimi, ServiceTrack.Api),
        new("OpenRouter", "openrouter.", "openrouter", true, c => c.OpenRouter, Color.FromArgb(160, 118, 250), Vendors.OpenRouter, ServiceTrack.Api),
        new("DeepSeek", "deepseek.", "deepseek", false, c => c.DeepSeek, Color.FromArgb(77, 107, 254), Vendors.DeepSeek, ServiceTrack.Api),
    ];

    /// <summary>Fornecedores, na ordem dos cartões nos ajustes.</summary>
    public static IReadOnlyList<string> VendorOrder { get; } =
        [Vendors.Anthropic, Vendors.OpenAI, Vendors.Google, Vendors.Kimi, Vendors.OpenRouter, Vendors.DeepSeek];

    /// <summary>Os serviços de um fornecedor, assinatura primeiro.</summary>
    public static IEnumerable<ServiceRecord> OfVendor(string vendor) =>
        Services.Where(s => s.Vendor == vendor).OrderBy(s => s.Track);

    public static ServiceRecord GetByPrefix(string id) =>
        Services.FirstOrDefault(s => id.StartsWith(s.IdPrefix, StringComparison.OrdinalIgnoreCase)) ?? Services[0]; // Claude é o padrão

    public static ServiceRecord? FindByGroup(string group) =>
        Services.FirstOrDefault(s => s.Group.Equals(group, StringComparison.OrdinalIgnoreCase));

    public static ServiceRecord GetByGroup(string group)
    {
        if (string.Equals(group, "gemini", StringComparison.OrdinalIgnoreCase))
            return Services.First(s => s.Group == "Antigravity");
        return FindByGroup(group) ?? Services[0];
    }
}

/// <summary>Fork: nomes dos fornecedores, usados como chave dos cartões e com o logo do primeiro serviço.</summary>
public static class Vendors
{
    public const string Anthropic = "Claude";
    public const string OpenAI = "OpenAI";
    public const string Google = "Gemini";
    public const string Kimi = "Kimi";
    public const string OpenRouter = "OpenRouter";
    public const string DeepSeek = "DeepSeek";
}
