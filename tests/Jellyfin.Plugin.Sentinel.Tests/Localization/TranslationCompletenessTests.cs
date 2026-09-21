using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.Sentinel.Localization;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Localization;

public class TranslationCompletenessTests
{
    private static Dictionary<string, string> LoadResource(string locale)
    {
        var assembly = typeof(SupportedLanguage).Assembly;
        var resourceName = $"Jellyfin.Plugin.Sentinel.Localization.Strings.{locale}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream!) ?? new Dictionary<string, string>();
    }

    public static IEnumerable<object[]> NonEnglishLocales() =>
        new[] { "de", "es", "fr", "sv", "da", "pl" }.Select(l => new object[] { l });

    [Theory]
    [MemberData(nameof(NonEnglishLocales))]
    public void Locale_HasExactlyTheSameKeysAsEnglish(string locale)
    {
        var english = LoadResource("en");
        var translated = LoadResource(locale);

        var missing = english.Keys.Except(translated.Keys).ToList();
        var extra = translated.Keys.Except(english.Keys).ToList();

        Assert.True(missing.Count == 0, $"{locale}.json is missing keys: {string.Join(", ", missing)}");
        Assert.True(extra.Count == 0, $"{locale}.json has keys not present in en.json: {string.Join(", ", extra)}");
    }

    [Theory]
    [MemberData(nameof(NonEnglishLocales))]
    public void Locale_HasNoEmptyValues(string locale)
    {
        var translated = LoadResource(locale);
        var empty = translated.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
        Assert.True(empty.Count == 0, $"{locale}.json has empty translations for: {string.Join(", ", empty)}");
    }
}
