using System.Runtime.InteropServices;
using Microsoft.Win32;
using RateTray.Localization;

namespace RateTray.Ui.Settings;

/// <summary>
/// Fork: cores da janela de ajustes. Segue o tema de <b>aplicativos</b> do Windows
/// (<c>AppsUseLightTheme</c>) quando o ajuste é "auto" — não o da barra de tarefas, que é o que
/// decide a cor dos ícones da bandeja e pode ser diferente.
/// </summary>
public sealed class SettingsTheme
{
    public bool Dark { get; }

    public Color Window { get; }
    public Color Sidebar { get; }
    public Color Surface { get; }
    public Color SurfaceAlt { get; }
    public Color Border { get; }
    public Color Text { get; }
    public Color SubText { get; }
    public Color Muted { get; }
    public Color Accent { get; }
    public Color AccentText { get; }
    public Color ChipBack { get; }
    public Color ChipText { get; }
    public Color Selected { get; }
    public Color Input { get; }
    public Color Good { get; }
    public Color Bad { get; }

    private SettingsTheme(bool dark)
    {
        Dark = dark;
        if (dark)
        {
            Window = Color.FromArgb(32, 32, 32);
            Sidebar = Color.FromArgb(28, 28, 28);
            Surface = Color.FromArgb(43, 43, 43);
            SurfaceAlt = Color.FromArgb(37, 37, 37);
            Border = Color.FromArgb(58, 58, 58);
            Text = Color.FromArgb(235, 235, 235);
            SubText = Color.FromArgb(190, 190, 190);
            Muted = Color.FromArgb(145, 145, 145);
            Accent = Color.FromArgb(76, 194, 255);
            AccentText = Color.FromArgb(11, 26, 36);
            ChipBack = Color.FromArgb(31, 42, 51);
            ChipText = Color.FromArgb(159, 216, 255);
            Selected = Color.FromArgb(45, 45, 45);
            Input = Color.FromArgb(28, 28, 28);
            Good = Color.FromArgb(91, 208, 111);
            Bad = Color.FromArgb(229, 83, 75);
        }
        else
        {
            Window = Color.FromArgb(243, 243, 243);
            Sidebar = Color.FromArgb(235, 235, 235);
            Surface = Color.FromArgb(251, 251, 251);
            SurfaceAlt = Color.FromArgb(245, 245, 245);
            Border = Color.FromArgb(222, 222, 222);
            Text = Color.FromArgb(27, 27, 27);
            SubText = Color.FromArgb(70, 70, 70);
            Muted = Color.FromArgb(112, 112, 112);
            Accent = Color.FromArgb(0, 95, 184);
            AccentText = Color.White;
            ChipBack = Color.FromArgb(222, 236, 249);
            ChipText = Color.FromArgb(0, 74, 143);
            Selected = Color.FromArgb(225, 225, 225);
            Input = Color.White;
            Good = Color.FromArgb(15, 123, 15);
            Bad = Color.FromArgb(196, 43, 28);
        }
    }

    /// <param name="theme">"auto", "light" ou "dark", como no settings.json.</param>
    public static SettingsTheme For(string theme) => new(theme.ToLowerInvariant() switch
    {
        "light" => false,
        "dark" => true,
        _ => AppsUseDarkTheme(),
    });

    private static bool AppsUseDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ janela

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    /// <summary>
    /// Barra de título escura e cantos arredondados no Windows 11. No Windows 10 as chamadas
    /// simplesmente falham e a janela fica com a moldura padrão — nada quebra.
    /// </summary>
    public void ApplyTo(Form form)
    {
        form.BackColor = Window;
        form.ForeColor = Text;
        if (!form.IsHandleCreated) return;

        var dark = Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        var round = DwmwcpRound;
        _ = DwmSetWindowAttribute(form.Handle, DwmwaWindowCornerPreference, ref round, sizeof(int));
    }

    /// <summary>Fonte da interface: Segoe UI Variable no Windows 11, Segoe UI antes dele.</summary>
    public static Font UiFont(float size, FontStyle style = FontStyle.Regular)
    {
        foreach (var family in new[] { "Segoe UI Variable Text", "Segoe UI" })
        {
            try
            {
                var font = new Font(family, size, style, GraphicsUnit.Point);
                if (font.Name == family) return font;
                font.Dispose();
            }
            catch (ArgumentException) { }
        }

        return new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, size, style);
    }

    /// <summary>Árabe e outros idiomas da direita para a esquerda.</summary>
    public static bool RightToLeft => Loc.IsRightToLeft;
}
