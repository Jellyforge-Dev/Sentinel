using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;

namespace Jellyfin.Plugin.Sentinel.Localization;

/// <summary>
/// Resolves a translation key into localized text for a given <see cref="SupportedLanguage"/>.
/// Loads and caches each locale's embedded JSON resource once per process lifetime — these are
/// small, fixed, read-only files, not something that changes at runtime.
/// </summary>
public sealed class LocalizationService
{
    private readonly ConcurrentDictionary<SupportedLanguage, IReadOnlyDictionary<string, string>> _cache = new();

    /// <summary>
    /// Translates <paramref name="key"/> into <paramref name="language"/>. Falls back to the key
    /// itself (never throws, never silently returns empty) if the key is unknown - a stale or
    /// renamed rule code must degrade visibly, not crash the caller.
    /// </summary>
    /// <param name="key">The translation key, e.g. <c>"VIDEO_CODEC_UNSUPPORTED_EXPLANATION"</c>.</param>
    /// <param name="language">The language to translate into.</param>
    /// <returns>The translated text, or <paramref name="key"/> itself if not found.</returns>
    public string Translate(string key, SupportedLanguage language)
    {
        ArgumentNullException.ThrowIfNull(key);

        var resource = _cache.GetOrAdd(language, Load);
        return resource.TryGetValue(key, out var value) ? value : key;
    }

    private static IReadOnlyDictionary<string, string> Load(SupportedLanguage language)
    {
        var assembly = typeof(LocalizationService).Assembly;
        var resourceName = $"Jellyfin.Plugin.Sentinel.Localization.Strings.{language.ToLocaleCode()}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
        {
            return new Dictionary<string, string>();
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}
