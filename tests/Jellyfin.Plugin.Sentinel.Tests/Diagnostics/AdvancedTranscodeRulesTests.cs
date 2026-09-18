using System;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Jellyfin.Plugin.Sentinel.Domain;
using MediaBrowser.Model.Session;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Diagnostics;

public class AdvancedTranscodeRulesTests
{
    private readonly RuleEngine _engine = new(AdvancedTranscodeRules.All);

    // The actual registered production configuration (see AllDiagnosticRules.cs, which
    // PluginServiceRegistrator.cs also consumes — a single shared source, not a second copy of
    // the same Concat call, so this can never silently drift from production) — a test using
    // only AdvancedTranscodeRules.All in isolation can't catch a rule from this set co-firing
    // alongside a CoreTranscodeRules rule for the same event.
    private readonly RuleEngine _combinedEngine = new(AllDiagnosticRules.All);

    private static PlaybackEvent BuildEvent(PlayMethod? playMethod, TranscodeReason reasons) => new()
    {
        SessionId = "s1",
        ItemId = "i1",
        Client = "Fire TV",
        DeviceName = "Living Room",
        PlayMethod = playMethod,
        TranscodeReasons = reasons,
        CreatedAtUtc = DateTime.UtcNow
    };

    [Fact]
    public void Diagnose_MatchesExternalAudioForcedTranscode_WithLikelyConfidence()
    {
        var result = _engine.Diagnose(BuildEvent(PlayMethod.Transcode, TranscodeReason.AudioIsExternal));

        Assert.Contains(result, d => d.Code == "EXTERNAL_AUDIO_FORCED_TRANSCODE" && d.Confidence == Confidence.Likely);
    }

    [Fact]
    public void Diagnose_MatchesHdrToneMappingTranscode_WithLikelyConfidence()
    {
        var result = _engine.Diagnose(BuildEvent(PlayMethod.Transcode, TranscodeReason.VideoRangeTypeNotSupported));

        Assert.Contains(result, d => d.Code == "HDR_TONE_MAPPING_TRANSCODE" && d.Confidence == Confidence.Likely);
    }

    [Fact]
    public void Diagnose_MatchesAudioChannelDownmix_WithPossibleConfidence()
    {
        // This is the exact real-world case confirmed on a live Jellyfin 12.1 server: an AV1
        // transcode produced TranscodeReasons = AudioChannelsNotSupported (and nothing else)
        // with no matching rule at the time.
        var result = _engine.Diagnose(BuildEvent(PlayMethod.Transcode, TranscodeReason.AudioChannelsNotSupported));

        Assert.Contains(result, d => d.Code == "AUDIO_CHANNEL_DOWNMIX" && d.Confidence == Confidence.Possible);
    }

    [Fact]
    public void Diagnose_DoesNotMatchAudioChannelDownmix_WhenAnotherReasonIsAlsoPresent()
    {
        // Master plan item #16 specifically says "with no other reason" — a video-codec
        // mismatch happening at the same time as an audio-channel mismatch should surface only
        // the stronger, more specific CoreTranscodeRules diagnosis, not also this weaker one.
        // Uses the combined production rule set, not AdvancedTranscodeRules in isolation, so this
        // also proves the stronger diagnosis actually fires instead of the two silently canceling
        // each other out.
        var result = _combinedEngine.Diagnose(BuildEvent(
            PlayMethod.Transcode,
            TranscodeReason.AudioChannelsNotSupported | TranscodeReason.VideoCodecNotSupported));

        Assert.Contains(result, d => d.Code == "VIDEO_CODEC_UNSUPPORTED");
        Assert.DoesNotContain(result, d => d.Code == "AUDIO_CHANNEL_DOWNMIX");
    }

    [Fact]
    public void Diagnose_MatchesBitrateCapExceeded_WhenOnlyBitrateReasonsPresent()
    {
        var result = _engine.Diagnose(BuildEvent(PlayMethod.Transcode, TranscodeReason.VideoBitrateNotSupported));

        Assert.Contains(result, d => d.Code == "BITRATE_CAP_EXCEEDED" && d.Confidence == Confidence.Possible);
    }

    [Fact]
    public void Diagnose_DoesNotMatchBitrateCapExceeded_WhenACodecReasonIsAlsoPresent()
    {
        // BITRATE_CAP_EXCEEDED is specifically for "no codec-mismatch reason present" (master
        // plan item #8) — a bitrate reason alongside a codec reason should not also fire this
        // weaker, bitrate-only inference.
        var result = _engine.Diagnose(BuildEvent(
            PlayMethod.Transcode,
            TranscodeReason.VideoBitrateNotSupported | TranscodeReason.VideoCodecNotSupported));

        Assert.DoesNotContain(result, d => d.Code == "BITRATE_CAP_EXCEEDED");
    }

    [Fact]
    public void Diagnose_MatchesResolutionDownscaleOnly_WhenOnlyResolutionAndBitrateReasonsPresent()
    {
        var result = _engine.Diagnose(BuildEvent(
            PlayMethod.Transcode,
            TranscodeReason.VideoResolutionNotSupported | TranscodeReason.ContainerBitrateExceedsLimit));

        Assert.Contains(result, d => d.Code == "RESOLUTION_DOWNSCALE_ONLY" && d.Confidence == Confidence.Likely);
    }

    [Fact]
    public void Diagnose_DoesNotMatchResolutionDownscaleOnly_WhenNoResolutionReasonPresent()
    {
        // A pure bitrate case (no VideoResolutionNotSupported) belongs to BITRATE_CAP_EXCEEDED
        // instead, not this rule.
        var result = _engine.Diagnose(BuildEvent(PlayMethod.Transcode, TranscodeReason.ContainerBitrateExceedsLimit));

        Assert.DoesNotContain(result, d => d.Code == "RESOLUTION_DOWNSCALE_ONLY");
    }

    [Fact]
    public void Diagnose_DoesNotMatchResolutionDownscaleOnly_WhenACodecReasonIsAlsoPresent()
    {
        var result = _engine.Diagnose(BuildEvent(
            PlayMethod.Transcode,
            TranscodeReason.VideoResolutionNotSupported | TranscodeReason.VideoCodecNotSupported));

        Assert.DoesNotContain(result, d => d.Code == "RESOLUTION_DOWNSCALE_ONLY");
    }

    [Theory]
    [InlineData(TranscodeReason.AudioIsExternal)] // would trigger EXTERNAL_AUDIO_FORCED_TRANSCODE
    [InlineData(TranscodeReason.VideoRangeTypeNotSupported)] // would trigger HDR_TONE_MAPPING_TRANSCODE
    [InlineData(TranscodeReason.AudioChannelsNotSupported)] // would trigger AUDIO_CHANNEL_DOWNMIX
    [InlineData(TranscodeReason.VideoBitrateNotSupported)] // would trigger BITRATE_CAP_EXCEEDED
    [InlineData(TranscodeReason.VideoResolutionNotSupported | TranscodeReason.ContainerBitrateExceedsLimit)] // would trigger RESOLUTION_DOWNSCALE_ONLY
    public void Diagnose_ReturnsNothing_WhenNotTranscoding(TranscodeReason reasons)
    {
        // Regression test for a review finding, itself a fix to an earlier regression test: a
        // single shared reasons value (deliberately combining several unrelated flags) satisfied
        // only two of these five rules' OWN HasFlag/equality/mask conditions independent of any
        // PlayMethod == Transcode gate — for the other three, the gate was never actually the
        // thing making the test pass, so deleting only THEIR gates would have gone undetected.
        // Each value above is chosen to satisfy exactly one rule's non-gate condition, so for
        // every rule there is a covered case where its own gate is the only thing standing
        // between this input and a false diagnosis. Real, non-zero TranscodeReasons matters
        // because Jellyfin also populates non-zero TranscodeReasons for DirectStream (remux)
        // sessions — see PlaybackEventFactory's own DirectStream handling — so a session that
        // only remuxed must never be diagnosed as if it had transcoded.
        Assert.Empty(_engine.Diagnose(BuildEvent(PlayMethod.DirectStream, reasons)));
        Assert.Empty(_engine.Diagnose(BuildEvent(PlayMethod.DirectPlay, reasons)));
        Assert.Empty(_engine.Diagnose(BuildEvent(null, reasons)));
    }

    [Fact]
    public void CombinedProductionRuleSet_FiresOnlyTheConfirmedCoreRule_WhenACodecReasonAccompaniesABitrateReason()
    {
        // Verifies the actual registered production configuration (see AllDiagnosticRules.cs),
        // not AdvancedTranscodeRules in isolation. Confirms the masked exclusions in
        // BITRATE_CAP_EXCEEDED and RESOLUTION_DOWNSCALE_ONLY genuinely prevent either from
        // co-firing next to a real CoreTranscodeRules match, which is what "no codec or format
        // mismatch was involved" in their own explanation text depends on. Includes
        // VideoResolutionNotSupported specifically so the RESOLUTION_DOWNSCALE_ONLY assertion is
        // non-vacuous — without it, that rule's own HasFlag check would already exclude it
        // regardless of whether its mask exclusion works at all.
        var result = _combinedEngine.Diagnose(BuildEvent(
            PlayMethod.Transcode,
            TranscodeReason.VideoCodecNotSupported | TranscodeReason.VideoBitrateNotSupported | TranscodeReason.VideoResolutionNotSupported));

        Assert.Contains(result, d => d.Code == "VIDEO_CODEC_UNSUPPORTED");
        Assert.DoesNotContain(result, d => d.Code == "BITRATE_CAP_EXCEEDED");
        Assert.DoesNotContain(result, d => d.Code == "RESOLUTION_DOWNSCALE_ONLY");
    }
}
