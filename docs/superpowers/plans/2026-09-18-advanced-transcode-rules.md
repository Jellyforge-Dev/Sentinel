# Advanced Transcode Rules & Known-Issue Surfacing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the highest-value, lowest-risk gap between what `JELLYFIN_SENTINEL_MASTER_PLAN.md` Section 14 specifies and what the rule engine currently implements — five new diagnoses that need zero new data collection, plus wiring the already-built-but-dormant Known Core Issues table into the actual API/dashboard.

**Architecture:** Two independent, additive changes to the existing rule-engine/dashboard pipeline. No changes to `PlaybackEvent`, `PlaybackEventFactory`, or the collector — every new rule reads fields the event already carries.

**Tech Stack:** C#/.NET 10, xUnit/Moq (existing test stack), vanilla JS (existing dashboard).

**Spec:** [`JELLYFIN_SENTINEL_MASTER_PLAN.md`](../../../JELLYFIN_SENTINEL_MASTER_PLAN.md), Section 14 ("Rule Engine", items 5, 8, 12, 13, 16, and the "Known Core Issues knowledge base" paragraph).

## Global Constraints

- Confidence values must match the master plan's own assignment per item exactly (see item numbers in each task) — do not upgrade/downgrade a confidence level based on "this looks like a direct flag match" reasoning; the plan's authors already made that call deliberately (see Section 16's own admission that not every direct flag match is `Confirmed`).
- No new fields on `PlaybackEvent`/`PlaybackProgressSnapshot` in this plan. Every predicate below reads only `PlayMethod` and `TranscodeReasons`, both already populated by the existing, already-reviewed collector pipeline.
- `TreatWarningsAsErrors` + `AnalysisMode=AllEnabledByDefault` are set project-wide — new public members need XML doc comments, matching the existing `CoreTranscodeRules.cs` style.
- No Claude/Claude Code attribution anywhere in this repo (commits, PRs, code comments) — standing project rule.
- Jellyfin's own JSON serialization is PascalCase, not camelCase (verified earlier in this project) — any new API response field must be read by the dashboard JS using the exact PascalCase name.
- `TranscodeReason` is a `[Flags]` enum on `MediaBrowser.Model.Session.TranscodeReason` (already imported wherever needed in this codebase) — the full verified member list includes (among others) `AudioIsExternal`, `VideoRangeTypeNotSupported`, `AudioChannelsNotSupported`, `VideoResolutionNotSupported`, `ContainerBitrateExceedsLimit`, `VideoBitrateNotSupported`, `AudioBitrateNotSupported`, all used below.

## Explicitly out of scope for this plan (do not attempt)

- **Master plan items #1 `SUBTITLE_BURN_IN` and #19 `SUBTITLE_FORMAT_UNSUPPORTED_TEXT`.** Both need the active subtitle stream's codec, which requires calling `BaseItem.GetMediaStreams()` (via `IHasMediaSources`) on the played item. A standalone reproduction (`dotnet run` against a bare `Movie` instance, referencing the real `Jellyfin.Controller 12.1.0`/`Jellyfin.Model 12.1.0` packages) confirmed this throws `NullReferenceException` outside a running Jellyfin host — it depends on a static service locator Jellyfin's own startup wires up, which is absent both in a standalone probe and in a bare `new Movie { ... }` constructed directly inside an xUnit test (exactly how this project's existing tests construct items). Implementing these two rules needs a design for how to get subtitle-codec data into `PlaybackEventFactory`/`PlaybackCollectorHostedService` without making that code path untestable outside a real host (e.g. resolving the stream list once, inside the collector's already-host-resident event handler, and passing the result down as plain data — not calling `Item.GetMediaStreams()` from code a unit test also has to exercise). That is a separate, small research spike plus its own plan; do not guess at it here.
- **Master plan items #9 `REPEATED_CLIENT_TRANSCODE_PATTERN` and #11 `NEW_CLIENT_VERSION_REGRESSION`.** Both require the `Incident` system (Section 17 of the master plan) — deduplication, historical cross-event queries, and for #11 a client-app-version field `PlaybackEvent` doesn't have yet. That's a new subsystem, not a new rule; it needs its own plan.
- **Master plan items #14 `DIRECT_PLAY_FAILED_MID_SESSION`, #15 `HARDWARE_TRANSCODE_UNAVAILABLE`, #17 `CLIENT_UNKNOWN_CAPABILITY_GAP`, #18 `REMOTE_ACCESS_BANDWIDTH_LIMIT`.** The master plan's own text marks #15 "experimental, Phase 2" and #14 "NEEDS VERIFICATION" outright; #17 and #18 need Jellyfin API surfaces (`DeviceProfile` capability comparison, remote/LAN session detection) that haven't been verified against real Jellyfin source the way every other data source this project uses has been. Building against an unverified API is exactly what this project's own no-hallucination rule forbids.
- **Master plan item #20 `MULTIPLE_REASONS_COMPOUND`.** The current `RuleEngine.Diagnose` already returns one `Diagnosis` per matching rule for the same event (see `RuleEngineTests.Diagnose_MatchesMultipleRules_WhenMultipleFlagsSet`), which already surfaces every contributing reason — just as N separate diagnoses rather than one merged one. Collapsing them into a single compound diagnosis is an engine-behavior change with UI implications, not a new rule; out of scope here.

## Task 1: Add five advanced transcode rules and register them

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Diagnostics/AdvancedTranscodeRules.cs`
- Modify: `Jellyfin.Plugin.Sentinel/PluginServiceRegistrator.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Diagnostics/AdvancedTranscodeRulesTests.cs`

**Interfaces:**
- Consumes: `Jellyfin.Plugin.Sentinel.Domain.DiagnosticRule`, `Jellyfin.Plugin.Sentinel.Domain.PlaybackEvent`, `Jellyfin.Plugin.Sentinel.Domain.Confidence` (all exist, unchanged).
- Produces: `AdvancedTranscodeRules.All` — an `IReadOnlyList<DiagnosticRule>`, same shape as the existing `CoreTranscodeRules.All`, consumed by `PluginServiceRegistrator`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Jellyfin.Plugin.Sentinel.Tests/Diagnostics/AdvancedTranscodeRulesTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AdvancedTranscodeRulesTests"`
Expected: FAIL — `AdvancedTranscodeRules` does not exist yet (compile error).

- [ ] **Step 3: Create `Jellyfin.Plugin.Sentinel/Diagnostics/AdvancedTranscodeRules.cs`**

```csharp
using System.Collections.Generic;
using Jellyfin.Plugin.Sentinel.Domain;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.Sentinel.Diagnostics;

/// <summary>
/// Diagnostic rules that infer a cause from a <em>combination</em> of <see cref="TranscodeReason"/>
/// flags rather than a single direct match — master plan Section 14, rules #5, #8, #12, #13, #16.
/// Each of these needs no data beyond what <see cref="CoreTranscodeRules"/> already uses
/// (<c>PlayMethod</c> and <c>TranscodeReasons</c>), which is why they're implemented now rather
/// than deferred alongside the rules that need new data collection (see this plan's own
/// "explicitly out of scope" section).
/// </summary>
public static class AdvancedTranscodeRules
{
    private const TranscodeReason BitrateOnlyReasons =
        TranscodeReason.ContainerBitrateExceedsLimit
        | TranscodeReason.VideoBitrateNotSupported
        | TranscodeReason.AudioBitrateNotSupported;

    private const TranscodeReason ResolutionOrBitrateReasons =
        BitrateOnlyReasons | TranscodeReason.VideoResolutionNotSupported;

    /// <summary>
    /// Gets the complete set of advanced (combination-inferred) transcode diagnostic rules.
    /// </summary>
    public static IReadOnlyList<DiagnosticRule> All { get; } = new List<DiagnosticRule>
    {
        new(
            Code: "EXTERNAL_AUDIO_FORCED_TRANSCODE",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.AudioIsExternal),
            Confidence: Confidence.Likely,
            Explain: e => "This file uses an external audio track, which some clients can't play alongside video without a transcode.",
            Recommendation: "If this happens often for this title, consider muxing the external audio track into the main file."),

        new(
            Code: "HDR_TONE_MAPPING_TRANSCODE",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.VideoRangeTypeNotSupported),
            Confidence: Confidence.Likely,
            Explain: e => "This video's HDR format (or other dynamic range type) isn't supported by your device, so Jellyfin had to tone-map and transcode it.",
            Recommendation: "No action needed unless playback quality or server load is a problem — this is expected for this client/HDR-format combination."),

        new(
            Code: "AUDIO_CHANNEL_DOWNMIX",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.AudioChannelsNotSupported),
            Confidence: Confidence.Possible,
            Explain: e => "This file's audio has more channels than your device/output supports, so Jellyfin had to downmix (and transcode) it.",
            Recommendation: "No action needed — this is expected behavior for this client/channel-layout combination."),

        new(
            Code: "BITRATE_CAP_EXCEEDED",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons != default
                && (e.TranscodeReasons & ~BitrateOnlyReasons) == default,
            Confidence: Confidence.Possible,
            Explain: e => "This file's bitrate is above what your connection or device profile allows, so Jellyfin had to transcode it down — no codec or format mismatch was involved.",
            Recommendation: "If this happens often, consider a lower-bitrate encode of this title or checking your network/device bitrate limit setting."),

        new(
            Code: "RESOLUTION_DOWNSCALE_ONLY",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.VideoResolutionNotSupported)
                && (e.TranscodeReasons & ~ResolutionOrBitrateReasons) == default,
            Confidence: Confidence.Likely,
            Explain: e => "This video's resolution is above what your connection or device allows, so Jellyfin had to downscale (and transcode) it — no codec or format mismatch was involved.",
            Recommendation: "This is a network/device limit, not a format problem — a lower-resolution version of this title would direct play."),
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~AdvancedTranscodeRulesTests"`
Expected: PASS, all 9 tests.

- [ ] **Step 5: Register the new rules in the rule engine**

In `Jellyfin.Plugin.Sentinel/PluginServiceRegistrator.cs`, add `using System.Linq;` to the top of the file, then change:

```csharp
serviceCollection.AddSingleton(new RuleEngine(CoreTranscodeRules.All));
```

to:

```csharp
serviceCollection.AddSingleton(new RuleEngine(CoreTranscodeRules.All.Concat(AdvancedTranscodeRules.All).ToList()));
```

- [ ] **Step 6: Run the full suite to verify nothing else broke**

Run: `dotnet test`
Expected: PASS, all tests (previous count + 9).

- [ ] **Step 7: Build to confirm zero warnings**

Run: `dotnet build`
Expected: `0 Warnung(en)`, `0 Fehler`.

- [ ] **Step 8: Commit**

```bash
git add Jellyfin.Plugin.Sentinel/Diagnostics/AdvancedTranscodeRules.cs Jellyfin.Plugin.Sentinel/PluginServiceRegistrator.cs tests/Jellyfin.Plugin.Sentinel.Tests/Diagnostics/AdvancedTranscodeRulesTests.cs
git commit -m "feat: add five combination-inferred transcode diagnosis rules"
```

## Task 2: Surface Known Core Issues in the API and dashboard

**Files:**
- Modify: `Jellyfin.Plugin.Sentinel/Api/SentinelController.cs`
- Modify: `Jellyfin.Plugin.Sentinel/Configuration/configPage.html`

**Interfaces:**
- Consumes: `Jellyfin.Plugin.Sentinel.Diagnostics.KnownCoreIssues.Match(string)` (already exists, already unit-tested in `KnownCoreIssuesTests.cs` — do not re-test the matching logic itself here, only that the controller wires it through).
- Produces: two new fields on the `GET Sentinel/diagnoses` JSON response, `KnownIssueUrl` (string, nullable) and `KnownIssueExplanation` (string, nullable).

This task has no dedicated automated test file: `SentinelController` currently has zero unit tests (its existing `ResolveMediaName` logic isn't tested either), and the underlying matching logic this task wires through is already covered by `KnownCoreIssuesTests.cs`. Verify this task by manual inspection of the built JSON shape (Step 3) rather than adding a new test harness disproportionate to the rest of the controller.

- [ ] **Step 1: Add the known-issue fields to the controller's response**

In `Jellyfin.Plugin.Sentinel/Api/SentinelController.cs`, add `using Jellyfin.Plugin.Sentinel.Diagnostics;` to the usings, then change the `GetRecentDiagnoses` method's projection from:

```csharp
        var response = summaries.Select(summary => new
        {
            summary.Id,
            summary.Code,
            Confidence = summary.Confidence.ToString(),
            summary.Evidence,
            summary.Explanation,
            summary.Recommendation,
            summary.CreatedAtUtc,
            summary.Client,
            summary.DeviceName,
            summary.PlayMethod,
            MediaName = ResolveMediaName(summary.ItemId)
        });
```

to:

```csharp
        var response = summaries.Select(summary =>
        {
            var knownIssue = KnownCoreIssues.Match(summary.Code);

            return new
            {
                summary.Id,
                summary.Code,
                Confidence = summary.Confidence.ToString(),
                summary.Evidence,
                summary.Explanation,
                summary.Recommendation,
                summary.CreatedAtUtc,
                summary.Client,
                summary.DeviceName,
                summary.PlayMethod,
                MediaName = ResolveMediaName(summary.ItemId),
                KnownIssueUrl = knownIssue?.IssueUrl,
                KnownIssueExplanation = knownIssue?.Explanation
            };
        });
```

- [ ] **Step 2: Run the full suite to confirm the controller still compiles cleanly**

Run: `dotnet build`
Expected: `0 Warnung(en)`, `0 Fehler`.

- [ ] **Step 3: Add rendering for known issues in the dashboard**

In `Jellyfin.Plugin.Sentinel/Configuration/configPage.html`, inside `appendDiagnosisRows`, change the `detailCell.innerHTML` assignment from:

```javascript
                    detailCell.innerHTML =
                        '<strong>Evidence:</strong><ul>' + buildEvidenceList(diagnosis.Evidence) + '</ul>' +
                        '<strong>Recommendation:</strong> ' + escapeHtmlText(diagnosis.Recommendation) +
                        '<div><em>Code: ' + escapeHtmlText(diagnosis.Code) + '</em></div>';
```

to:

```javascript
                    detailCell.innerHTML =
                        '<strong>Evidence:</strong><ul>' + buildEvidenceList(diagnosis.Evidence) + '</ul>' +
                        '<strong>Recommendation:</strong> ' + escapeHtmlText(diagnosis.Recommendation) +
                        '<div><em>Code: ' + escapeHtmlText(diagnosis.Code) + '</em></div>' +
                        buildKnownIssueBlock(diagnosis);
```

and add this new function above `appendDiagnosisRows` (right after `buildEvidenceList`):

```javascript
                function buildKnownIssueBlock(diagnosis) {
                    if (!diagnosis.KnownIssueUrl) {
                        return '';
                    }

                    // Deliberately labeled distinctly from Sentinel's own diagnosis — this is a
                    // curated pointer to an already-filed Jellyfin core bug, not something
                    // Sentinel itself inferred (master plan Section 14: "Known Jellyfin Bug",
                    // never presented as if it were a Sentinel diagnosis).
                    return '<div style="margin-top:0.5em;border-left:3px solid orange;padding-left:0.5em;">' +
                        '<strong>Known Jellyfin Bug (not a Sentinel diagnosis):</strong> ' +
                        escapeHtmlText(diagnosis.KnownIssueExplanation) + ' ' +
                        '<a href="' + escapeHtmlText(diagnosis.KnownIssueUrl) + '" target="_blank" rel="noopener noreferrer">' +
                        escapeHtmlText(diagnosis.KnownIssueUrl) + '</a></div>';
                }
```

- [ ] **Step 4: Manually verify the JSON shape**

There is no automated test for the HTML/JS (the project has no JS test framework — see `SETUP.md`'s testing section). Verify by running the app locally (`npm`/`dotnet run` equivalent for this plugin is a full Jellyfin host, so this step is: build, and defer live verification to the plugin's normal live-server testing flow already established in this project) — at minimum, confirm by reading the code that `KnownIssueUrl`/`KnownIssueExplanation` will be `null` (not omitted) for every diagnosis code that isn't `TRANSCODE_REASON_MISSING`, so `if (!diagnosis.KnownIssueUrl)` in the JS correctly treats `null` as falsy and renders nothing extra for ordinary diagnoses.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.Sentinel/Api/SentinelController.cs Jellyfin.Plugin.Sentinel/Configuration/configPage.html
git commit -m "feat: surface the Known Core Issues table in the API and dashboard"
```

## Task 3: Document the expanded rule set

**Files:**
- Modify: `SETUP.md`

**Interfaces:**
- Consumes: nothing (documentation only).
- Produces: nothing (documentation only).

- [ ] **Step 1: Update the "Only a narrow set of diagnoses so far" bullet in `SETUP.md`**

Find the bullet (in the "Current limitations" section) that currently reads:

```
- **Only a narrow set of diagnoses so far.** Sentinel currently recognizes unsupported
  video/audio codecs, unsupported containers, unsupported secondary audio tracks, too many
  streams, and "transcoded but Jellyfin didn't say why." It does not yet explain every possible
  reason a transcode happens (e.g. bitrate-driven transcodes aren't covered yet) — an
  unrecognized transcode is simply not diagnosed, not misdiagnosed.
```

Replace it with:

```
- **A wider, but still incomplete, set of diagnoses.** Sentinel currently recognizes unsupported
  video/audio codecs, unsupported containers, unsupported secondary audio tracks, too many
  streams, external audio tracks, HDR/dynamic-range mismatches, audio channel downmixing,
  bitrate-only transcodes, resolution-only transcodes, and "transcoded but Jellyfin didn't say
  why." Not yet covered: subtitle burn-in (needs subtitle-stream data this plugin doesn't
  collect yet), repeated-transcode-pattern detection and client-version-regression detection
  (both need a not-yet-built incident/history system), and a few explicitly experimental or
  unverified diagnoses from the original project plan (hardware-transcode-unavailable,
  mid-session direct-play failure, client-capability-gap, remote-bandwidth-limit) that need
  further research before they can be built without guessing. An unrecognized transcode is
  simply not diagnosed, not misdiagnosed.
- **Known Jellyfin core bugs are now called out separately.** When a diagnosis code has a
  matching entry in Sentinel's curated Known Core Issues table (currently just
  `TRANSCODE_REASON_MISSING`, linked to jellyfin/jellyfin#12193), the dashboard shows it as a
  distinct "Known Jellyfin Bug" note, not as a Sentinel-inferred diagnosis — so the two are never
  confused about which one is actually vouching for the explanation.
```

- [ ] **Step 2: Commit**

```bash
git add SETUP.md
git commit -m "docs: document the expanded rule set and known-issue surfacing"
```
