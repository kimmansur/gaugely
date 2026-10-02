using System.Drawing.Drawing2D;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Ui.Settings;

namespace RateTray.Ui;

/// <summary>
/// Fork: prova visual da faixa flutuante, para o site e a documentação. Desenha a faixa com os
/// dados de exemplo de <see cref="SettingsRender"/> sobre um fundo neutro que faz o papel da
/// borda da tela, e grava em PNG. Nenhuma leitura real é feita e nada é salvo.
/// </summary>
internal static class WidgetRender
{
    private static readonly string[] Shown = ["Claude", "Codex", "Antigravity", "Kimi", "OpenRouter"];

    public static int Run(string file, string mode, string edge, string theme, string? dial, double scale)
    {
        var notch = mode switch
        {
            "notch" => true,
            "floating" => false,
            _ => (bool?)null,
        };
        var borda = edge switch
        {
            "left" => BordaTela.Esquerda,
            "right" => BordaTela.Direita,
            "top" => BordaTela.Topo,
            "bottom" => BordaTela.Base,
            _ => (BordaTela?)null,
        };
        if (notch is null || borda is null || theme is not ("dark" or "light"))
        {
            Console.Error.WriteLine("usage: Gaugely.exe --render-widget <file.png> [notch|floating] [left|right|top|bottom] [dark|light] [gauge|semicircle|segmented]");
            return 2;
        }

        var config = new AppConfig { Theme = theme };
        config.Widget.Modo = notch.Value ? ModoApresentacao.Notch : ModoApresentacao.Flutuante;
        config.Widget.Borda = borda.Value;
        config.Widget.FracaoBorda = 0.5;
        config.Widget.Scale = Math.Clamp(scale, 0.5, 4);
        config.Widget.MostrarAlca = notch.Value;
        if (dial is not null) config.Widget.Dial = Dial.Name(Dial.Parse(dial));

        Loc.Use("en");
        var groups = SettingsRender.Sample()
            .Where(r => r.Ok && Shown.Contains(r.Group))
            .Select(r => new WidgetGroup(r.Group, r.Readings, r.Error))
            .ToList();

        using var form = new WidgetForm(config, new Palette(config));
        form.Apply(groups);
        form.CreateControl();

        using var strip = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(strip, new Rectangle(Point.Empty, form.Size));

        // A borda da tela é a borda da imagem: a faixa encosta nela, e o resto é "área de trabalho".
        var gap = (int)Math.Round(56 * config.Widget.Scale);
        var canvas = new Size(form.Width + gap * 4, form.Height + gap * 2);
        var at = borda switch
        {
            BordaTela.Esquerda => new Point(0, (canvas.Height - form.Height) / 2),
            BordaTela.Direita => new Point(canvas.Width - form.Width, (canvas.Height - form.Height) / 2),
            BordaTela.Topo => new Point((canvas.Width - form.Width) / 2, 0),
            _ => new Point((canvas.Width - form.Width) / 2, canvas.Height - form.Height),
        };
        if (!notch.Value)
            at = new Point(canvas.Width - form.Width - gap / 2, canvas.Height - form.Height - gap / 2);

        using var image = new Bitmap(canvas.Width, canvas.Height);
        using (var g = Graphics.FromImage(image))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var dark = theme == "dark";
            using (var desktop = new LinearGradientBrush(new Rectangle(Point.Empty, canvas),
                       dark ? Color.FromArgb(38, 52, 74) : Color.FromArgb(196, 214, 236),
                       dark ? Color.FromArgb(20, 24, 33) : Color.FromArgb(232, 238, 246), 35f))
            {
                desktop.WrapMode = WrapMode.TileFlipXY;    // sem isso o gradiente deixa um fio claro na borda
                g.FillRectangle(desktop, new Rectangle(Point.Empty, canvas));
            }

            // Recorta a faixa pelo mesmo contorno que o Windows aplica como Region na janela de verdade.
            using var shape = notch.Value
                ? WidgetForm.NotchShape(new Rectangle(0, 0, form.Width, form.Height), (int)Math.Round(14 * config.Widget.Scale), borda.Value)
                : WidgetForm.Rounded(new Rectangle(0, 0, form.Width, form.Height), (int)Math.Round(14 * config.Widget.Scale));
            using var move = new Matrix();
            move.Translate(at.X, at.Y);
            shape.Transform(move);
            g.SetClip(shape);
            g.DrawImage(strip, at);
        }

        image.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"{Path.GetFullPath(file)} {image.Width}x{image.Height}");
        return 0;
    }
}
