using Jellyfin.Plugin.Sentinel.Localization;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Localization;

public class SupportedLanguageTests
{
    [Theory]
    [InlineData(SupportedLanguage.En, "en")]
    [InlineData(SupportedLanguage.De, "de")]
    [InlineData(SupportedLanguage.Es, "es")]
    [InlineData(SupportedLanguage.Fr, "fr")]
    [InlineData(SupportedLanguage.Sv, "sv")]
    [InlineData(SupportedLanguage.Da, "da")]
    [InlineData(SupportedLanguage.Pl, "pl")]
    public void ToLocaleCode_ReturnsExpectedTwoLetterCode(SupportedLanguage language, string expected)
    {
        Assert.Equal(expected, language.ToLocaleCode());
    }
}
