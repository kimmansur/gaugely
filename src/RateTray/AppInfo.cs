using System.Reflection;

namespace RateTray;

/// <summary>
/// Static facts about this build — version, links, copyright — read from the assembly so they
/// track <c>&lt;Version&gt;</c> in the csproj rather than being duplicated here.
/// </summary>
public static class AppInfo
{
    /// <summary>Projeto original (RateTray), mantido no Sobre para crédito — não é de onde saem as versões.</summary>
    public const string UpstreamUrl = "https://github.com/nowrap/rate-tray";

    // Fork: as versões saem deste repositório, não do upstream — é ele que tem o modo notch, os
    // provedores extras e o resto. O repositório é público, então a API responde sem credencial.
    private const string Repo = "kimmansur/gaugely";

    public const string RepoUrl = "https://github.com/" + Repo;
    public const string ReleasesUrl = RepoUrl + "/releases";
    public const string IssuesUrl = RepoUrl + "/issues";
    public const string ApiTagsUrl = "https://api.github.com/repos/" + Repo + "/tags";
    public const string ApiLatestReleaseUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";

    /// <summary>Todo asset instalável tem de estar sob este prefixo — o resto é descartado.</summary>
    public const string ApiAssetsPrefix = "https://api.github.com/repos/" + Repo + "/releases/assets/";

    /// <summary>Display version, e.g. "0.2.0" — build metadata after a '+' is trimmed.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>Normalised to Major.Minor.Build so it compares cleanly against a release tag.</summary>
    public static System.Version SemVer { get; } =
        Normalize(System.Version.TryParse(Version, out var v) ? v : new System.Version(0, 0, 0));

    public static string Copyright { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    /// <summary>Drops the fourth (Revision) component and floors the third, so "0.2.0" (Revision -1)
    /// and "0.2.0.0" from a tag do not compare as different versions.</summary>
    public static System.Version Normalize(System.Version value) =>
        new(value.Major, value.Minor, Math.Max(0, value.Build));

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
