using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;

namespace RateTray.Ui.Settings;

/// <summary>
/// Fork: prova visual da janela de ajustes. Para cada idioma, abre a janela fora da tela com dados
/// de exemplo e grava, por página, a janela no tamanho padrão e o conteúdo inteiro da página.
/// Nenhuma leitura real é feita, nada é salvo e o cofre não é consultado: toda chave aparece
/// como ausente.
/// </summary>
internal static class SettingsRender
{
    public static int Run(string directory, string? onlyLanguage, string theme, string? dial = null)
    {
        if (onlyLanguage is not null && onlyLanguage != "all" && !Loc.Available.Contains(onlyLanguage, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"unknown language '{onlyLanguage}'; available: {string.Join(", ", Loc.Available)}, all");
            return 2;
        }

        if (theme is not ("dark" or "light" or "auto"))
        {
            Console.Error.WriteLine($"unknown theme '{theme}'; use dark, light or auto");
            return 2;
        }

        Directory.CreateDirectory(directory);
        var written = 0;
        SettingsWindow.HasKey = _ => false;          // nada do cofre desta máquina entra na imagem
        foreach (var language in Loc.Available)
        {
            if (onlyLanguage is not null && onlyLanguage != "all" && !language.Equals(onlyLanguage, StringComparison.OrdinalIgnoreCase))
                continue;

            Loc.Use(language);
            var config = new AppConfig { Theme = theme, Language = language };
            if (dial is not null) config.Widget.Dial = Dial.Name(Dial.Parse(dial));
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

                // Só a área útil: a moldura e a barra de título não se desenham fora da tela e
                // sairiam como faixas brancas.
                using (var window = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(window, new Rectangle(Point.Empty, form.Size));
                    var origin = form.PointToScreen(Point.Empty);
                    var client = new Rectangle(origin.X - form.Left, origin.Y - form.Top, form.ClientSize.Width, form.ClientSize.Height);
                    client.Intersect(new Rectangle(Point.Empty, window.Size));
                    using var cropped = window.Clone(client, window.PixelFormat);
                    cropped.Save(Path.Combine(directory, $"{language}-{page}-janela.png"));
                    written++;
                }

                if (form.CurrentContent is { Width: > 0, Height: > 0 } content)
                {
                    using var full = new Bitmap(content.Width, content.Height);
                    content.DrawToBitmap(full, new Rectangle(Point.Empty, content.Size));
                    full.Save(Path.Combine(directory, $"{language}-{page}-pagina.png"));
                    written++;
                }
            }

            form.Close();
        }

        Console.WriteLine($"{written} PNG files in {Path.GetFullPath(directory)}");
        return written > 0 ? 0 : 1;
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
