namespace Jellyfin.Plugin.Sentinel.Localization;

/// <summary>
/// A language Sentinel can render its own dashboard and diagnosis text in — independent of
/// Jellyfin's own per-user display language, since Sentinel's diagnoses are admin-facing,
/// server-wide data, not per-viewer content.
/// </summary>
public enum SupportedLanguage
{
    /// <summary>English (reference/fallback language).</summary>
    En,

    /// <summary>German.</summary>
    De,

    /// <summary>Spanish.</summary>
    Es,

    /// <summary>French.</summary>
    Fr,

    /// <summary>Swedish.</summary>
    Sv,

    /// <summary>Danish.</summary>
    Da,

    /// <summary>Polish.</summary>
    Pl
}

/// <summary>
/// Extension methods for <see cref="SupportedLanguage"/>.
/// </summary>
public static class SupportedLanguageExtensions
{
    /// <summary>
    /// Gets the two-letter locale code used as this language's resource file name
    /// (e.g. <c>Localization/Strings/de.json</c>).
    /// </summary>
    /// <param name="language">The language.</param>
    /// <returns>The two-letter lowercase locale code.</returns>
    public static string ToLocaleCode(this SupportedLanguage language) => language switch
    {
        SupportedLanguage.En => "en",
        SupportedLanguage.De => "de",
        SupportedLanguage.Es => "es",
        SupportedLanguage.Fr => "fr",
        SupportedLanguage.Sv => "sv",
        SupportedLanguage.Da => "da",
        SupportedLanguage.Pl => "pl",
        _ => "en"
    };
}
