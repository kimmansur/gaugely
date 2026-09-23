using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;

namespace RateTray.Ui.Settings;

/// <summary>
/// Fork: prova visual da janela de ajustes. Para cada idioma, abre a janela fora da tela com dados
/// de exemplo e grava, por página, a janela no tamanho padrão e o conteúdo inteiro da página.
/// Nenhuma leitura real é feita e nada é salvo; só o "Chave salva" reflete o cofre da máquina.
/// </summary>
internal static class SettingsRender
{
    public static int Run(string directory, string? onlyLanguage, string theme)
    {
        Directory.CreateDirectory(directory);
        foreach (var language in Loc.Available)
        {
            if (onlyLanguage is not null && onlyLanguage != "all" && !language.Equals(onlyLanguage, StringComparison.OrdinalIgnoreCase))
                continue;

            Loc.Use(language);
            var config = new AppConfig { Theme = theme, Language = language };
            var results = Sample();
            config.Icons = results.SelectMany(r => r.Readings).Where(r => !r.IsInformational).Select(r => r.Id).ToList();

            using var form = new SettingsWindow(config, results.SelectMany(r => r.Readings).ToList(), results)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-6000, 0),
                ShowInTaskbar = false,
            };
            form.Show();
            Pump();

            for (var page = 0; page < form.PageCount; page++)
            {
                form.ShowPage(page);
                Pump();

                using (var window = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(window, new Rectangle(Point.Empty, form.Size));
                    window.Save(Path.Combine(directory, $"{language}-{page}-janela.png"));
                }

                if (form.CurrentContent is { Width: > 0, Height: > 0 } content)
                {
                    using var full = new Bitmap(content.Width, content.Height);
                    content.DrawToBitmap(full, new Rectangle(Point.Empty, content.Size));
                    full.Save(Path.Combine(directory, $"{language}-{page}-pagina.png"));
                }
            }

            form.Close();
        }

        return 0;
    }

    private static void Pump()
    {
        for (var i = 0; i < 8; i++)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    private static List<ProviderResult> Sample()
    {
        var now = DateTimeOffset.Now;
        LimitReading Quota(string id, string group, string label, double percent, double hours, int variant = 0, int count = 1) => new()
        {
            Id = id, Group = group, Label = label, Percent = percent, ResetsAt = now.AddHours(hours),
            Variant = variant, VariantCount = count,
        };
        LimitReading Money(string id, string group, string label, decimal amount, string? note = null) => new()
        {
            Id = id, Group = group, Label = label, Amount = amount, AmountUnit = "US$", Note = note,
        };

        return
        [
            ProviderResult.Success("Claude",
            [
                Quota("claude.session", "Claude", Loc.T("label.claude.session"), 42, 3, 0, 2),
                Quota("claude.weekly", "Claude", Loc.T("label.claude.weeklyAll"), 61, 90, 1, 2),
            ]),
            ProviderResult.Success("Anthropic API",
            [
                Money("anthropicapi.spend_month", "Anthropic API", Loc.T("label.api.spendMonth"), 48.12m, Loc.T("note.api.tokens", "3.4 M")),
                Money("anthropicapi.spend_today", "Anthropic API", Loc.T("label.api.spendToday"), 2.76m),
            ]),
            ProviderResult.Success("Codex", [Quota("codex.primary", "Codex", Loc.T("label.codex.window", "5 h"), 18, 2)]),
            ProviderResult.Failed("OpenAI API", Loc.T("error.api.noKey", "OpenAI")),
            ProviderResult.Success("Antigravity", [Quota("antigravity.pro", "Antigravity", "Gemini 3 Pro", 7, 20)]),
            ProviderResult.Success("Kimi", [Quota("kimi.session", "Kimi", Loc.T("label.kimi.session"), 33, 4)]),
            ProviderResult.Success("OpenRouter",
            [
                Money("openrouter.balance", "OpenRouter", Loc.T("label.openrouter.balance"), 12.40m),
                Money("openrouter.spend_month", "OpenRouter", Loc.T("label.openrouter.spendMonth"), 7.85m),
            ]),
            ProviderResult.Success("DeepSeek", [Money("deepseek.balance", "DeepSeek", Loc.T("label.api.balance"), 3.20m, Loc.T("note.deepseek.split", "0.00", "3.20"))]),
        ];
    }
}
