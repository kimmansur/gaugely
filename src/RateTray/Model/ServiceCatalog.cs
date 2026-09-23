using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using RateTray.Configuration;

namespace RateTray.Model;

/// <summary>Fork: um registro por serviço; é a única lista de serviços do app.</summary>
public record ServiceRecord(
    string Group,
    string IdPrefix,
    string LogoKey,
    bool HasDarkVariant,
    Func<ColorOptions, string?> ColorSetting,
    Color FallbackColor
);

public static class ServiceCatalog
{
    // Fork: Item 10 - Catálogo único de serviços
    public static readonly IReadOnlyList<ServiceRecord> Services =
    [
        new("Claude", "claude.", "claude", false, c => c.Claude, Color.FromArgb(217, 119, 87)),
        new("Codex", "codex.", "codex", false, c => c.Codex, Color.FromArgb(16, 163, 127)),
        new("Kimi", "kimi.", "kimi", true, c => c.Kimi, Color.FromArgb(96, 125, 255)),
        new("OpenRouter", "openrouter.", "openrouter", true, c => c.OpenRouter, Color.FromArgb(160, 118, 250)),
        new("Antigravity", "antigravity.", "antigravity", false, c => c.Antigravity, Color.FromArgb(66, 133, 244))
    ];

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
