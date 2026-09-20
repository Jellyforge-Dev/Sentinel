# Sentinel MVP Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. This plan is large — expect it to span many dispatch/review cycles and, per explicit user instruction, to continue autonomously across session/usage-limit resets without pausing for confirmation between tasks.

**Goal:** Complete the four MVP pillars from `JELLYFIN_SENTINEL_MASTER_PLAN.md` Section 9/31 (Playback Doctor + Incident Engine + Notification Delivery + compact dashboard) that are currently only partially built, add full localization (7 languages) for every diagnosis explanation and every dashboard UI string, fix media display to show "Series - Episode", modernize the dashboard's visual design, and add a new (not in the original master plan, explicitly requested) plugin-update / restart-pending notification feature.

**Architecture:** Six mostly-independent workstreams (A–F below), each producing its own reviewable PR. Workstream A (localization infrastructure) is a hard prerequisite for E3 (localized dashboard strings) and touches the diagnosis-storage schema, so it must land first. B (Incident Engine) and C (Notification Delivery) are sequential — C notifies on Incident lifecycle events, so it needs B's `Incident` entity to exist. D, E, and F can proceed in parallel with B/C once A is merged.

**Tech Stack:** C#/.NET 10 (existing), xUnit/Moq (existing), vanilla JS (existing dashboard, no new frontend framework — consistent with the rest of this project and Jellyfin's own plugin config pages).

**Spec:** [`JELLYFIN_SENTINEL_MASTER_PLAN.md`](../../../JELLYFIN_SENTINEL_MASTER_PLAN.md) Sections 9, 14, 15, 16, 17, 28, 29, 31. The plugin-update/restart-pending notification feature and the specific 7-language requirement are NOT in that document — they come directly from the user's most recent request and are treated as first-class scope here regardless.

## Global Constraints

- No Claude/Claude Code attribution anywhere in this repo (commits, PRs, code comments) — standing project rule, unchanged.
- `TreatWarningsAsErrors` + `AnalysisMode=AllEnabledByDefault` — every new public member needs an XML doc comment, matching existing style throughout this codebase.
- SQLite schema changes must be additive and non-destructive to existing installations: new tables via `CREATE TABLE IF NOT EXISTS`; new columns on existing tables via a guarded migration (`PRAGMA table_info(<table>)`, add only if missing) — never assume a fresh database, always assume an admin is upgrading from 0.1.5.0 with real data already in `sentinel.db`.
- Every outbound network call this plan introduces (webhook, Discord, Telegram, Email, plugin-update check) must have an explicit timeout and must never let an unhandled exception escape into Jellyfin's own hosted-service/event-handler call path — matching the established `try/catch` + `[LoggerMessage]` pattern already used in `PlaybackCollectorHostedService`.
- Locale files are flat JSON, English as the fallback/reference language, keys never renamed across languages, matching the org-wide i18n convention in `GitHub/CLAUDE.md`. Supported languages for this plan: English (`en`), German (`de`), Spanish (`es`), French (`fr`), Swedish (`sv`), Danish (`da`), Polish (`pl`).
- No hallucinated Jellyfin APIs. Every Jellyfin interface/property this plan relies on has already been verified against the real v12.1 source during planning (see inline citations in each task) — if an implementer needs an API not cited here, it must be verified the same way (via `gh api repos/jellyfin/jellyfin/contents/<path>?ref=v12.1`) before use, never assumed from general .NET/Emby knowledge.
- Confidence/severity values, rule codes, and existing behavior already shipped and reviewed (all rules in `CoreTranscodeRules.cs`/`AdvancedTranscodeRules.cs`, the `PlayState`-cleared-before-stop fix, the `PlayMethod`/`TranscodeReasons` invariant) are NOT to be re-litigated by this plan — only extended.

---

# Workstream A: Localization Infrastructure

Everything in this workstream must land and be merged before Workstream E's localized-dashboard tasks begin, because the dashboard's translated strings are served through the API this workstream builds.

## Task A1: Add a `Language` setting to Sentinel's configuration

**Files:**
- Modify: `Jellyfin.Plugin.Sentinel/Configuration/PluginConfiguration.cs`
- Create: `Jellyfin.Plugin.Sentinel/Localization/SupportedLanguage.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Localization/SupportedLanguageTests.cs`

**Interfaces:**
- Produces: `SupportedLanguage` enum (`En`, `De`, `Es`, `Fr`, `Sv`, `Da`, `Pl`) and a `PluginConfiguration.Language` property, default `En`.

- [ ] **Step 1: Write the failing test**

```csharp
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
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test --filter "FullyQualifiedName~SupportedLanguageTests"` — expected FAIL (type doesn't exist).

- [ ] **Step 3: Create `Jellyfin.Plugin.Sentinel/Localization/SupportedLanguage.cs`**

```csharp
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
```

- [ ] **Step 4: Run to verify it passes.**

- [ ] **Step 5: Add the `Language` property to `PluginConfiguration`**

```csharp
using Jellyfin.Plugin.Sentinel.Localization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Sentinel.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the language Sentinel renders its dashboard and diagnosis explanations in.
    /// Independent of Jellyfin's own per-user display language — see
    /// <see cref="SupportedLanguage"/>'s remarks.
    /// </summary>
    public SupportedLanguage Language { get; set; } = SupportedLanguage.En;
}
```

- [ ] **Step 6: Build and run the full suite** — `dotnet build && dotnet test` — expected 0 warnings, all passing.

- [ ] **Step 7: Commit.**

## Task A2: Translation resource files for all diagnosis rule text

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Localization/Strings/en.json`, `de.json`, `es.json`, `fr.json`, `sv.json`, `da.json`, `pl.json`
- Modify: `Jellyfin.Plugin.Sentinel/Jellyfin.Plugin.Sentinel.csproj` (embed the JSON files as resources)
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Localization/TranslationCompletenessTests.cs`

**Interfaces:**
- Produces: seven embedded JSON files, each a flat `{ "KEY": "translated text" }` map. `en.json` is authoritative for the key set — every other file must contain exactly the same keys (verified by a test, not by hand-checking).

**Key naming convention:** `<RULE_CODE>_EXPLANATION` and `<RULE_CODE>_RECOMMENDATION` for every rule in `CoreTranscodeRules.cs` and `AdvancedTranscodeRules.cs`, plus a handful of dashboard-chrome keys prefixed `UI_` (added in Task A5).

The 11 rule codes needing both keys today: `VIDEO_CODEC_UNSUPPORTED`, `AUDIO_CODEC_UNSUPPORTED`, `CONTAINER_UNSUPPORTED`, `SECONDARY_AUDIO_UNSUPPORTED`, `TOO_MANY_STREAMS`, `TRANSCODE_REASON_MISSING`, `EXTERNAL_AUDIO_FORCED_TRANSCODE`, `HDR_TONE_MAPPING_TRANSCODE`, `AUDIO_CHANNEL_DOWNMIX`, `BITRATE_CAP_EXCEEDED`, `RESOLUTION_DOWNSCALE_ONLY` — 22 keys total for this task.

- [ ] **Step 1: Write the failing completeness test**

```csharp
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
```

- [ ] **Step 2: Run to verify it fails** (resources don't exist yet).

- [ ] **Step 3: Write `en.json`** (authoritative source text — copy the existing `Explain`/`Recommendation` string literals verbatim from `CoreTranscodeRules.cs` and `AdvancedTranscodeRules.cs`, do not reword them):

```json
{
  "VIDEO_CODEC_UNSUPPORTED_EXPLANATION": "Your device can't play this video's codec natively, so Jellyfin had to convert it on the fly.",
  "VIDEO_CODEC_UNSUPPORTED_RECOMMENDATION": "No action needed unless playback quality or server load is a problem — this is expected for this client/codec combination.",
  "AUDIO_CODEC_UNSUPPORTED_EXPLANATION": "Your device can't decode this audio track natively, so Jellyfin had to transcode the audio.",
  "AUDIO_CODEC_UNSUPPORTED_RECOMMENDATION": "No action needed — this is expected behavior for this client/audio-codec combination.",
  "CONTAINER_UNSUPPORTED_EXPLANATION": "Your device doesn't support this file's container format, so Jellyfin had to remux or transcode it.",
  "CONTAINER_UNSUPPORTED_RECOMMENDATION": "No action needed — this is expected behavior for this client/container combination.",
  "SECONDARY_AUDIO_UNSUPPORTED_EXPLANATION": "This file has a secondary audio track your device can't handle alongside the primary one, forcing a transcode.",
  "SECONDARY_AUDIO_UNSUPPORTED_RECOMMENDATION": "If this happens often for this title, consider removing or re-encoding the secondary audio track.",
  "TOO_MANY_STREAMS_EXPLANATION": "This file has more audio/subtitle streams than your client can handle at once, so Jellyfin had to transcode it.",
  "TOO_MANY_STREAMS_RECOMMENDATION": "Consider trimming unused audio/subtitle tracks from this file.",
  "TRANSCODE_REASON_MISSING_EXPLANATION": "Jellyfin transcoded this playback but didn't record why — this can happen due to a known Jellyfin logging gap, not necessarily a configuration problem.",
  "TRANSCODE_REASON_MISSING_RECOMMENDATION": "No specific action — Sentinel doesn't have enough information for a confident diagnosis here.",
  "EXTERNAL_AUDIO_FORCED_TRANSCODE_EXPLANATION": "This file uses an external audio track, which some clients can't play alongside video without a transcode.",
  "EXTERNAL_AUDIO_FORCED_TRANSCODE_RECOMMENDATION": "If this happens often for this title, consider muxing the external audio track into the main file.",
  "HDR_TONE_MAPPING_TRANSCODE_EXPLANATION": "This video's HDR format (or other dynamic range type) isn't supported by your device, so Jellyfin had to convert it (typically including tone-mapping).",
  "HDR_TONE_MAPPING_TRANSCODE_RECOMMENDATION": "No action needed unless playback quality or server load is a problem — this is expected for this client/HDR-format combination.",
  "AUDIO_CHANNEL_DOWNMIX_EXPLANATION": "This file's audio has more channels than your device/output supports, so Jellyfin had to downmix (and transcode) it.",
  "AUDIO_CHANNEL_DOWNMIX_RECOMMENDATION": "No action needed — this is expected behavior for this client/channel-layout combination.",
  "BITRATE_CAP_EXCEEDED_EXPLANATION": "This file's bitrate is above what your connection or device profile allows, so Jellyfin had to transcode it down — no codec or format mismatch was involved.",
  "BITRATE_CAP_EXCEEDED_RECOMMENDATION": "If this happens often, consider a lower-bitrate encode of this title or checking your network/device bitrate limit setting.",
  "RESOLUTION_DOWNSCALE_ONLY_EXPLANATION": "This video's resolution is above what your connection or device allows, so Jellyfin had to downscale (and transcode) it — no codec or format mismatch was involved.",
  "RESOLUTION_DOWNSCALE_ONLY_RECOMMENDATION": "This is a network/device limit, not a format problem — a lower-resolution version of this title would direct play."
}
```

- [ ] **Step 4: Translate into `de.json`, `es.json`, `fr.json`, `sv.json`, `da.json`, `pl.json`** — same 22 keys, natural (not machine-literal) translations. This is the one step in this entire plan that is inherently a translation-quality judgment call, not a mechanical transcription — the implementer must produce fluent, accurate translations, not placeholder text, for all six languages before moving on. Do not leave any locale file with English text "translated later."

- [ ] **Step 5: Embed the JSON files as resources.** In `Jellyfin.Plugin.Sentinel.csproj`, add:

```xml
<ItemGroup>
  <EmbeddedResource Include="Localization\Strings\*.json" />
</ItemGroup>
```

- [ ] **Step 6: Run the full suite** — `dotnet test` — all 14 completeness/non-empty assertions (7 locales × 2 checks, minus English's own 2 = 12, plus the 7-language `SupportedLanguageTests` from A1) must pass.

- [ ] **Step 7: Commit.**

## Task A3: `LocalizationService` — the single place that resolves a key + language into text

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Localization/LocalizationService.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Localization/LocalizationServiceTests.cs`

**Interfaces:**
- Consumes: the embedded JSON resources from Task A2.
- Produces: `LocalizationService.Translate(string key, SupportedLanguage language)` — used by every later task that needs translated text (Diagnosis rendering, notification text, dashboard UI strings).

- [ ] **Step 1: Write the failing tests**

```csharp
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
```

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement `LocalizationService`**

```csharp
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
```

- [ ] **Step 4: Run the full suite, confirm passing.**

- [ ] **Step 5: Commit.**

## Task A4: Store only the rule Code in `Diagnosis` — stop persisting pre-rendered English text

**Files:**
- Modify: `Jellyfin.Plugin.Sentinel/Domain/Diagnosis.cs`, `Jellyfin.Plugin.Sentinel/Diagnostics/RuleEngine.cs`, `Jellyfin.Plugin.Sentinel/Persistence/DiagnosisRepository.cs`, `Jellyfin.Plugin.Sentinel/Persistence/SentinelDatabase.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Diagnostics/RuleEngineTests.cs` (update existing assertions), `tests/Jellyfin.Plugin.Sentinel.Tests/Persistence/DiagnosisRepositoryTests.cs` (update existing assertions — find via `Glob tests/**/DiagnosisRepository*Tests.cs` first and read it before editing)

**Why:** Today `Diagnosis.Explanation`/`Recommendation` are English strings computed once by `RuleEngine.Diagnose()` and written to SQLite verbatim (`DiagnosisRepository.cs:54-55`). Changing Sentinel's language setting must not require a database migration or re-translation of history — the fix is to stop storing rendered text at all, and translate at read time (Task A5) from the `Code` column, which is already stored.

- [ ] **Step 1: Update `Diagnosis.cs`** — remove `Explanation`/`Recommendation` string properties; the entity now carries only `Code`, `Confidence`, `Evidence`. (Read the current file first — it's small — before editing, since its exact current shape must be preserved for `Confidence`/`Evidence`.)

- [ ] **Step 2: Update `DiagnosticRule.Explain`** — this plan does NOT remove `DiagnosticRule.Explain`/`Recommendation` from `Domain/DiagnosticRule.cs` or from `CoreTranscodeRules.cs`/`AdvancedTranscodeRules.cs` (those stay as-is, English-only, and are now used ONLY as the fallback text Task A2's `en.json` was transcribed from — they are effectively dead code paths once A5 lands, but leaving them in place avoids a churn-only rewrite of two already-reviewed rule files; do not delete `Explain`/`Recommendation` from `DiagnosticRule` in this task). Instead, `RuleEngine.Diagnose()` stops calling `rule.Explain(playbackEvent)`/`rule.Recommendation` when building the `Diagnosis` it returns — it only needs `rule.Code` now for the evidence list construction that already exists (`$"Matched rule = {rule.Code}"`).

- [ ] **Step 3: Update `DiagnosisRepository.cs`** — `Insert` no longer writes `Explanation`/`Recommendation` columns. Add a guarded schema migration in `SentinelDatabase.Initialize()`:

```csharp
private void Initialize()
{
    using var connection = OpenConnection();
    using (var command = connection.CreateCommand())
    {
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS PlaybackEvent ( ... unchanged ... );
            CREATE INDEX IF NOT EXISTS IX_PlaybackEvent_Item_Client ON PlaybackEvent(ItemId, Client, CreatedAtUtc);

            CREATE TABLE IF NOT EXISTS Diagnosis (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlaybackEventId INTEGER NOT NULL,
                Code TEXT NOT NULL,
                Confidence TEXT NOT NULL,
                EvidenceJson TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (PlaybackEventId) REFERENCES PlaybackEvent(Id)
            );
            CREATE INDEX IF NOT EXISTS IX_Diagnosis_Code ON Diagnosis(Code);
            CREATE INDEX IF NOT EXISTS IX_Diagnosis_PlaybackEventId ON Diagnosis(PlaybackEventId);
            """;
        command.ExecuteNonQuery();
    }

    // Existing installations already have a Diagnosis table with Explanation/Recommendation
    // NOT NULL columns from before this change - CREATE TABLE IF NOT EXISTS above is a no-op
    // for them, so Insert() (which no longer supplies those columns) would violate the NOT NULL
    // constraint. Drop them if present; a fresh install never has them, so this is a no-op there.
    DropColumnIfExists(connection, "Diagnosis", "Explanation");
    DropColumnIfExists(connection, "Diagnosis", "Recommendation");
}

private static void DropColumnIfExists(SqliteConnection connection, string table, string column)
{
    using (var checkCommand = connection.CreateCommand())
    {
        checkCommand.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        checkCommand.Parameters.AddWithValue("$column", column);
        var exists = (long)checkCommand.ExecuteScalar()! > 0;
        if (!exists)
        {
            return;
        }
    }

    using var alterCommand = connection.CreateCommand();
    alterCommand.CommandText = $"ALTER TABLE {table} DROP COLUMN {column};";
    alterCommand.ExecuteNonQuery();
}
```

(`ALTER TABLE ... DROP COLUMN` requires SQLite ≥ 3.35.0 — verify the bundled `SQLitePCLRaw.provider.e_sqlite3` version used elsewhere in this project meets that; if the implementer finds it doesn't, fall back to the documented SQLite recreate-table-and-copy pattern instead of assuming.)

`GetRecent` stops selecting/returning `Explanation`/`Recommendation` from `Diagnosis` — `DiagnosisSummary` (Task A5 below) computes them from `Code` at call time instead.

- [ ] **Step 4: Update every existing test that references `Diagnosis.Explanation`/`Recommendation`** or asserts on `DiagnosisRepository`'s column set — find them first: `Grep -r "\.Explanation\|\.Recommendation" tests/`. Update each to assert on `Code`/`Confidence`/`Evidence` instead, or (for the ones specifically about rendering) move that assertion to Task A5's new tests.

- [ ] **Step 5: Run the full suite** — expect failures to update iteratively until 0 failures, 0 warnings.

- [ ] **Step 6: Commit.**

## Task A5: Translate at API-serve time; add dashboard UI string keys

**Files:**
- Modify: `Jellyfin.Plugin.Sentinel/Domain/DiagnosisSummary.cs`, `Jellyfin.Plugin.Sentinel/Persistence/DiagnosisRepository.cs`, `Jellyfin.Plugin.Sentinel/Api/SentinelController.cs`, `Jellyfin.Plugin.Sentinel/Localization/Strings/*.json` (add `UI_*` keys)
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Api/SentinelControllerTests.cs` (new file — this controller has no tests yet; keep this one narrowly scoped to the translation wiring, not a full controller test suite)

**Interfaces:**
- Produces: `GET Sentinel/diagnoses` response gains `Explanation`/`Recommendation` fields again, but now computed via `LocalizationService.Translate($"{Code}_EXPLANATION", currentLanguage)` at request time, using `Plugin.Instance.Configuration.Language`. Also produces a new `GET Sentinel/ui-strings` endpoint returning the current language's `UI_*` keys as a flat JSON object, for the dashboard JS to consume.

**New `UI_*` keys to add to all 7 locale files** (extend Task A2's files — re-run the completeness test after): `UI_TITLE` ("Jellyfin Sentinel"), `UI_INTRO` (the existing intro paragraph), `UI_COL_TIME`, `UI_COL_MEDIA`, `UI_COL_CLIENT_DEVICE`, `UI_COL_CONFIDENCE`, `UI_COL_EXPLANATION`, `UI_LOADING`, `UI_NO_DIAGNOSES`, `UI_LOAD_FAILED`, `UI_EVIDENCE_LABEL`, `UI_RECOMMENDATION_LABEL`, `UI_CODE_LABEL`, `UI_KNOWN_ISSUE_LABEL`.

- [ ] **Step 1: Write the failing test** for `SentinelController`

```csharp
using System.Linq;
using Jellyfin.Plugin.Sentinel.Api;
using Jellyfin.Plugin.Sentinel.Configuration;
using Jellyfin.Plugin.Sentinel.Localization;
using Jellyfin.Plugin.Sentinel.Persistence;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Api;

// NOTE for implementer: Plugin.Instance is a static singleton set by Jellyfin at plugin load
// time (see Plugin.cs). If SentinelController reads Plugin.Instance.Configuration.Language
// directly, this test needs Plugin.Instance to be non-null with a real Configuration - check
// how existing tests (if any) handle Plugin.Instance in isolation before assuming a pattern;
// if none do, prefer injecting the current language via a small interface
// (e.g. ICurrentLanguageProvider) resolved through DI instead of touching the Plugin static,
// so this controller stays unit-testable. Adjust the constructor signature below to match
// whichever approach is actually taken - this is a judgment call for whoever implements this
// task, not prescribed further here.
public class SentinelControllerTranslationTests
{
    // ... constructs a DiagnosisRepository-backed or mocked scenario with one diagnosis of
    // Code = "VIDEO_CODEC_UNSUPPORTED", requests German, asserts the returned Explanation
    // is NOT the English string and NOT literally "VIDEO_CODEC_UNSUPPORTED_EXPLANATION".
}
```

(This step is deliberately left as a design note rather than exact code — resolving "how does a controller access the current language without becoming untestable" is a real judgment call given `Plugin.Instance` is a static Jellyfin sets, and forcing a specific DI shape here without seeing how `Plugin.cs`/`PluginServiceRegistrator.cs` currently expose configuration would be guessing. Read `Plugin.cs` first.)

- [ ] **Step 2-N:** Implement `DiagnosisSummary` losing its own `Explanation`/`Recommendation` fields (or keeping them as `Code`-derived computed properties — implementer's call, guided by minimizing changes to `DiagnosisRepository.GetRecent`'s SQL), add `LocalizationService` to `SentinelController`'s constructor, translate at response-projection time, add the `GET Sentinel/ui-strings` endpoint, extend all 7 locale JSON files with the `UI_*` keys (re-run Task A2's completeness test — it will now check the larger key set automatically since it just diffs against `en.json`).

- [ ] **Step Final: Run the full suite, 0 warnings, all passing. Commit.**

---

# Workstream B: Incident Engine (master plan Section 17)

## Task B1: `Incident` domain type and SQLite schema

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Domain/Incident.cs`, `Jellyfin.Plugin.Sentinel/Domain/IncidentStatus.cs`
- Modify: `Jellyfin.Plugin.Sentinel/Persistence/SentinelDatabase.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Domain/IncidentTests.cs` (basic construction/property test only — this task is mostly schema)

**Schema:**

```sql
CREATE TABLE IF NOT EXISTS Incident (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Code TEXT NOT NULL,
    ItemId TEXT NOT NULL,
    Client TEXT NOT NULL,
    DeviceName TEXT NOT NULL,
    Status TEXT NOT NULL,
    OccurrenceCount INTEGER NOT NULL,
    FirstSeenUtc TEXT NOT NULL,
    LastSeenUtc TEXT NOT NULL,
    AcknowledgedAtUtc TEXT NULL,
    ResolvedAtUtc TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Incident_Fingerprint ON Incident(Code, ItemId, Client, DeviceName)
    WHERE Status != 'Resolved';
CREATE INDEX IF NOT EXISTS IX_Incident_LastSeenUtc ON Incident(LastSeenUtc);

CREATE TABLE IF NOT EXISTS IncidentDiagnosis (
    IncidentId INTEGER NOT NULL,
    DiagnosisId INTEGER NOT NULL,
    PRIMARY KEY (IncidentId, DiagnosisId),
    FOREIGN KEY (IncidentId) REFERENCES Incident(Id),
    FOREIGN KEY (DiagnosisId) REFERENCES Diagnosis(Id)
);
```

Design note for the implementer: the partial unique index (`WHERE Status != 'Resolved'`) is what makes "same Code+ItemId+Client+DeviceName" dedup work while still allowing a *new* incident to open after a prior one for the exact same fingerprint was resolved (master plan Section 17's "Reopened if it recurs after resolution" — modeled here as: if an active, non-Resolved incident with this fingerprint exists, increment it; if the only matching row is Resolved, that specific recurrence instead flips it to `Reopened` rather than creating a duplicate row — see Task B3 for the exact upsert logic this constrains).

- [ ] Steps: write `IncidentStatus` enum (`Detected`, `Acknowledged`, `Resolved`, `Reopened` — master plan's `Investigating` sub-state is not modeled as a separate DB value in this plan; treat it as a UI-only concept if the dashboard task wants it, to avoid a status enum nobody transitions into or out of automatically), write `Incident` record/class (Id, Code, ItemId, Client, DeviceName, Status, OccurrenceCount, FirstSeenUtc, LastSeenUtc, AcknowledgedAtUtc, ResolvedAtUtc — all following this codebase's existing `required`/nullable conventions, see `PlaybackEvent.cs` for the pattern), add the schema to `SentinelDatabase.Initialize()`, basic construction test, commit.

## Task B2: `IncidentRepository` — upsert-on-diagnosis, list, acknowledge, resolve

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Persistence/IncidentRepository.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Persistence/IncidentRepositoryTests.cs`

**Interfaces:**
- Consumes: `SentinelDatabase` (existing pattern, see `DiagnosisRepository`/`PlaybackEventRepository` for the exact style to match — same connection-per-call, parameterized SQL, no ORM).
- Produces:
  - `long UpsertOnDiagnosis(long diagnosisId, string code, string itemId, string client, string deviceName)` — the core dedup logic (Step 3 below is the load-bearing test for this).
  - `IReadOnlyList<Incident> GetRecent(int limit)`
  - `void Acknowledge(long incidentId)`
  - `void Resolve(long incidentId)`

- [ ] **Step 1: Write the failing tests** — at minimum these five, each a distinct behavior:
  1. `UpsertOnDiagnosis_CreatesNewIncident_WhenNoneExistsForFingerprint` — first call for a fingerprint creates `Status = Detected`, `OccurrenceCount = 1`.
  2. `UpsertOnDiagnosis_IncrementsExistingIncident_WhenSameFingerprintRecurs` — second call for the same `(Code, ItemId, Client, DeviceName)` while the existing row is `Detected` increments `OccurrenceCount` to 2 and updates `LastSeenUtc`, does NOT create a second row.
  3. `UpsertOnDiagnosis_ReopensIncident_WhenFingerprintRecursAfterResolution` — an incident resolved via `Resolve()`, then the same fingerprint recurs → status becomes `Reopened`, `OccurrenceCount` continues incrementing (not reset to 1), `ResolvedAtUtc` is cleared back to null.
  4. `Acknowledge_SetsStatusAndTimestamp`
  5. `Resolve_SetsStatusAndTimestamp`

- [ ] **Step 2: Run to verify all fail.**

- [ ] **Step 3: Implement.** The upsert is the one piece of real logic here:

```csharp
public long UpsertOnDiagnosis(long diagnosisId, string code, string itemId, string client, string deviceName)
{
    using var connection = _database.OpenConnection();
    using var transaction = connection.BeginTransaction();

    long incidentId;
    var now = DateTime.UtcNow.ToString("O");

    using (var findCommand = connection.CreateCommand())
    {
        findCommand.Transaction = transaction;
        findCommand.CommandText =
            """
            SELECT Id, Status FROM Incident
            WHERE Code = $code AND ItemId = $itemId AND Client = $client AND DeviceName = $deviceName
            ORDER BY Id DESC LIMIT 1;
            """;
        findCommand.Parameters.AddWithValue("$code", code);
        findCommand.Parameters.AddWithValue("$itemId", itemId);
        findCommand.Parameters.AddWithValue("$client", client);
        findCommand.Parameters.AddWithValue("$deviceName", deviceName);

        using var reader = findCommand.ExecuteReader();
        if (reader.Read())
        {
            incidentId = reader.GetInt64(0);
            var existingStatus = reader.GetString(1);
            reader.Close();

            using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = existingStatus == nameof(IncidentStatus.Resolved)
                ? """
                  UPDATE Incident
                  SET Status = $reopened, OccurrenceCount = OccurrenceCount + 1, LastSeenUtc = $now, ResolvedAtUtc = NULL
                  WHERE Id = $id;
                  """
                : """
                  UPDATE Incident SET OccurrenceCount = OccurrenceCount + 1, LastSeenUtc = $now WHERE Id = $id;
                  """;
            updateCommand.Parameters.AddWithValue("$id", incidentId);
            updateCommand.Parameters.AddWithValue("$now", now);
            if (existingStatus == nameof(IncidentStatus.Resolved))
            {
                updateCommand.Parameters.AddWithValue("$reopened", nameof(IncidentStatus.Reopened));
            }

            updateCommand.ExecuteNonQuery();
        }
        else
        {
            reader.Close();
            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                INSERT INTO Incident (Code, ItemId, Client, DeviceName, Status, OccurrenceCount, FirstSeenUtc, LastSeenUtc)
                VALUES ($code, $itemId, $client, $deviceName, $detected, 1, $now, $now);
                SELECT last_insert_rowid();
                """;
            insertCommand.Parameters.AddWithValue("$code", code);
            insertCommand.Parameters.AddWithValue("$itemId", itemId);
            insertCommand.Parameters.AddWithValue("$client", client);
            insertCommand.Parameters.AddWithValue("$deviceName", deviceName);
            insertCommand.Parameters.AddWithValue("$detected", nameof(IncidentStatus.Detected));
            insertCommand.Parameters.AddWithValue("$now", now);
            incidentId = (long)insertCommand.ExecuteScalar()!;
        }
    }

    using (var linkCommand = connection.CreateCommand())
    {
        linkCommand.Transaction = transaction;
        linkCommand.CommandText = "INSERT INTO IncidentDiagnosis (IncidentId, DiagnosisId) VALUES ($incidentId, $diagnosisId);";
        linkCommand.Parameters.AddWithValue("$incidentId", incidentId);
        linkCommand.Parameters.AddWithValue("$diagnosisId", diagnosisId);
        linkCommand.ExecuteNonQuery();
    }

    transaction.Commit();
    return incidentId;
}
```

Note: the `SELECT ... ORDER BY Id DESC LIMIT 1` (rather than relying solely on the partial unique index for the lookup) is deliberate — it finds the most recent row for a fingerprint regardless of status, so a `Resolved` row is found and reopened instead of the partial-unique-index gap silently allowing an unrelated second `Detected` row to be inserted for the same fingerprint by a race. `Acknowledge`/`Resolve`/`GetRecent` follow the same connection-per-call style as `DiagnosisRepository` — write them directly, no further design judgment needed.

- [ ] **Step 4-6: Run tests, build, commit.**

## Task B3: Wire the collector to raise Incidents, not just Diagnosis rows

**Files:**
- Modify: `Jellyfin.Plugin.Sentinel/Collector/PlaybackCollectorHostedService.cs`, `Jellyfin.Plugin.Sentinel/PluginServiceRegistrator.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Collector/PlaybackCollectorHostedServiceTests.cs` (extend)

**Interfaces:**
- Consumes: `IncidentRepository.UpsertOnDiagnosis` (Task B2).
- Produces: after `_diagnosisRepository.Insert(playbackEventId, diagnosis)` in `OnPlaybackStopped`, also call `_incidentRepository.UpsertOnDiagnosis(diagnosisId, diagnosis.Code, playbackEvent.ItemId, playbackEvent.Client, playbackEvent.DeviceName)`. `DiagnosisRepository.Insert` needs to start returning the inserted row's id (check whether it already does via `last_insert_rowid()` — if not, add it, following the exact pattern in Task B2's insert).

- [ ] Add `IncidentRepository` to the constructor (inject via DI, register as singleton in `PluginServiceRegistrator.cs` next to the other repositories), write a test that fires the same diagnosable event twice and asserts exactly one `Incident` row with `OccurrenceCount = 2`, run full suite, commit.

## Task B4: Incident API endpoints

**Files:**
- Modify: `Jellyfin.Plugin.Sentinel/Api/SentinelController.cs`

**Interfaces:**
- Produces: `GET Sentinel/incidents` (replaces the dashboard's primary data source — keep `GET Sentinel/diagnoses` too, for now, as the drill-down detail source an incident's evidence expands into), `POST Sentinel/incidents/{id}/acknowledge`, `POST Sentinel/incidents/{id}/resolve`. All three keep the existing `[Authorize(Policy = Policies.RequiresElevation)]` gate already on this controller.

- [ ] Standard ASP.NET controller-action task — write it directly against the existing controller's style, add a small integration-style test using an in-memory `SentinelDatabase` (temp file, same pattern as every other repository test in this project), run full suite, commit.

## Task B5: Dashboard renders Incidents (grouped, with Acknowledge/Resolve)

**Files:**
- Modify: `Jellyfin.Plugin.Sentinel/Configuration/configPage.html`

- [ ] Switch the dashboard's data source from `Sentinel/diagnoses` to `Sentinel/incidents`. Each row shows Code (translated explanation via the last-linked diagnosis — the API response for `GetRecent` incidents should include the most recent linked diagnosis's translated Explanation/Recommendation, following Task A5's pattern), OccurrenceCount, Status badge, First/Last seen, and Acknowledge/Resolve buttons that POST to the new endpoints and then reload the list. This task depends on Workstream E's visual modernization for styling — implement the functional wiring here, defer final visual polish to Task E2 if the two land close together, but do not block one on the other; a plain-but-working version now is better than nothing.

---

# Workstream C: Notification Delivery (master plan Section 29, MVP subset: Webhook, Discord, Telegram, Email)

## Task C1: Verify current Discord/Telegram rate limits before hardcoding retry backoff

**This is a research task, not a code task — do it before C3/C4.** The master plan itself flags this as unverified (Section 33 Risks). Before writing retry/backoff logic:
- Fetch Discord's current webhook rate-limit documentation (WebFetch `https://discord.com/developers/docs/topics/rate-limits` or the current equivalent URL) and record the actual per-webhook limit.
- Fetch Telegram Bot API's current rate-limit guidance (WebFetch `https://core.telegram.org/bots/faq#my-bot-is-hitting-limits-how-do-i-avoid-this` or current equivalent) and record the actual per-chat/global limits.
- Write findings as a short comment block at the top of `NotificationChannels/DiscordNotificationChannel.cs` and `NotificationChannels/TelegramNotificationChannel.cs` (created in C3/C4) citing the source URL and date checked — matching this project's established "verified, not assumed" documentation style. Do not hardcode a specific backoff number without this citation.

## Task C2: `INotificationChannel` abstraction + generic Webhook implementation

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Notifications/INotificationChannel.cs`, `Jellyfin.Plugin.Sentinel/Notifications/NotificationMessage.cs`, `Jellyfin.Plugin.Sentinel/Notifications/WebhookNotificationChannel.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Notifications/WebhookNotificationChannelTests.cs`

**Interfaces:**

```csharp
namespace Jellyfin.Plugin.Sentinel.Notifications;

/// <summary>A single notification-worthy event, already translated and evidence-attached.</summary>
public sealed class NotificationMessage
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required string Severity { get; init; } // maps from Confidence — see Task C6
    public required string IncidentUrl { get; init; } // deep link to the dashboard, if constructible
}

/// <summary>One outbound notification destination.</summary>
public interface INotificationChannel
{
    string ChannelName { get; }
    Task<bool> SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}
```

`WebhookNotificationChannel` POSTs `message` as JSON to a configured URL using `IHttpClientFactory` (register a named client `"Sentinel.Notifications"` with a 10-second timeout in `PluginServiceRegistrator.cs` — verify `IHttpClientFactory` is available via Jellyfin's DI container the same way `ILibraryManager` etc. already are; if not directly injectable, use `new HttpClient()` with an explicit `Timeout` set, documenting why). On non-2xx response or exception, log via `[LoggerMessage]` (existing pattern) and return `false` — never throw out of `SendAsync`.

- [ ] Standard TDD cycle: failing test with a mocked `HttpMessageHandler` (see `Moq.Protected` or a minimal fake `HttpMessageHandler` subclass — this project doesn't have an existing HTTP-mocking pattern yet, so the implementer establishes one; keep it simple, don't add a new NuGet package for this if a 15-line fake handler suffices), implement, pass, build, commit.

## Task C3: Discord notification channel

**Files:** Create `Jellyfin.Plugin.Sentinel/Notifications/DiscordNotificationChannel.cs` + test.

Discord webhooks accept the same "POST JSON to a URL" shape as the generic webhook, with a specific payload structure (`{"content": ..., "embeds": [...]}"`) — verify the current Discord webhook payload schema via WebFetch against Discord's current developer docs before implementing (do not assume a remembered schema is still current), then implement following the same `INotificationChannel` shape as Task C2, with retry using the backoff value researched in C1.

## Task C4: Telegram notification channel

**Files:** Create `Jellyfin.Plugin.Sentinel/Notifications/TelegramNotificationChannel.cs` + test.

Telegram Bot API `sendMessage` (`https://api.telegram.org/bot<token>/sendMessage`) — verify current parameter names/limits via WebFetch against Telegram's current Bot API docs before implementing. Config needs `BotToken` + `ChatId` (add to `PluginConfiguration`, mask `BotToken` in any place this project already sanitizes sensitive config for display — check whether `Sentinel` has an equivalent of Questorr's `configSanitize.js` pattern; if not, this task adds the minimal version needed: never return `BotToken`/SMTP password/webhook URLs verbatim from any GET endpoint that echoes configuration back to the dashboard).

## Task C5: Email notification channel

**Files:** Create `Jellyfin.Plugin.Sentinel/Notifications/EmailNotificationChannel.cs` + test.

`System.Net.Mail.SmtpClient` is marked obsolete/not recommended by Microsoft for new code; before adding a new dependency, check whether `MailKit` (MIT-licensed) is an acceptable addition given this project's existing dependency posture (currently: `Microsoft.Data.Sqlite` + `SQLitePCLRaw.*` only, both already justified in `build.yaml`'s own comments) — if adding `MailKit`, it needs the same `build.yaml` `artifacts:` treatment as the SQLite packages (native/managed DLL enumeration, verified via `dotnet publish` output inspection, exactly like the SQLite precedent this project already went through twice). This is very likely to need its own dedicated task/PR given how much process the SQLite native-loading issue took earlier in this project — do not rush the packaging side.

## Task C6: Severity mapping, dedup-aware alerting, and config UI

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Notifications/NotificationDispatcher.cs`
- Modify: `Jellyfin.Plugin.Sentinel/Configuration/PluginConfiguration.cs`, `Jellyfin.Plugin.Sentinel/Configuration/configPage.html`, `Jellyfin.Plugin.Sentinel/Collector/PlaybackCollectorHostedService.cs` (or wherever `IncidentRepository.UpsertOnDiagnosis` is called — the dispatcher hooks in right after)

**Behavior:**
- `NotificationDispatcher` is called once per `Incident` create-or-reopen event (NOT on every `OccurrenceCount` increment of an already-`Detected`/already-notified incident — this is the "dedup-aware alerting" requirement from Section 17/29: one notification per new incident, not one per matching diagnosis).
- Confidence → severity mapping: `Confirmed`/`VeryLikely` → `"high"`, `Likely` → `"medium"`, `Possible`/`Unknown` → `"low"`. Config has a minimum-severity threshold per channel (default: notify on all).
- Config UI: enable/disable per channel, per-channel settings fields, and a "Send test notification" button per channel (mandatory per Section 31's MVP definition — do not ship a channel without one) that calls a new `POST Sentinel/notifications/test/{channel}` endpoint.

---

# Workstream D: Plugin-Update / Restart-Pending Notification (new feature, not in the master plan)

Verified during planning against Jellyfin v12.1 source: `MediaBrowser.Common.Updates.IInstallationManager.GetAvailablePluginUpdates(CancellationToken)` returns `Task<IEnumerable<InstallationInfo>>`; `MediaBrowser.Common.IApplicationHost.HasPendingRestart` (bool) and `HasPendingRestartChanged` (event) are both real, and `IServerApplicationHost : IApplicationHost`, so the `applicationHost` parameter already passed into `PluginServiceRegistrator.RegisterServices` exposes both directly.

**Important semantic distinction the implementer must preserve, not conflate:** "a plugin update is available" and "a restart is pending" are two independent signals — `HasPendingRestart` can be true for reasons unrelated to plugins, and an available update does not by itself set it (only installing one does, including an automatic install if a plugin's manifest has `AutoUpdate = true` — verified in `PluginManager.cs`'s `PopulateManifest`). Build two distinct notifications, not one that overclaims a causal link that isn't always true: (1) "N plugin update(s) available" (informational, periodic check), (2) "Server restart is pending" (reacts to `HasPendingRestartChanged`, mentions it may be due to a plugin update if one was recently detected as installed — check via timestamp correlation, not assumption).

## Task D1: `PluginUpdateMonitorHostedService`

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Collector/PluginUpdateMonitorHostedService.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Collector/PluginUpdateMonitorHostedServiceTests.cs`

**Interfaces:**
- Consumes: `IInstallationManager` (register/resolve via Jellyfin's DI — verify it's already registered by Jellyfin's own host, which it should be as a core service; do not register a second instance), `IApplicationHost`/`IServerApplicationHost`, `NotificationDispatcher` (Workstream C).
- Produces: an `IHostedService` (same shape as `PlaybackCollectorHostedService`) that, on a periodic timer (default hourly, configurable), calls `GetAvailablePluginUpdates()` and diffs the result against a "last notified set" (new small table `NotifiedPluginUpdate(PluginId TEXT, Version TEXT, NotifiedAtUtc TEXT, PRIMARY KEY (PluginId, Version))` — same dedup principle as Incidents: never re-notify for the same plugin+version twice) to decide whether to fire a new "updates available" notification. Separately subscribes to `HasPendingRestartChanged` and fires a "restart pending" notification the first time it flips to true after plugin startup (track a simple `bool _lastKnownPendingRestart` field, same pattern as everywhere else in this codebase that tracks "did this just change").

- [ ] Standard TDD cycle using a mocked `IInstallationManager`/`IApplicationHost` (Moq, matching every other hosted-service test in this project), verify dedup (same update isn't notified twice), verify the restart-pending edge-trigger (only fires on the false→true transition, not on every event raise if Jellyfin fires the event more than once for the same state — check this by testing "event fires twice while already true" produces exactly one notification), register in `PluginServiceRegistrator.cs`, run full suite, commit.

---

# Workstream E: Dashboard Modernization + Media Display Fix

## Task E1: "Series - Episode" media name

**Files:** Modify `Jellyfin.Plugin.Sentinel/Api/SentinelController.cs`

Verified: `MediaBrowser.Controller.Entities.TV.Episode` implements `IHasSeries` (`MediaBrowser.Controller.Entities.IHasSeries`), whose `FindSeriesName()` method correctly falls back between a loaded `Series` navigation property and a cached `SeriesName` field — use `FindSeriesName()`, not the raw `SeriesName` property, to get the fallback behavior for free.

```csharp
private string ResolveMediaName(string itemId)
{
    if (!Guid.TryParse(itemId, out var guid))
    {
        return itemId;
    }

    var item = _libraryManager.GetItemById(guid);
    if (item is null)
    {
        return itemId;
    }

    if (item is MediaBrowser.Controller.Entities.IHasSeries hasSeries)
    {
        var seriesName = hasSeries.FindSeriesName();
        return string.IsNullOrEmpty(seriesName) ? item.Name : $"{seriesName} - {item.Name}";
    }

    return item.Name;
}
```

- [ ] Write a test constructing a real `Episode` (matching how `Movie` is already constructed in existing tests) with `SeriesName` set, assert the "Series - Episode" format; also test a `Movie` (not `IHasSeries`) still returns just the plain name. Run full suite, commit.

## Task E2: Visual modernization of `configPage.html`

**Files:** Modify `Jellyfin.Plugin.Sentinel/Configuration/configPage.html`

No new framework. Use Jellyfin's own existing CSS custom properties/classes where they exist (check a couple of other well-known Jellyfin plugin config pages' rendered DOM for which `emby-*`/theme-variable classes are available in the config-page context — the plugin has no browser access itself, so the implementer should read this project's own earlier `configPage.html` history for what worked, and keep new markup additive: card-style incident rows instead of a raw `<table>`, status badges (colored per `IncidentStatus`), a confidence-colored left border per row, responsive layout for narrow windows. This is a visual/CSS task with no new automated test — verify by having the user (or, if browser tooling is available in-session, Claude-in-Chrome against a real running Jellyfin instance) load the page and confirm it renders correctly; do not claim this task complete from code inspection alone, per this project's own standing rule about UI changes.

## Task E3: Dashboard JS consumes translated strings

**Files:** Modify `Jellyfin.Plugin.Sentinel/Configuration/configPage.html`

On `pageshow`, fetch `Sentinel/ui-strings` (Task A5) alongside `Sentinel/incidents`, and use the returned `UI_*` values for every hardcoded label currently in the JS (table headers, loading/empty/error text). Add a language `<select>` to the config page itself (this is the actual settings UI for `PluginConfiguration.Language` from Task A1 — needs its own `GET`/`POST Sentinel/config` pair if one doesn't already exist; check `Plugin.cs` for how Jellyfin plugin configuration pages conventionally save settings — likely via `ApiClient.updatePluginConfiguration`, a jellyfin-web built-in, not a custom endpoint; verify against the official `jellyfin-plugin-template`'s own configPage.html pattern before building a custom one).

---

# Workstream F: Security Review and Comprehensive Testing

## Task F1: Independent security review

Dispatch a security-focused review (the `ecc:security-reviewer` agent or the `security-review` skill, whichever is available in the executing session) across the full diff of Workstreams A-E once merged, explicitly covering:
- SQL injection (confirm every new query in `IncidentRepository`/`NotifiedPluginUpdate` uses parameters, never string interpolation — matching the existing pattern everywhere else in this codebase).
- Outbound-request handling in Workstream C: confirm webhook/Discord/Telegram/Email destinations are only ever admin-configured (never derived from playback/session data), confirm no credential (BotToken, SMTP password, webhook URL) is ever echoed back in a GET response or logged at Info/Debug level.
- XSS in `configPage.html`: confirm every new dynamic value (Incident fields, translated strings, plugin-update notification content) goes through the existing `escapeHtmlText` helper before insertion into `innerHTML`, exactly as the current dashboard code already does — a new field is exactly the kind of thing that's easy to add without remembering to escape it.
- Confirm `DropColumnIfExists`'s dynamic SQL (Task A4) interpolates only the fixed, hardcoded table/column name literals this plan specifies — never a caller-supplied value — since SQLite parameterization doesn't cover identifiers.

## Task F2: Full-suite regression pass + live-server verification checklist

Run the complete test suite, confirm 0 warnings/0 failures. Then produce the exact checklist the user asked for at the end of this whole plan (see "Final Report" below) — this is not a task with code, it's the deliverable the user explicitly requested: a written list of what needs a real Jellyfin server to verify (notification delivery per channel, language switching visibly changing dashboard text, Incident dedup/reopen behavior over multiple real playbacks, Series-Episode display) versus what's covered by the automated suite alone.

---

# Final Report (produce this after every workstream is merged and released)

Per the user's explicit request, once everything above is implemented, tested, and released, report back with:
1. A version-by-version changelog of what shipped in this pass.
2. An explicit list of anything that only works under specific conditions/dependencies (e.g., "Email notifications require MailKit, added as a new dependency — see build.yaml"; "Discord/Telegram rate-limit backoff values are only as current as the date they were verified in Task C1 — recheck if delivery starts failing months from now"; any Jellyfin API used that could differ on server versions other than the verified v12.1 target).
3. The live-server verification checklist from Task F2, for the user to work through and report results/problems back, as they explicitly asked to do.
