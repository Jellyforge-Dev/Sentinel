using System;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Jellyfin.Plugin.Sentinel.Domain;
using MediaBrowser.Model.Session;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Diagnostics;

public class AdvancedTranscodeRulesTests
{
    private readonly RuleEngine _engine = new(AdvancedTranscodeRules.All);

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
        // This is the exact real-world case confirmed on a live Jellyfin 12.1 server: a
        // bitrate-driven AV1 transcode that also downmixed audio to 2 channels produced
        // TranscodeReasons = AudioChannelsNotSupported with no matching rule at the time.
        var result = _engine.Diagnose(BuildEvent(PlayMethod.Transcode, TranscodeReason.AudioChannelsNotSupported));

        Assert.Contains(result, d => d.Code == "AUDIO_CHANNEL_DOWNMIX" && d.Confidence == Confidence.Possible);
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

    [Fact]
    public void Diagnose_ReturnsNothing_ForDirectPlay()
    {
        var result = _engine.Diagnose(BuildEvent(PlayMethod.DirectPlay, default));

        Assert.Empty(result);
    }
}
