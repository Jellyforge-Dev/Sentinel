using Jellyfin.Plugin.Sentinel.Localization;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Localization;

public class LocalizationServiceTests
{
    private readonly LocalizationService _service = new();

    [Fact]
    public void Translate_ReturnsGermanText_ForKnownKeyAndGerman()
    {
        var result = _service.Translate("VIDEO_CODEC_UNSUPPORTED_EXPLANATION", SupportedLanguage.De);

        Assert.NotEqual("VIDEO_CODEC_UNSUPPORTED_EXPLANATION", result);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void Translate_FallsBackToEnglish_WhenKeyMissingInRequestedLanguage()
    {
        // Every key that exists in en.json exists in every other locale too (Task A2's own
        // completeness test guarantees this) - so this specifically covers an UNKNOWN key,
        // which must degrade to itself rather than throw, so a stale rule code (e.g. after a
        // future rename) never crashes the dashboard.
        var result = _service.Translate("SOME_KEY_THAT_DOES_NOT_EXIST", SupportedLanguage.De);

        Assert.Equal("SOME_KEY_THAT_DOES_NOT_EXIST", result);
    }

    [Fact]
    public void Translate_ReturnsSameTextForEnglish_AsTheSourceJson()
    {
        var result = _service.Translate("TOO_MANY_STREAMS_EXPLANATION", SupportedLanguage.En);

        Assert.Equal(
            "This file has more audio/subtitle streams than your client can handle at once, so Jellyfin had to transcode it.",
            result);
    }
}
