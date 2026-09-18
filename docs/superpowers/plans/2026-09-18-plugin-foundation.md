# Sentinel Plugin Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the Jellyfin Sentinel plugin skeleton and deliver a working, end-to-end Playback Doctor core — a real Jellyfin 12.1 plugin that observes `PlaybackStopped` events, runs them through a deterministic rule engine built on the verified `TranscodeReason` flags enum, and persists evidence-backed diagnoses to its own SQLite database. This is independently testable and valuable without the Notification/Dashboard layer, which is a separate, later plan.

**Architecture:** A single Jellyfin plugin project (`Jellyfin.Plugin.Sentinel`) registers a background `IHostedService` via `IPluginServiceRegistrator`. The service subscribes to `ISessionManager.PlaybackStopped`, normalizes the event into a plain `PlaybackEvent` domain object, runs it through a `RuleEngine` of `DiagnosticRule` predicates matched against the real `TranscodeReason` bit-flags, and writes both the raw event and any resulting `Diagnosis` rows to a plugin-owned SQLite database (via `Microsoft.Data.Sqlite`, no ORM).

**Tech Stack:** .NET 10 (`net10.0`), C#, `Jellyfin.Controller`/`Jellyfin.Model` NuGet 12.1.0, `Microsoft.Data.Sqlite` 10.0.12, xUnit + Moq for tests.

**Spec:** `JELLYFIN_SENTINEL_MASTER_PLAN.md` (Sections 3, 11, 12, 14, 18, 21) and `JELLYFIN_ECOSYSTEM_GAP_ANALYSIS.md` (context only, no tasks derive from it directly).

## Global Constraints

- Target ABI: `12.1.0.0`, `TargetFramework=net10.0`. Verified via NuGet nuspec for `Jellyfin.Controller` 12.1.0 (`targetFramework="net10.0"`) and `global.json` at `jellyfin/jellyfin` tag `v12.1` (`"sdk": {"version": "10.0.0"}`). The official `jellyfin-plugin-template` repo itself is still pinned to `net9.0`/10.11.5 as of this writing — do not copy its `.csproj` verbatim, the versions below are correct and template's are stale.
- License: **GPL-3.0**. `Jellyfin.Controller`/`Jellyfin.Model` 12.1.0 are licensed `GPL-3.0-only` per their NuGet nuspec (`<license type="expression">GPL-3.0-only</license>`) — this is a confirmed, unambiguous copyleft dependency, not just an ecosystem convention.
- No ORM/EF Core — use `Microsoft.Data.Sqlite` directly (master plan Section 47: avoid unnecessary dependencies; the schema is small and stable enough not to need migrations tooling yet).
- `Nullable` enabled, `TreatWarningsAsErrors=true`, `GenerateDocumentationFile=true` — matches the verified official template convention.
- `Confidence` is a 5-value enum, never a float (Section 16). Every `Diagnosis` must carry a non-empty evidence list (Section 15).
- **No Claude/Claude Code attribution anywhere in this repository** — no `Co-Authored-By` trailers in commits, no "Generated with Claude Code" in any commit message, PR, or file. This is an explicit, standing instruction from the repository owner and overrides any default tooling behavior.
- The plugin's own SQLite database lives at `Plugin.Instance!.DataFolderPath` (verified: `BasePlugin.DataFolderPath` is a real, populated property — this is the same storage pattern the official Playback Reporting plugin uses).

---

### Task 1: Repository & Plugin Scaffold

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Jellyfin.Plugin.Sentinel.csproj`
- Create: `Jellyfin.Plugin.Sentinel/build.yaml`
- Create: `Jellyfin.Plugin.Sentinel/Plugin.cs`
- Create: `Jellyfin.Plugin.Sentinel/Configuration/PluginConfiguration.cs`
- Create: `Jellyfin.Plugin.Sentinel/Configuration/configPage.html`
- Create: `Directory.Build.props`
- Create: `.gitignore`
- Create: `LICENSE`
- Create/overwrite: `README.md`

**Interfaces:**
- Produces: `Jellyfin.Plugin.Sentinel.Plugin` (public class, `Id = Guid.Parse("4b5e3a79-7798-4066-841e-db7eb0125dc9")`, `Name = "Sentinel"`) — later tasks reference `Plugin.Instance!.DataFolderPath`.
- Produces: `Jellyfin.Plugin.Sentinel.Configuration.PluginConfiguration` (empty `BasePluginConfiguration` subclass for now — settings are added when the Notification plan needs them, not before, per YAGNI).

- [ ] **Step 1: Create the project directory and initialize the class library**

Run from the repository root (`C:\Users\phili\SynologyDrive\Technik\Vibe-Coding\claude-code\GitHub\Sentinel`):

```bash
dotnet new classlib -f net10.0 -n Jellyfin.Plugin.Sentinel -o Jellyfin.Plugin.Sentinel
rm Jellyfin.Plugin.Sentinel/Class1.cs
```

- [ ] **Step 2: Replace the generated `.csproj` with the Jellyfin-plugin-shaped version**

Overwrite `Jellyfin.Plugin.Sentinel/Jellyfin.Plugin.Sentinel.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>Jellyfin.Plugin.Sentinel</RootNamespace>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Nullable>enable</Nullable>
    <AnalysisMode>AllEnabledByDefault</AnalysisMode>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Jellyfin.Controller" Version="12.1.0">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
    <PackageReference Include="Jellyfin.Model" Version="12.1.0">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.12" />
  </ItemGroup>

  <ItemGroup>
    <None Remove="Configuration\configPage.html" />
    <EmbeddedResource Include="Configuration\configPage.html" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Add `Directory.Build.props`**

Create `Directory.Build.props` at the repository root (sibling of `Jellyfin.Plugin.Sentinel/`):

```xml
<Project>
    <PropertyGroup>
        <Version>0.1.0.0</Version>
        <AssemblyVersion>0.1.0.0</AssemblyVersion>
        <FileVersion>0.1.0.0</FileVersion>
    </PropertyGroup>
</Project>
```

- [ ] **Step 4: Add `build.yaml`**

Create `Jellyfin.Plugin.Sentinel/build.yaml`:

```yaml
---
name: "Sentinel"
guid: "4b5e3a79-7798-4066-841e-db7eb0125dc9"
version: "0.1.0.0"
targetAbi: "12.1.0.0"
framework: "net10.0"
overview: "Evidence-based root-cause diagnosis for Jellyfin playback problems"
description: >
  Sentinel watches Jellyfin playback sessions and explains, with evidence,
  why a specific transcode or playback problem happened — not just that
  it happened.
category: "General"
owner: "Jellyforge-Dev"
artifacts:
- "Jellyfin.Plugin.Sentinel.dll"
changelog: >
  Initial foundation: playback observation, rule engine, SQLite persistence.
```

- [ ] **Step 5: Add `Configuration/PluginConfiguration.cs`**

```csharp
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Sentinel.Configuration;

/// <summary>
/// Plugin configuration. Empty for now — settings are added when a later
/// task actually needs one (notification channels, retention, etc.).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
}
```

- [ ] **Step 6: Add `Configuration/configPage.html`**

```html
<!doctype html>
<html>
<head>
    <title>Sentinel</title>
</head>
<body>
    <div id="SentinelConfigPage" data-role="page" class="page type-interior pluginConfigurationPage">
        <div data-role="content">
            <div class="content-primary">
                <h1>Jellyfin Sentinel</h1>
                <p>Sentinel is running and observing playback sessions. The incident dashboard is not implemented yet — this page will be replaced by the Dashboard plan.</p>
            </div>
        </div>
    </div>
</body>
</html>
```

- [ ] **Step 7: Add `Plugin.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Sentinel.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Sentinel;

/// <summary>
/// The main Sentinel plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Sentinel";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("4b5e3a79-7798-4066-841e-db7eb0125dc9");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
```

- [ ] **Step 8: Build and verify**

```bash
dotnet build Jellyfin.Plugin.Sentinel/Jellyfin.Plugin.Sentinel.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. If .NET 10 isn't on your `PATH`, use the locally-installed copy explicitly: `"$env:LOCALAPPDATA/dotnet10/dotnet.exe" build ...` (PowerShell) or the equivalent path in bash.

- [ ] **Step 9: Add `.gitignore`**

```gitignore
bin/
obj/
*.user
.vs/
.idea/
*.suo
*.db
*.db-journal
```

- [ ] **Step 10: Add `LICENSE` (GPL-3.0 full text)**

Copy the verified, authentic GPLv3 text from the official template you already have locally cloned in this session's scratchpad (`jellyfin-plugin-template/LICENSE`) to the repository root as `LICENSE`. Do not hand-type license text.

- [ ] **Step 11: Write `README.md`**

```markdown
# Jellyfin Sentinel

Evidence-based root-cause diagnosis and incident notifications for self-hosted Jellyfin servers.

Sentinel watches Jellyfin playback sessions and explains — with evidence, not guesses — why a
specific transcode or playback problem happened, tracks it as a deduplicated incident, and
(in a later milestone) can notify you about it.

**Status:** early development. The Playback Doctor core (collector, rule engine, SQLite
persistence) is being built first; the notification and dashboard layers follow.

See [`JELLYFIN_SENTINEL_MASTER_PLAN.md`](./JELLYFIN_SENTINEL_MASTER_PLAN.md) for the full
architecture and product plan, and [`JELLYFIN_ECOSYSTEM_GAP_ANALYSIS.md`](./JELLYFIN_ECOSYSTEM_GAP_ANALYSIS.md)
for the ecosystem research behind it.

## License

GPL-3.0 — see [`LICENSE`](./LICENSE). Sentinel links against Jellyfin's `GPL-3.0-only`
NuGet packages (`Jellyfin.Controller`, `Jellyfin.Model`), so the compiled plugin is bound by
GPLv3 regardless of the repository license; GPL-3.0 is used here to make that explicit.
```

- [ ] **Step 12: Initialize git and push**

```bash
git init
git add .gitignore LICENSE README.md Directory.Build.props Jellyfin.Plugin.Sentinel/ JELLYFIN_SENTINEL_MASTER_PLAN.md JELLYFIN_ECOSYSTEM_GAP_ANALYSIS.md
git commit -m "feat: scaffold Sentinel plugin targeting Jellyfin 12.1 (net10.0)"
git branch -M main
git remote add origin https://github.com/Jellyforge-Dev/Sentinel.git
git push -u origin main
```

Before running this, confirm `git config user.name` / `user.email` are set to your own identity (not an AI assistant), and do not add any `Co-Authored-By` or "Generated with" trailer to the commit message — this repository must not show Claude as a contributor, per standing instruction.

---

### Task 2: Test Project Scaffold

**Files:**
- Create: `tests/Jellyfin.Plugin.Sentinel.Tests/Jellyfin.Plugin.Sentinel.Tests.csproj`
- Create: `tests/Jellyfin.Plugin.Sentinel.Tests/SmokeTests.cs`
- Create: `Jellyfin.Plugin.Sentinel.sln`

**Interfaces:**
- Consumes: `Jellyfin.Plugin.Sentinel.csproj` (Task 1) via project reference.

- [ ] **Step 1: Scaffold the xUnit test project**

```bash
dotnet new xunit -n Jellyfin.Plugin.Sentinel.Tests -o tests/Jellyfin.Plugin.Sentinel.Tests --framework net10.0
dotnet add tests/Jellyfin.Plugin.Sentinel.Tests reference Jellyfin.Plugin.Sentinel/Jellyfin.Plugin.Sentinel.csproj
dotnet add tests/Jellyfin.Plugin.Sentinel.Tests package Moq
```

- [ ] **Step 2: Create the solution file and add both projects**

```bash
dotnet new sln -n Jellyfin.Plugin.Sentinel
dotnet sln add Jellyfin.Plugin.Sentinel/Jellyfin.Plugin.Sentinel.csproj
dotnet sln add tests/Jellyfin.Plugin.Sentinel.Tests/Jellyfin.Plugin.Sentinel.Tests.csproj
```

- [ ] **Step 3: Replace the generated test file with a trivial smoke test**

Overwrite `tests/Jellyfin.Plugin.Sentinel.Tests/SmokeTests.cs` (delete the auto-generated `UnitTest1.cs` if `dotnet new xunit` created one with that name instead):

```csharp
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests;

public class SmokeTests
{
    [Fact]
    public void TestProject_IsWired_Correctly()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test Jellyfin.Plugin.Sentinel.sln
```

Expected: `Passed! - Failed: 0, Passed: 1`.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.Sentinel.sln tests/
git commit -m "chore: add xUnit test project"
```

---

### Task 3: Domain Types & Playback Event Normalization

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Domain/Confidence.cs`
- Create: `Jellyfin.Plugin.Sentinel/Domain/PlaybackEvent.cs`
- Create: `Jellyfin.Plugin.Sentinel/Domain/Diagnosis.cs`
- Create: `Jellyfin.Plugin.Sentinel/Domain/DiagnosticRule.cs`
- Create: `Jellyfin.Plugin.Sentinel/Diagnostics/PlaybackEventFactory.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Diagnostics/PlaybackEventFactoryTests.cs`

**Interfaces:**
- Produces: `PlaybackEventFactory.FromEventArgs(PlaybackStopEventArgs args) : PlaybackEvent?` — consumed by Task 7's hosted service.
- Produces: `PlaybackEvent { SessionId, ItemId, Client, DeviceName, PlayMethod, TranscodeReasons, VideoCodec, AudioCodec, SubtitleFormat, CreatedAtUtc }` — consumed by Task 4 (persistence) and Task 5 (rule engine).
- Produces: `Diagnosis { Code, Confidence, Evidence, Explanation, Recommendation }` — consumed by Task 4 and Task 5.

- [ ] **Step 1: Write the domain types (no test needed — plain data holders)**

`Jellyfin.Plugin.Sentinel/Domain/Confidence.cs`:

```csharp
namespace Jellyfin.Plugin.Sentinel.Domain;

/// <summary>
/// How certain a diagnosis is. Never a numeric percentage — there is no
/// statistically defensible way to derive one from a handful of rule matches.
/// </summary>
public enum Confidence
{
    Unknown,
    Possible,
    Likely,
    VeryLikely,
    Confirmed
}
```

`Jellyfin.Plugin.Sentinel/Domain/PlaybackEvent.cs`:

```csharp
using System;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.Sentinel.Domain;

/// <summary>
/// A normalized record of one finished playback session, independent of
/// Jellyfin's own session/event types.
/// </summary>
public sealed class PlaybackEvent
{
    public required string SessionId { get; init; }

    public required string ItemId { get; init; }

    public required string Client { get; init; }

    public required string DeviceName { get; init; }

    public PlayMethod? PlayMethod { get; init; }

    public TranscodeReason TranscodeReasons { get; init; }

    public string? VideoCodec { get; init; }

    public string? AudioCodec { get; init; }

    public string? SubtitleFormat { get; init; }

    public required DateTime CreatedAtUtc { get; init; }
}
```

`Jellyfin.Plugin.Sentinel/Domain/Diagnosis.cs`:

```csharp
using System.Collections.Generic;

namespace Jellyfin.Plugin.Sentinel.Domain;

/// <summary>
/// One rule engine result. Never constructed without evidence.
/// </summary>
public sealed class Diagnosis
{
    public required string Code { get; init; }

    public required Confidence Confidence { get; init; }

    public required IReadOnlyList<string> Evidence { get; init; }

    public required string Explanation { get; init; }

    public required string Recommendation { get; init; }
}
```

`Jellyfin.Plugin.Sentinel/Domain/DiagnosticRule.cs`:

```csharp
using System;

namespace Jellyfin.Plugin.Sentinel.Domain;

/// <summary>
/// One deterministic rule: if <see cref="Predicate"/> matches a <see cref="PlaybackEvent"/>,
/// the rule engine produces a <see cref="Diagnosis"/> using the other fields.
/// </summary>
/// <param name="Code">Stable machine-readable diagnosis code, e.g. "VIDEO_CODEC_UNSUPPORTED".</param>
/// <param name="Predicate">Whether this rule matches a given playback event.</param>
/// <param name="Confidence">How certain this diagnosis is when the predicate matches.</param>
/// <param name="Explain">Produces the plain-language explanation for a matching event.</param>
/// <param name="Recommendation">What the admin should do about it, if anything.</param>
public sealed record DiagnosticRule(
    string Code,
    Func<PlaybackEvent, bool> Predicate,
    Confidence Confidence,
    Func<PlaybackEvent, string> Explain,
    string Recommendation);
```

- [ ] **Step 2: Write the failing test for the normalization factory**

Create `tests/Jellyfin.Plugin.Sentinel.Tests/Diagnostics/PlaybackEventFactoryTests.cs`:

```csharp
using System;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Diagnostics;

public class PlaybackEventFactoryTests
{
    [Fact]
    public void FromEventArgs_MapsTranscodeReasonsAndClientFromSession()
    {
        var sessionManager = new Mock<ISessionManager>();
        var session = new SessionInfo(sessionManager.Object, NullLogger.Instance)
        {
            Id = "session-1",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
            TranscodingInfo = new TranscodingInfo
            {
                TranscodeReasons = TranscodeReason.SubtitleCodecNotSupported | TranscodeReason.VideoCodecNotSupported,
                VideoCodec = "hevc",
                AudioCodec = "eac3"
            }
        };

        var args = new PlaybackStopEventArgs
        {
            Session = session,
            Item = new Movie { Id = Guid.NewGuid() }
        };

        var result = PlaybackEventFactory.FromEventArgs(args);

        Assert.NotNull(result);
        Assert.Equal("session-1", result!.SessionId);
        Assert.Equal("Fire TV", result.Client);
        Assert.Equal("Living Room TV", result.DeviceName);
        Assert.Equal(PlayMethod.Transcode, result.PlayMethod);
        Assert.True(result.TranscodeReasons.HasFlag(TranscodeReason.VideoCodecNotSupported));
        Assert.True(result.TranscodeReasons.HasFlag(TranscodeReason.SubtitleCodecNotSupported));
        Assert.Equal("hevc", result.VideoCodec);
    }

    [Fact]
    public void FromEventArgs_ReturnsNull_WhenSessionIsMissing()
    {
        var args = new PlaybackStopEventArgs
        {
            Session = null,
            Item = new Movie { Id = Guid.NewGuid() }
        };

        var result = PlaybackEventFactory.FromEventArgs(args);

        Assert.Null(result);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test tests/Jellyfin.Plugin.Sentinel.Tests --filter "FullyQualifiedName~PlaybackEventFactoryTests"
```

Expected: compile error — `PlaybackEventFactory` does not exist yet.

- [ ] **Step 4: Implement `PlaybackEventFactory`**

Create `Jellyfin.Plugin.Sentinel/Diagnostics/PlaybackEventFactory.cs`:

```csharp
using System;
using Jellyfin.Plugin.Sentinel.Domain;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Sentinel.Diagnostics;

/// <summary>
/// Converts Jellyfin's own event args into Sentinel's normalized <see cref="PlaybackEvent"/>.
/// </summary>
public static class PlaybackEventFactory
{
    /// <summary>
    /// Builds a <see cref="PlaybackEvent"/> from a stopped-playback event, or null if the
    /// event doesn't carry enough information to diagnose (no session or no item).
    /// </summary>
    /// <remarks>
    /// <see cref="PlaybackEvent.SubtitleFormat"/> is intentionally left null here — extracting
    /// the actively-used subtitle stream requires matching <c>PlayState.SubtitleStreamIndex</c>
    /// against <c>Item.MediaStreams</c>, which is deferred to the plan that adds the
    /// subtitle-burn-in rule so it gets its own reviewed task and test coverage.
    /// </remarks>
    public static PlaybackEvent? FromEventArgs(PlaybackStopEventArgs args)
    {
        var session = args.Session;
        if (session is null || args.Item is null)
        {
            return null;
        }

        var transcodingInfo = session.TranscodingInfo;

        return new PlaybackEvent
        {
            SessionId = session.Id ?? string.Empty,
            ItemId = args.Item.Id.ToString(),
            Client = session.Client ?? string.Empty,
            DeviceName = session.DeviceName ?? string.Empty,
            PlayMethod = session.PlayState?.PlayMethod,
            TranscodeReasons = transcodingInfo?.TranscodeReasons ?? default,
            VideoCodec = transcodingInfo?.VideoCodec,
            AudioCodec = transcodingInfo?.AudioCodec,
            SubtitleFormat = null,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/Jellyfin.Plugin.Sentinel.Tests --filter "FullyQualifiedName~PlaybackEventFactoryTests"
```

Expected: `Passed! - Failed: 0, Passed: 2`.

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.Sentinel/Domain/ Jellyfin.Plugin.Sentinel/Diagnostics/PlaybackEventFactory.cs tests/Jellyfin.Plugin.Sentinel.Tests/Diagnostics/
git commit -m "feat: add domain types and playback event normalization"
```

---

### Task 4: SQLite Persistence Layer

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Persistence/SentinelDatabase.cs`
- Create: `Jellyfin.Plugin.Sentinel/Persistence/PlaybackEventRepository.cs`
- Create: `Jellyfin.Plugin.Sentinel/Persistence/DiagnosisRepository.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Persistence/SentinelDatabaseTests.cs`

**Interfaces:**
- Consumes: `PlaybackEvent`, `Diagnosis` (Task 3).
- Produces: `SentinelDatabase(string databasePath)`, `.OpenConnection() : SqliteConnection` — consumed by both repositories and by Task 7's DI registration.
- Produces: `PlaybackEventRepository.Insert(PlaybackEvent) : long` (returns the new row id).
- Produces: `DiagnosisRepository.Insert(long playbackEventId, Diagnosis)` and `.GetAllForPlaybackEvent(long playbackEventId) : IReadOnlyList<(string Code, string Confidence)>`.

- [ ] **Step 1: Write the failing round-trip test**

Create `tests/Jellyfin.Plugin.Sentinel.Tests/Persistence/SentinelDatabaseTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Sentinel.Domain;
using Jellyfin.Plugin.Sentinel.Persistence;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Persistence;

public class SentinelDatabaseTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SentinelDatabase _database;

    public SentinelDatabaseTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"sentinel-test-{Guid.NewGuid()}.db");
        _database = new SentinelDatabase(_databasePath);
    }

    [Fact]
    public void InsertAndReadBack_PlaybackEventAndDiagnosis_RoundTrips()
    {
        var playbackEventRepository = new PlaybackEventRepository(_database);
        var diagnosisRepository = new DiagnosisRepository(_database);

        var playbackEvent = new PlaybackEvent
        {
            SessionId = "session-1",
            ItemId = "item-1",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode,
            TranscodeReasons = MediaBrowser.Model.Session.TranscodeReason.VideoCodecNotSupported,
            VideoCodec = "hevc",
            AudioCodec = "eac3",
            SubtitleFormat = null,
            CreatedAtUtc = DateTime.UtcNow
        };

        var playbackEventId = playbackEventRepository.Insert(playbackEvent);

        var diagnosis = new Diagnosis
        {
            Code = "VIDEO_CODEC_UNSUPPORTED",
            Confidence = Confidence.Confirmed,
            Evidence = new[] { "TranscodeReasons = VideoCodecNotSupported" },
            Explanation = "Test explanation",
            Recommendation = "Test recommendation"
        };

        diagnosisRepository.Insert(playbackEventId, diagnosis);

        var stored = diagnosisRepository.GetAllForPlaybackEvent(playbackEventId);

        Assert.Single(stored);
        Assert.Equal("VIDEO_CODEC_UNSUPPORTED", stored[0].Code);
        Assert.Equal("Confirmed", stored[0].Confidence);
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Jellyfin.Plugin.Sentinel.Tests --filter "FullyQualifiedName~SentinelDatabaseTests"
```

Expected: compile error — `SentinelDatabase`, `PlaybackEventRepository`, `DiagnosisRepository` don't exist yet.

- [ ] **Step 3: Implement `SentinelDatabase`**

Create `Jellyfin.Plugin.Sentinel/Persistence/SentinelDatabase.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Sentinel.Persistence;

/// <summary>
/// Sentinel's own SQLite database — fully separate from Jellyfin's database.
/// </summary>
public sealed class SentinelDatabase
{
    private readonly string _connectionString;

    public SentinelDatabase(string databasePath)
    {
        _connectionString = $"Data Source={databasePath}";
        Initialize();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS PlaybackEvent (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                ItemId TEXT NOT NULL,
                Client TEXT NOT NULL,
                DeviceName TEXT NOT NULL,
                PlayMethod TEXT NOT NULL,
                TranscodeReasons INTEGER NOT NULL,
                VideoCodec TEXT NULL,
                AudioCodec TEXT NULL,
                SubtitleFormat TEXT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PlaybackEvent_Item_Client
                ON PlaybackEvent(ItemId, Client, CreatedAtUtc);

            CREATE TABLE IF NOT EXISTS Diagnosis (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlaybackEventId INTEGER NOT NULL,
                Code TEXT NOT NULL,
                Confidence TEXT NOT NULL,
                EvidenceJson TEXT NOT NULL,
                Explanation TEXT NOT NULL,
                Recommendation TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (PlaybackEventId) REFERENCES PlaybackEvent(Id)
            );
            CREATE INDEX IF NOT EXISTS IX_Diagnosis_Code ON Diagnosis(Code);
            """;
        command.ExecuteNonQuery();
    }
}
```

- [ ] **Step 4: Implement `PlaybackEventRepository`**

Create `Jellyfin.Plugin.Sentinel/Persistence/PlaybackEventRepository.cs`:

```csharp
using Jellyfin.Plugin.Sentinel.Domain;

namespace Jellyfin.Plugin.Sentinel.Persistence;

public sealed class PlaybackEventRepository
{
    private readonly SentinelDatabase _database;

    public PlaybackEventRepository(SentinelDatabase database)
    {
        _database = database;
    }

    public long Insert(PlaybackEvent playbackEvent)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO PlaybackEvent
                (SessionId, ItemId, Client, DeviceName, PlayMethod, TranscodeReasons, VideoCodec, AudioCodec, SubtitleFormat, CreatedAtUtc)
            VALUES
                ($sessionId, $itemId, $client, $deviceName, $playMethod, $transcodeReasons, $videoCodec, $audioCodec, $subtitleFormat, $createdAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$sessionId", playbackEvent.SessionId);
        command.Parameters.AddWithValue("$itemId", playbackEvent.ItemId);
        command.Parameters.AddWithValue("$client", playbackEvent.Client);
        command.Parameters.AddWithValue("$deviceName", playbackEvent.DeviceName);
        command.Parameters.AddWithValue("$playMethod", playbackEvent.PlayMethod?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("$transcodeReasons", (long)playbackEvent.TranscodeReasons);
        command.Parameters.AddWithValue("$videoCodec", (object?)playbackEvent.VideoCodec ?? System.DBNull.Value);
        command.Parameters.AddWithValue("$audioCodec", (object?)playbackEvent.AudioCodec ?? System.DBNull.Value);
        command.Parameters.AddWithValue("$subtitleFormat", (object?)playbackEvent.SubtitleFormat ?? System.DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", playbackEvent.CreatedAtUtc.ToString("O"));

        return (long)command.ExecuteScalar()!;
    }
}
```

- [ ] **Step 5: Implement `DiagnosisRepository`**

Create `Jellyfin.Plugin.Sentinel/Persistence/DiagnosisRepository.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Sentinel.Domain;

namespace Jellyfin.Plugin.Sentinel.Persistence;

public sealed class DiagnosisRepository
{
    private readonly SentinelDatabase _database;

    public DiagnosisRepository(SentinelDatabase database)
    {
        _database = database;
    }

    public void Insert(long playbackEventId, Diagnosis diagnosis)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Diagnosis
                (PlaybackEventId, Code, Confidence, EvidenceJson, Explanation, Recommendation, CreatedAtUtc)
            VALUES
                ($playbackEventId, $code, $confidence, $evidenceJson, $explanation, $recommendation, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$playbackEventId", playbackEventId);
        command.Parameters.AddWithValue("$code", diagnosis.Code);
        command.Parameters.AddWithValue("$confidence", diagnosis.Confidence.ToString());
        command.Parameters.AddWithValue("$evidenceJson", JsonSerializer.Serialize(diagnosis.Evidence));
        command.Parameters.AddWithValue("$explanation", diagnosis.Explanation);
        command.Parameters.AddWithValue("$recommendation", diagnosis.Recommendation);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O"));

        command.ExecuteNonQuery();
    }

    public IReadOnlyList<(string Code, string Confidence)> GetAllForPlaybackEvent(long playbackEventId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Code, Confidence FROM Diagnosis WHERE PlaybackEventId = $playbackEventId;";
        command.Parameters.AddWithValue("$playbackEventId", playbackEventId);

        var results = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1)));
        }

        return results;
    }
}
```

- [ ] **Step 6: Run the test to verify it passes**

```bash
dotnet test tests/Jellyfin.Plugin.Sentinel.Tests --filter "FullyQualifiedName~SentinelDatabaseTests"
```

Expected: `Passed! - Failed: 0, Passed: 1`.

- [ ] **Step 7: Commit**

```bash
git add Jellyfin.Plugin.Sentinel/Persistence/ tests/Jellyfin.Plugin.Sentinel.Tests/Persistence/
git commit -m "feat: add SQLite persistence for playback events and diagnoses"
```

---

### Task 5: Rule Engine + Confirmed-Confidence Transcode Rules

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Diagnostics/RuleEngine.cs`
- Create: `Jellyfin.Plugin.Sentinel/Diagnostics/CoreTranscodeRules.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Diagnostics/RuleEngineTests.cs`

**Interfaces:**
- Consumes: `PlaybackEvent`, `Diagnosis`, `DiagnosticRule`, `Confidence` (Task 3).
- Produces: `RuleEngine(IReadOnlyList<DiagnosticRule> rules)`, `.Diagnose(PlaybackEvent) : IReadOnlyList<Diagnosis>` — consumed by Task 7.
- Produces: `CoreTranscodeRules.All : IReadOnlyList<DiagnosticRule>` — consumed by Task 7's DI registration.

**Scope note:** this task covers the rules that need zero additional normalization beyond the `TranscodeReason` flags already captured in Task 3 (master plan Section 14, rules #2, #3, #4, #6, #7, #10). The subtitle-burn-in rule (#1) and compound-diagnosis merging (#20) are deferred to the plan that adds richer `MediaStreams` normalization — see the note in `PlaybackEventFactory`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Jellyfin.Plugin.Sentinel.Tests/Diagnostics/RuleEngineTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Jellyfin.Plugin.Sentinel.Tests --filter "FullyQualifiedName~RuleEngineTests"
```

Expected: compile error — `RuleEngine` and `CoreTranscodeRules` don't exist yet.

- [ ] **Step 3: Implement `RuleEngine`**

Create `Jellyfin.Plugin.Sentinel/Diagnostics/RuleEngine.cs`:

```csharp
using System.Collections.Generic;
using Jellyfin.Plugin.Sentinel.Domain;

namespace Jellyfin.Plugin.Sentinel.Diagnostics;

/// <summary>
/// Evaluates every registered <see cref="DiagnosticRule"/> against a <see cref="PlaybackEvent"/>.
/// A single event can produce zero, one, or several diagnoses — one per matching rule.
/// </summary>
public sealed class RuleEngine
{
    private readonly IReadOnlyList<DiagnosticRule> _rules;

    public RuleEngine(IReadOnlyList<DiagnosticRule> rules)
    {
        _rules = rules;
    }

    public IReadOnlyList<Diagnosis> Diagnose(PlaybackEvent playbackEvent)
    {
        var diagnoses = new List<Diagnosis>();

        foreach (var rule in _rules)
        {
            if (!rule.Predicate(playbackEvent))
            {
                continue;
            }

            diagnoses.Add(new Diagnosis
            {
                Code = rule.Code,
                Confidence = rule.Confidence,
                Evidence = new[]
                {
                    $"PlayMethod = {playbackEvent.PlayMethod}",
                    $"TranscodeReasons = {playbackEvent.TranscodeReasons}",
                    $"Matched rule = {rule.Code}"
                },
                Explanation = rule.Explain(playbackEvent),
                Recommendation = rule.Recommendation
            });
        }

        return diagnoses;
    }
}
```

- [ ] **Step 4: Implement `CoreTranscodeRules`**

Create `Jellyfin.Plugin.Sentinel/Diagnostics/CoreTranscodeRules.cs`:

```csharp
using System.Collections.Generic;
using Jellyfin.Plugin.Sentinel.Domain;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.Sentinel.Diagnostics;

/// <summary>
/// The Confirmed-confidence rules that need nothing beyond the <see cref="TranscodeReason"/>
/// flags already captured on a <see cref="PlaybackEvent"/> — master plan Section 14, rules
/// #2-#4, #6, #7, plus #10 (missing-reason handling).
/// </summary>
public static class CoreTranscodeRules
{
    public static IReadOnlyList<DiagnosticRule> All { get; } = new List<DiagnosticRule>
    {
        new(
            Code: "VIDEO_CODEC_UNSUPPORTED",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.VideoCodecNotSupported),
            Confidence: Confidence.Confirmed,
            Explain: e => "Your device can't play this video's codec natively, so Jellyfin had to convert it on the fly.",
            Recommendation: "No action needed unless playback quality or server load is a problem — this is expected for this client/codec combination."),

        new(
            Code: "AUDIO_CODEC_UNSUPPORTED",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.AudioCodecNotSupported),
            Confidence: Confidence.Confirmed,
            Explain: e => "Your device can't decode this audio track natively, so Jellyfin had to transcode the audio.",
            Recommendation: "No action needed — this is expected behavior for this client/audio-codec combination."),

        new(
            Code: "CONTAINER_UNSUPPORTED",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.ContainerNotSupported),
            Confidence: Confidence.Confirmed,
            Explain: e => "Your device doesn't support this file's container format, so Jellyfin had to remux or transcode it.",
            Recommendation: "No action needed — this is expected behavior for this client/container combination."),

        new(
            Code: "SECONDARY_AUDIO_UNSUPPORTED",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.SecondaryAudioNotSupported),
            Confidence: Confidence.Confirmed,
            Explain: e => "This file has a secondary audio track your device can't handle alongside the primary one, forcing a transcode.",
            Recommendation: "If this happens often for this title, consider removing or re-encoding the secondary audio track."),

        new(
            Code: "TOO_MANY_STREAMS",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.StreamCountExceedsLimit),
            Confidence: Confidence.Confirmed,
            Explain: e => "This file has more audio/subtitle streams than your client can handle at once, so Jellyfin had to transcode it.",
            Recommendation: "Consider trimming unused audio/subtitle tracks from this file."),

        new(
            Code: "TRANSCODE_REASON_MISSING",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode && e.TranscodeReasons == default,
            Confidence: Confidence.Unknown,
            Explain: e => "Jellyfin transcoded this playback but didn't record why — this can happen due to a known Jellyfin logging gap, not necessarily a configuration problem.",
            Recommendation: "No specific action — Sentinel doesn't have enough information for a confident diagnosis here."),
    };
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/Jellyfin.Plugin.Sentinel.Tests --filter "FullyQualifiedName~RuleEngineTests"
```

Expected: `Passed! - Failed: 0, Passed: 9`.

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.Sentinel/Diagnostics/RuleEngine.cs Jellyfin.Plugin.Sentinel/Diagnostics/CoreTranscodeRules.cs tests/Jellyfin.Plugin.Sentinel.Tests/Diagnostics/RuleEngineTests.cs
git commit -m "feat: add rule engine and confirmed-confidence transcode rules"
```

---

### Task 6: Known-Core-Issues Knowledge Base

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Diagnostics/KnownCoreIssues.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Diagnostics/KnownCoreIssuesTests.cs`

**Interfaces:**
- Produces: `KnownCoreIssue(string Symptom, string IssueUrl, string Explanation)`, `KnownCoreIssues.Match(string diagnosisCode) : KnownCoreIssue?` — intended to be called wherever a `Diagnosis.Code` is surfaced to an admin (the Dashboard plan wires this up; this task only builds and tests the lookup itself).

- [ ] **Step 1: Write the failing test**

Create `tests/Jellyfin.Plugin.Sentinel.Tests/Diagnostics/KnownCoreIssuesTests.cs`:

```csharp
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Diagnostics;

public class KnownCoreIssuesTests
{
    [Fact]
    public void Match_ReturnsIssue_ForTranscodeReasonMissing()
    {
        var match = KnownCoreIssues.Match("TRANSCODE_REASON_MISSING");

        Assert.NotNull(match);
        Assert.Equal("https://github.com/jellyfin/jellyfin/issues/12193", match!.IssueUrl);
    }

    [Fact]
    public void Match_ReturnsNull_ForUnknownCode()
    {
        var match = KnownCoreIssues.Match("SOME_UNRELATED_CODE");

        Assert.Null(match);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Jellyfin.Plugin.Sentinel.Tests --filter "FullyQualifiedName~KnownCoreIssuesTests"
```

Expected: compile error — `KnownCoreIssues` doesn't exist yet.

- [ ] **Step 3: Implement `KnownCoreIssues`**

Create `Jellyfin.Plugin.Sentinel/Diagnostics/KnownCoreIssues.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Sentinel.Diagnostics;

/// <summary>
/// One verified, currently-open Jellyfin core issue that explains a confusing symptom.
/// This is a curated, hand-maintained table, not a live GitHub API poll — a stale entry
/// pointing at an already-fixed issue is a low-severity, easily corrected failure mode,
/// which is preferable to taking on a live external dependency for this.
/// </summary>
public sealed record KnownCoreIssue(string Symptom, string IssueUrl, string Explanation);

public static class KnownCoreIssues
{
    public static IReadOnlyList<KnownCoreIssue> All { get; } = new List<KnownCoreIssue>
    {
        new(
            Symptom: "TRANSCODE_REASON_MISSING",
            IssueUrl: "https://github.com/jellyfin/jellyfin/issues/12193",
            Explanation: "Jellyfin sometimes drops the transcode-reason field from its own logs. This is a known upstream bug, not a configuration problem on your server."),
    };

    /// <summary>
    /// Finds a known core issue matching a Sentinel diagnosis code, if any exists.
    /// </summary>
    public static KnownCoreIssue? Match(string diagnosisCode) =>
        All.FirstOrDefault(issue => issue.Symptom == diagnosisCode);
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test tests/Jellyfin.Plugin.Sentinel.Tests --filter "FullyQualifiedName~KnownCoreIssuesTests"
```

Expected: `Passed! - Failed: 0, Passed: 2`.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.Sentinel/Diagnostics/KnownCoreIssues.cs tests/Jellyfin.Plugin.Sentinel.Tests/Diagnostics/KnownCoreIssuesTests.cs
git commit -m "feat: add known-core-issues knowledge base"
```

---

### Task 7: Playback Collector Hosted Service — wiring it all together

**Files:**
- Create: `Jellyfin.Plugin.Sentinel/Collector/PlaybackCollectorHostedService.cs`
- Create: `Jellyfin.Plugin.Sentinel/PluginServiceRegistrator.cs`
- Test: `tests/Jellyfin.Plugin.Sentinel.Tests/Collector/PlaybackCollectorHostedServiceTests.cs`

**Interfaces:**
- Consumes: `PlaybackEventFactory` (Task 3), `RuleEngine`/`CoreTranscodeRules` (Task 5), `PlaybackEventRepository`/`DiagnosisRepository` (Task 4), `Plugin.Instance!.DataFolderPath` (Task 1).
- Produces: `PlaybackCollectorHostedService` registered as `IHostedService`; `PluginServiceRegistrator` registered as `IPluginServiceRegistrator` — both discovered automatically by Jellyfin at server startup, nothing downstream consumes them directly.

- [ ] **Step 1: Write the failing integration test**

Create `tests/Jellyfin.Plugin.Sentinel.Tests/Collector/PlaybackCollectorHostedServiceTests.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sentinel.Collector;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Jellyfin.Plugin.Sentinel.Persistence;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Collector;

public class PlaybackCollectorHostedServiceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SentinelDatabase _database;

    public PlaybackCollectorHostedServiceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"sentinel-collector-test-{Guid.NewGuid()}.db");
        _database = new SentinelDatabase(_databasePath);
    }

    [Fact]
    public async Task OnPlaybackStopped_PersistsPlaybackEventAndDiagnosis()
    {
        var sessionManagerMock = new Mock<ISessionManager>();
        var playbackEventRepository = new PlaybackEventRepository(_database);
        var diagnosisRepository = new DiagnosisRepository(_database);
        var ruleEngine = new RuleEngine(CoreTranscodeRules.All);

        var service = new PlaybackCollectorHostedService(
            sessionManagerMock.Object,
            ruleEngine,
            playbackEventRepository,
            diagnosisRepository,
            NullLogger<PlaybackCollectorHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var session = new SessionInfo(sessionManagerMock.Object, NullLogger.Instance)
        {
            Id = "session-42",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
            TranscodingInfo = new TranscodingInfo
            {
                TranscodeReasons = TranscodeReason.VideoCodecNotSupported
            }
        };

        var args = new PlaybackStopEventArgs
        {
            Session = session,
            Item = new Movie { Id = Guid.NewGuid() }
        };

        sessionManagerMock.Raise(m => m.PlaybackStopped += null, sessionManagerMock.Object, args);

        await service.StopAsync(CancellationToken.None);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Diagnosis WHERE Code = 'VIDEO_CODEC_UNSUPPORTED';";
        var count = (long)command.ExecuteScalar()!;

        Assert.Equal(1, count);
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Jellyfin.Plugin.Sentinel.Tests --filter "FullyQualifiedName~PlaybackCollectorHostedServiceTests"
```

Expected: compile error — `PlaybackCollectorHostedService` doesn't exist yet.

- [ ] **Step 3: Implement `PlaybackCollectorHostedService`**

Create `Jellyfin.Plugin.Sentinel/Collector/PlaybackCollectorHostedService.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Jellyfin.Plugin.Sentinel.Persistence;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sentinel.Collector;

/// <summary>
/// Observes finished playback sessions and turns them into persisted diagnoses.
/// Registered as an <see cref="IHostedService"/> — the main <see cref="Plugin"/> class
/// cannot be one itself, per Jellyfin's plugin constraints.
/// </summary>
public sealed class PlaybackCollectorHostedService : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly RuleEngine _ruleEngine;
    private readonly PlaybackEventRepository _playbackEventRepository;
    private readonly DiagnosisRepository _diagnosisRepository;
    private readonly ILogger<PlaybackCollectorHostedService> _logger;

    public PlaybackCollectorHostedService(
        ISessionManager sessionManager,
        RuleEngine ruleEngine,
        PlaybackEventRepository playbackEventRepository,
        DiagnosisRepository diagnosisRepository,
        ILogger<PlaybackCollectorHostedService> logger)
    {
        _sessionManager = sessionManager;
        _ruleEngine = ruleEngine;
        _playbackEventRepository = playbackEventRepository;
        _diagnosisRepository = diagnosisRepository;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs args)
    {
        var playbackEvent = PlaybackEventFactory.FromEventArgs(args);
        if (playbackEvent is null)
        {
            return;
        }

        var playbackEventId = _playbackEventRepository.Insert(playbackEvent);
        var diagnoses = _ruleEngine.Diagnose(playbackEvent);

        foreach (var diagnosis in diagnoses)
        {
            _diagnosisRepository.Insert(playbackEventId, diagnosis);
            _logger.LogInformation(
                "Sentinel diagnosis {Code} ({Confidence}) for session {SessionId}",
                diagnosis.Code,
                diagnosis.Confidence,
                playbackEvent.SessionId);
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test tests/Jellyfin.Plugin.Sentinel.Tests --filter "FullyQualifiedName~PlaybackCollectorHostedServiceTests"
```

Expected: `Passed! - Failed: 0, Passed: 1`.

- [ ] **Step 5: Implement `PluginServiceRegistrator` (no unit test — this is pure DI wiring, verified manually against a real server in Step 6)**

Create `Jellyfin.Plugin.Sentinel/PluginServiceRegistrator.cs`:

```csharp
using System.IO;
using Jellyfin.Plugin.Sentinel.Collector;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Jellyfin.Plugin.Sentinel.Persistence;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Sentinel;

/// <summary>
/// Registers Sentinel's services with Jellyfin's DI container at server startup.
/// Requires a parameterless constructor — cannot be the same class as <see cref="Plugin"/>.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(_ =>
        {
            var databasePath = Path.Combine(Plugin.Instance!.DataFolderPath, "sentinel.db");
            return new SentinelDatabase(databasePath);
        });
        serviceCollection.AddSingleton<PlaybackEventRepository>();
        serviceCollection.AddSingleton<DiagnosisRepository>();
        serviceCollection.AddSingleton(new RuleEngine(CoreTranscodeRules.All));
        serviceCollection.AddHostedService<PlaybackCollectorHostedService>();
    }
}
```

- [ ] **Step 6: Manual verification against a real Jellyfin 12.1 server**

This step cannot be automated — no live-Jellyfin-server test harness exists in this ecosystem (master plan Section 30). Do this once before considering the plan done:

1. `dotnet publish Jellyfin.Plugin.Sentinel/Jellyfin.Plugin.Sentinel.csproj -c Release`
2. Copy the contents of `Jellyfin.Plugin.Sentinel/bin/Release/net10.0/publish/` into a `Sentinel` subfolder under your Jellyfin server's `plugins/` directory.
3. Start (or restart) the Jellyfin 12.1 server and check its own log for a startup error. This is the moment that confirms two currently-unverified assumptions at once: that `ISessionManager` is actually resolvable via constructor injection into a plugin's hosted service, and that `Plugin.Instance` is non-null by the time `PluginServiceRegistrator.RegisterServices` runs. If either assumption is wrong, the server log will show a DI resolution failure or a `NullReferenceException` here — fix by consulting the actual startup log before assuming anything else is broken.
4. Play a video on a client/codec combination you know forces a transcode (e.g., an HEVC file on a client that doesn't support HEVC).
5. Stop playback, then inspect `<plugin data folder>/sentinel.db` (the exact path was just logged by Sentinel — check the server log for it, or find it under the Jellyfin data directory's `plugins/configurations/Sentinel/` path) with any SQLite browser or `sqlite3 sentinel.db "SELECT * FROM Diagnosis;"`. Confirm a `VIDEO_CODEC_UNSUPPORTED` (or matching) row exists with non-empty evidence.

- [ ] **Step 7: Commit**

```bash
git add Jellyfin.Plugin.Sentinel/Collector/ Jellyfin.Plugin.Sentinel/PluginServiceRegistrator.cs tests/Jellyfin.Plugin.Sentinel.Tests/Collector/
git commit -m "feat: wire playback collector hosted service into DI"
git push
```

---

## Self-Review

**Spec coverage:** Section 3 (verified APIs) → Tasks 1, 3, 7. Section 11 (architecture: collector → normalization → rule engine → evidence store) → Tasks 3-7. Section 12 (data model: `PlaybackEvent`, `Diagnosis`) → Tasks 3-4. Section 14 (rule engine, rules #2-#4, #6, #7, #10) → Task 5. Section 14 addendum (Known Core Issues) → Task 6. Section 15 (evidence system) → Task 5's `RuleEngine.Diagnose`. Section 16 (confidence enum, not float) → Task 3. Section 18 (Playback Doctor pipeline) → Tasks 3, 5, 7 together. Section 21 (plugin-update correlation) is explicitly **not** in this plan — it's a separate subsystem sharing this plan's infrastructure, and belongs in its own plan once this foundation is proven working against a real server. Sections 28/29 (Dashboard, Notifications) are explicitly out of scope for this plan per the Scope Check — they are independent subsystems and get their own plan.

**Placeholder scan:** no TBD/TODO markers; the one deliberately-deferred piece (`SubtitleFormat = null`) is documented with a concrete reason and a named follow-up (the subtitle-burn-in rule's plan), not a vague "implement later."

**Type consistency:** `PlaybackEventFactory.FromEventArgs` returns `PlaybackEvent?`, matched by both call sites (Task 3's test, Task 7's hosted service) checking for null. `RuleEngine.Diagnose` returns `IReadOnlyList<Diagnosis>` consistently in Tasks 5 and 7. `DiagnosisRepository.Insert` takes `(long playbackEventId, Diagnosis diagnosis)` consistently across Tasks 4 and 7.
