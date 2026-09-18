using System;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Jellyfin.Plugin.Sentinel.Domain;
using MediaBrowser.Model.Session;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Diagnostics;

public class RuleEngineTests
{
    private readonly RuleEngine _engine = new(CoreTranscodeRules.All);

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
    public void Diagnose_MatchesVideoCodecUnsupported_WithConfirmedConfidence()
    {
        var result = _engine.Diagnose(BuildEvent(PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported));

        Assert.Contains(result, d => d.Code == "VIDEO_CODEC_UNSUPPORTED" && d.Confidence == Confidence.Confirmed);
    }

    [Fact]
    public void Diagnose_MatchesAudioCodecUnsupported()
    {
        var result = _engine.Diagnose(BuildEvent(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported));

        Assert.Contains(result, d => d.Code == "AUDIO_CODEC_UNSUPPORTED");
    }

    [Fact]
    public void Diagnose_MatchesContainerUnsupported()
    {
        var result = _engine.Diagnose(BuildEvent(PlayMethod.Transcode, TranscodeReason.ContainerNotSupported));

        Assert.Contains(result, d => d.Code == "CONTAINER_UNSUPPORTED");
    }

    [Fact]
    public void Diagnose_MatchesSecondaryAudioUnsupported()
    {
        var result = _engine.Diagnose(BuildEvent(PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported));

        Assert.Contains(result, d => d.Code == "SECONDARY_AUDIO_UNSUPPORTED");
    }

    [Fact]
    public void Diagnose_MatchesTooManyStreams()
    {
        var result = _engine.Diagnose(BuildEvent(PlayMethod.Transcode, TranscodeReason.StreamCountExceedsLimit));

        Assert.Contains(result, d => d.Code == "TOO_MANY_STREAMS");
    }

    [Fact]
    public void Diagnose_ReturnsUnknownConfidence_WhenTranscodedWithNoReasonRecorded()
    {
        var result = _engine.Diagnose(BuildEvent(PlayMethod.Transcode, default));

        Assert.Contains(result, d => d.Code == "TRANSCODE_REASON_MISSING" && d.Confidence == Confidence.Unknown);
    }

    [Fact]
    public void Diagnose_MatchesMultipleRules_WhenMultipleFlagsSet()
    {
        var result = _engine.Diagnose(BuildEvent(
            PlayMethod.Transcode,
            TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported));

        Assert.Contains(result, d => d.Code == "VIDEO_CODEC_UNSUPPORTED");
        Assert.Contains(result, d => d.Code == "AUDIO_CODEC_UNSUPPORTED");
    }

    [Fact]
    public void Diagnose_ReturnsNothing_ForDirectPlay()
    {
        var result = _engine.Diagnose(BuildEvent(PlayMethod.DirectPlay, default));

        Assert.Empty(result);
    }

    [Fact]
    public void Diagnose_EveryDiagnosis_HasNonEmptyEvidence()
    {
        var result = _engine.Diagnose(BuildEvent(PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported));

        Assert.All(result, d => Assert.NotEmpty(d.Evidence));
    }
}
