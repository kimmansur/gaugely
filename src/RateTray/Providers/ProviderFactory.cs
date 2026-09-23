using RateTray.Configuration;

namespace RateTray.Providers;

/// <summary>
/// Fork: uma lista só dos provedores, usada pelo ciclo da bandeja e pelo botão "Testar" dos
/// ajustes. Cada provedor recebe o objeto de opções do <paramref name="config"/> passado — o ciclo
/// passa o que está valendo; o "Testar" passa a cópia em edição.
/// </summary>
public static class ProviderFactory
{
    public static List<IUsageProvider> All(AppConfig config) =>
    [
        new ClaudeUsageProvider(config.Claude),
        new CodexUsageProvider(config.Codex),
        new KimiUsageProvider(config.Kimi),
        new OpenRouterUsageProvider(config.OpenRouter),
        new AntigravityUsageProvider(config.Antigravity),
        new AnthropicApiUsageProvider(config.AnthropicApi),
        new OpenAIApiUsageProvider(config.OpenAIApi),
        new KimiApiUsageProvider(config.KimiApi),
        new DeepSeekUsageProvider(config.DeepSeek),
    ];

    public static IUsageProvider? For(string group, AppConfig config) =>
        All(config).FirstOrDefault(p => p.Group.Equals(group, StringComparison.OrdinalIgnoreCase));
}
