# Jellyfin Sentinel — Master Architecture & Product Plan

**Status:** Research & planning complete. No production code written yet, per explicit instruction. This document is the input for a separate implementation session.

**Research method:** Four parallel research passes against primary sources (jellyfin/jellyfin source, official docs, GitHub repos/issues, features.jellyfin.org, community forums, npm/PyPI-equivalent registries for notification tooling). Every claim below is tagged with a confidence level. Anything not directly verifiable is marked `UNKNOWN` or `NEEDS VERIFICATION` — nothing below is invented API surface.

Confidence legend used throughout: **VERIFIED (source)** = read in jellyfin source code · **VERIFIED (docs)** = read in official Jellyfin documentation · **VERIFIED (GitHub)** = confirmed repo/issue/README exists as described · **INFERRED** = high-confidence deduction, not directly read · **UNKNOWN / NEEDS VERIFICATION** = could not confirm, must be prototyped/validated before being relied on.

---

## 1. Executive Summary

Jellyfin has no server-side system that explains *why* something went wrong. It has raw data (session records, a `TranscodeReason` enum, log files) but no layer that turns that data into a diagnosis a non-expert can act on. Multiple existing plugins prove there is real, unmet demand adjacent to this problem — **jellyfin-helper** (the most feature-complete "admin dashboard" plugin, 57★, actively maintained on Jellyfin 12.0+) explicitly does *not* attempt root-cause playback diagnosis or plugin-conflict detection in its own documented scope. That is the gap.

Jellyfin Sentinel is a self-hosted Jellyfin plugin that watches playback sessions and server state, applies a deterministic rule engine to Jellyfin's own `TranscodeReason` and session/device data, and produces evidence-backed, confidence-rated incident diagnoses — then, optionally, delivers those diagnoses through a purpose-built multi-channel notification system (Email, Discord, Telegram, ntfy, Gotify, generic webhook). It does not replace existing statistics plugins (Playback Reporting), integrity scanners (MediaDash, media-integrity-scanner), or the official notification plugin (jellyfin-plugin-webhook) — it sits on top of the first, defers to the second for now, and is architected to interoperate with the third rather than duplicate it.

The MVP is intentionally narrow: **Playback Doctor** (transcode/playback root-cause diagnosis with evidence) + **Incident Engine** (dedup, severity, lifecycle) + **Notification Delivery** (a small, real provider set) + a compact dashboard. Library Health, Client Compatibility learning, Plugin Health, Migration Assistant, and AI explanation are explicitly deferred to later phases because either the data needed doesn't exist yet (compatibility matrix needs history) or the problem is already reasonably solved by other tools (integrity scanning).

## 2. Problem Definition

A Jellyfin admin today, when a user reports "playback stutters" or "why did my 4K movie transcode on my TV," has exactly one tool: open **Dashboard → Logs**, grep for strings like `TranscodingJob` or the session ID, and manually reconstruct what happened. Jellyfin's dashboard *does* show live transcode reason/codec/bitrate for an active session (confirmed via community guides), but nothing aggregates this over time, nothing explains *why* in plain language, and nothing tells the admin whether this is a one-off or a recurring pattern for a specific client/media combination. This reconstruction work is manual, expert-only, and thrown away after every incident — the next occurrence starts from zero again.

Separately: Jellyfin 12 (current stable as of Sept 2026) introduced a major, breaking architecture shift (.NET 10, breaking DB changes), and the plugin ecosystem is visibly mid-migration — several popular plugins have no 12.x build yet, and Jellyfin's own official troubleshooting advice for plugin problems is "disable all plugins, re-enable one at a time, restart between each." This is a second, distinct, unautomated diagnostic gap.

## 3. Existing Ecosystem (Technical Foundation — Verified)

| Question | Finding | Confidence |
|---|---|---|
| Current Jellyfin version | **12.0 → 12.1**, released ~Sept 2026. This is a version-scheme jump from the final 10.11.x line — the "10." prefix was dropped, this is *not* a typo or a stale assumption in the original brief. | VERIFIED (docs: jellyfin.org/posts/state-of-the-fin-2026-05-24) |
| Plugin ABI / compatibility | Declared via `targetAbi` (4-part version) in `build.yaml`; JPRM packages this into `meta.json`/repo `manifest.json`. A manifest can host multiple ABI-tagged builds; highest ABI-compatible version wins at install time. | VERIFIED (docs + template) |
| 12.0 breaking changes | Official blog explicitly warns of breaking DB changes and recommends removing non-built-in plugins before migrating. Real, current ecosystem breakage confirmed (e.g. `Exikle/Artemis-Cluster#1144` lists several plugins with no 12.x build yet; `IAmParadox27/jellyfin-plugin-home-sections#282` "Broken on Jellyfin 12"). | VERIFIED (GitHub) |
| Plugin lifecycle | `IServerEntryPoint` is being phased out in favor of standard ASP.NET Core `IHostedService`, registered via `IPluginServiceRegistrator`. Constraint: the main `BasePlugin` class **cannot itself** be an `IHostedService` — a separate hosted service class must be registered. | VERIFIED (source: jellyfin-plugin-template; jellyfin/jellyfin#13191) |
| Playback session events | `ISessionManager` (`MediaBrowser.Controller.Session`) exposes `PlaybackStart` / `PlaybackStopped` events (`PlaybackProgressEventArgs` / `PlaybackStopEventArgs`), implemented in `Emby.Server.Implementations/Session/SessionManager.cs`. | VERIFIED (source) |
| Library item events (add/update/remove) | Exist on `ILibraryManager`, but exact event/interface names were **not** confirmed this pass. | NEEDS VERIFICATION |
| Transcode reason data | `MediaBrowser.Model/Session/TranscodeReason.cs` is a **real enum**, confirmed values include `ContainerNotSupported`, `VideoCodecNotSupported`, `AudioCodecNotSupported`, `SubtitleCodecNotSupported`, `AudioIsExternal`, `SecondaryAudioNotSupported`, `StreamCountExceedsLimit` (list not exhaustively enumerated). Exposed via `TranscodingInfo`/`SessionInfo`. **This is the single most important verified primitive for the whole product** — it is exactly the mechanism a root-cause engine needs. | VERIFIED (source) |
| Transcode reason reliability | Known gap: `jellyfin/jellyfin#12193` reports the field can disappear from logs in some versions. Treat as a strong signal, not an infallible one — the rule engine must degrade confidence gracefully when the field is absent rather than assume it's always populated. | VERIFIED (GitHub issue), flagged as a reliability caveat |
| FFmpeg output access | Each transcode writes to a log file under the server log directory; `TranscodingJobHelper` logs the full ffmpeg command line at `INF` level. **No documented plugin-facing API** to fetch a specific job's stdout/stderr by session ID — correlation would require tailing/parsing the log directory and matching by timestamp/session ID, which is brittle across versions. Whether `TranscodingJobHelper`/`TranscodingJob` are DI-accessible to third-party plugins at all is unconfirmed. | INFERRED / **NEEDS VERIFICATION** — treat as experimental, not MVP-load-bearing |
| Scheduled tasks | `IScheduledTask` / `IConfigurableScheduledTask` (`MediaBrowser.Model.Tasks`) — stable, ABI-identical across 10.x/12.x, auto-registers in the dashboard's Scheduled Tasks UI. Real precedent: Trakt's `SyncLibraryTask`, KodiSyncQueue's `RetentionTask`. | VERIFIED (source + real usage) |
| Plugin configuration UI | `IHasWebPages` + `IPluginConfigurationPage`, served as a static config page (e.g. `configPage.html`) in the standard dashboard slot. A third-party plugin (`IAmParadox27/jellyfin-plugin-pages`) attempts fuller custom-page injection, but its stability/official status under 12.x is unconfirmed. | VERIFIED (source) for config-page slot; **NEEDS VERIFICATION** for richer tab injection |
| Plugin-owned persistence | Official **Playback Reporting** plugin maintains its **own SQLite database**, fully separate from Jellyfin's core DB. This is proven, low-risk prior art. | VERIFIED (GitHub) |
| Logging | `ILogger<T>` DI injection is the standard plugin logging path (consistent with all examples found; not read verbatim in a spec page). No API exists for a plugin to read *other* components'/plugins' log output — only the shared log file on disk, read manually (see FFmpeg row above). | INFERRED (high confidence) |
| Server-side notification bus | `MediaBrowser.Controller.Notifications` (`INotificationService`, `NotificationManager`) **existed but was removed from Jellyfin core as of 10.9.1** (confirmed via `jellyfin/jellyfin` Discussion #11664). The de-facto current mechanism is the **official, separate** `jellyfin/jellyfin-plugin-webhook` plugin. **There is no server-provided notification bus a plugin can hook into for outbound delivery — Sentinel must build its entire notification subsystem itself**, exactly as assumed in the brief. | VERIFIED (GitHub discussion + docs) |
| Client capability data | `SessionInfo` exposes `Client`, `DeviceName`, `ClientCapabilities`; `DeviceProfile` (`MediaBrowser.Model.Dlna`) holds negotiated codec/container support used by `StreamBuilder`. Sufficient raw data to build a historical client-compatibility matrix from session records. OS/platform-level granularity beyond the reported client app name is unconfirmed. | VERIFIED (source) for Client/DeviceName/ClientCapabilities; **NEEDS VERIFICATION** for platform granularity |

**Conclusion:** the technical foundation for a Playback Doctor is solid and verified. The technical foundation for deep FFmpeg-log correlation is not — that capability is deferred to a post-MVP experimental spike, not relied on for MVP confidence claims.

## 4. Plugin Landscape

All entries below verified via GitHub (stars/activity pulled live during research, 2026-09-18).

| Project | URL | Category | What it solves | Jellyfin version | Activity | Stars | Overlap with Sentinel | Gap Sentinel fills |
|---|---|---|---|---|---|---|---|---|
| Playback Reporting (official) | jellyfin/jellyfin-plugin-playbackreporting | Playback | Logs playback activity, graphable, own SQLite DB | Current | Active (2026-09-14) | 135 | Data-source overlap — same raw sessions | Shows *what*, never *why*; no root cause |
| Transcode Killer (official) | jellyfin/jellyfin-plugin-transcodekiller | Playback | Hard-blocks transcoding above a resolution threshold | 10.11 | Active | 29 | Low — blunt prevention, not diagnosis | Explains *why* instead of brute-force blocking |
| Intro Skipper (legacy) | ConfusedPolarBear/intro-skipper | Playback | Intro/credits detection | 10.8.z | **Archived 2023** | 968 | None | — |
| Intro Skipper (active) | intro-skipper/intro-skipper | Playback | Same, community-maintained | 10.11.6+ | Active | 2,775 | None | — |
| jellyfin-plugin-webhook (official) | jellyfin/jellyfin-plugin-webhook | Notifications | Handlebars-templated webhooks/Discord/etc. on native events | Current | Active (v21, 2026-05) | 246–248 | **Direct overlap** on generic event→channel delivery | Sentinel adds the *incident/diagnosis* payload this has no concept of |
| Jellynouncer | MarkusMcNugen/Jellynouncer | Notifications | Discord notifications for new/upgraded content | Current | Active | 4 | Low | Confirms Discord-webhook is an expected pattern |
| Mind the Gaps | IDisposable/jellyfin-plugin-mindthegaps | Library | Missing movies/episodes/collection gaps | 10.11.x | Active | 28 | None | Completeness ≠ integrity/health |
| Jellyfin Reports (official) | jellyfin/jellyfin-plugin-reports | Library | Excel/CSV report export | Current | Active | 48 | Low | Static export, no anomaly detection |
| Meilisearch plugin | arnesacnussem/jellyfin-plugin-meilisearch | Search | Fast/accurate search via Meilisearch, uses Jellyfin 12's external-search-provider API | 12.x + 10.x | Active | 329 | None | Confirms a formal 12.x "external provider" plugin pattern exists — worth studying for Sentinel's own extension points |
| **jellyfin-helper** | JellyPlugins/jellyfin-helper | Infra/Admin | 8-tab dashboard: cleanup, Overseerr/Jellyseerr sync, codec/storage analytics, ML recommendations, Arr comparisons, backup/restore | 12.0+ (.NET 10) | Active (2026-09-17) | 57 | **Closest existing competitor** | README explicitly excludes root-cause playback diagnosis, transcoding troubleshooting, plugin-conflict detection — confirms Sentinel's exact gap is unowned |
| MediaDash (active fork) | crackruckles/MediaDash | Library/Integrity | Duplicates, broken-file detection via multi-point test-playback, oversized-encode detection, NFO/artwork repair | 10.11+/12.0+ | Active (2026-09-14) | 63 | **Direct overlap** with Library Health/Integrity | Sentinel should not rebuild this; differentiate by correlating integrity findings to actual playback incidents |
| Media Integrity Scanner | mcgarrah/jellyfin-plugin-media-integrity-scanner | Library/Integrity | Two-phase ffprobe (fast) + optional ffmpeg decode (deep) incremental scan | Current | Active | 0 | Direct overlap on scan *design* | Validates the resource-budgeted incremental-scan pattern the brief already wants — reuse the idea, don't rebuild the scanner as MVP scope |
| Duplicate finders (3 small repos) | ldroides / kristoffersingleton / MadFra | Library | Narrow duplicate detection/tagging | UNKNOWN | UNKNOWN | UNKNOWN | Low | Not competitive, safe to ignore |
| Jellyfin Enhanced | n00bcodr/Jellyfin-Enhanced | UI | Client-side UX enhancements | 10.11+ | Active | 1,806 | None | Out of scope by design |
| Home Screen Sections | IAmParadox27/jellyfin-plugin-home-sections | UI | Home screen sections | 12.x | Active | 514 | None | Out of scope |
| Media Bar | IAmParadox27/jellyfin-plugin-media-bar | UI | Hero banner | Current | Active | 456 | None | Out of scope |
| SleekFin | varunaditya-plus/SleekFin | UI/Theme | Full reskin | Current | Active | 93 | None | Out of scope |
| AI Search | Franciskid/jellyfin-plugin-ai-search | AI | Semantic search via OpenAI-compatible embeddings | UNKNOWN | UNKNOWN | UNKNOWN | None | Confirms the "optional OpenAI-compatible endpoint, no hard cloud dependency" pattern Sentinel's AI layer should mirror |
| whisper-subs / subgen | GeiserX / McCloudS | AI | Local Whisper subtitle generation | UNKNOWN | UNKNOWN | UNKNOWN | None | Same local-inference precedent |

**Adjacent non-plugin tools** (external, separately hosted — relevant as prior art, not direct competitors): **Jellystat**, **Streamystats**, **Tracearr** — Tautulli-equivalent statistics dashboards, none do root-cause diagnosis. Jellyfin's own built-in `/health` endpoint and optional Prometheus `/metrics` endpoint (VERIFIED, jellyfin.org docs) provide only coarse liveness/metrics, no playback semantics.

## 5. Community Demand

| Claim | Evidence | Confidence |
|---|---|---|
| Real confusion about *why* transcoding happens | forum.jellyfin.org thread "Cannot Understand Why Transcoding Is Not Working" exists; community guides exist purely to explain how to manually read transcode-reason fields from the dashboard | Confirmed via search snippet, not fully loaded |
| Workaround today is manual log grepping | Multiple sources describe grepping Dashboard Logs for `TranscodingJob`/`DirectPlay` strings | Search-snippet confirmed |
| Explicit official feature request for server health/monitoring | features.jellyfin.org/posts/3796 "Monitor Server Real-time Status and Resources" — title confirmed via direct fetch; vote/comment counts did not render (JS-rendered site) | Title VERIFIED, popularity UNKNOWN |
| Admin *incident* notifications (not content notifications) as a distinct, evidenced request | **Not found.** All notification feature requests located (Discord, MQTT, mobile push) are about *new content*, not *server problems*. This does not mean no demand exists — it means it wasn't found as an explicit standalone request. | Absence noted honestly, not asserted as "no demand" |
| Plugin conflict/compatibility pain | Confirmed significant and current: 12.0's .NET 10 migration breaking legacy plugins (real GitHub issues), and Jellyfin's own official troubleshooting is manual bisection (disable all, re-enable one by one, restart between each) | VERIFIED |
| Library integrity checking demand | Confirmed, but **already partially solved** by MediaDash, media-integrity-scanner, and the standalone tool `checkrr` — Jellyfin's built-in scan only checks file existence/parseable metadata, not playability | VERIFIED, but not a green-field gap |

**Implication for Section 39's hypothesis** ("notifications must be core MVP"): community evidence for *content*-notification demand is strong (multiple feature requests, an entire official plugin exists for it), but there is no direct evidence of demand specifically for *incident/health* notifications. The hypothesis is **upheld on product-logic grounds** (a diagnosis nobody sees has no value — see Section 39 discussion below) but **not proven by direct community request volume**. This is flagged honestly rather than papered over.

## 6. Competitive Analysis

| Capability | Existing Project | Sentinel Difference |
|---|---|---|
| Playback statistics/history | Playback Reporting (official) | Sentinel consumes the same category of session data but produces *diagnoses*, not just graphs |
| Prevent transcoding by policy | Transcode Killer (official) | Sentinel explains root cause instead of blanket-blocking |
| Generic event → notification channel delivery | jellyfin-plugin-webhook (official) | Sentinel's notification payload is an *incident* (deduplicated, severity-scored, evidence-backed), not a raw event; Sentinel is architected to coexist with, not replace, jellyfin-plugin-webhook |
| Admin dashboard / codec analytics / cleanup / Arr sync | jellyfin-helper | jellyfin-helper's own scope explicitly excludes root-cause diagnosis and plugin-conflict detection — direct, confirmed gap |
| Corrupt/broken media detection | MediaDash, media-integrity-scanner, checkrr (standalone) | Sentinel does not re-implement corruption scanning in MVP; later, it correlates *existing* integrity signals to actual playback incidents rather than duplicating the scan itself |
| Library completeness (missing episodes/movies) | Mind the Gaps | Out of scope for Sentinel entirely — different problem class |
| Server liveness/metrics | Jellyfin built-in `/health`, `/metrics` | Sentinel can consume these as one signal source but adds semantic diagnosis on top |
| Cross-server statistics (Plex/Jellyfin/Emby) | Tracearr, Jellystat, Streamystats | Different audience (multi-server aggregate stats); no overlap with root-cause diagnosis |

## 7. Identified Gap

Nobody currently converts Jellyfin's own `TranscodeReason` + session + device data into a plain-language, evidence-backed explanation of *why* a specific playback problem happened, tracked as a deduplicated incident over time, per client/media pattern. The closest adjacent plugin (jellyfin-helper) explicitly disclaims this. This is a real, confirmed, currently-unowned gap — not a rebuild of an existing solved problem.

## 8. Product Thesis

Jellyfin manages media well but cannot tell its administrator *why* something just went wrong. Jellyfin Sentinel closes that gap by turning Jellyfin's own diagnostic data — which already exists but is scattered across logs and dashboards — into a deduplicated, evidence-backed incident with a plain-language root cause and a concrete recommendation.

## 9. Product Scope

**In scope (eventually):** playback root-cause diagnosis, incident lifecycle management, multi-channel incident notification, basic server-health signal aggregation (consuming Jellyfin's own `/health`/`/metrics`), plugin-conflict/crash correlation, and — much later — a thin, correlation-only library-integrity layer.

**MVP scope (Section 31/38 below):** Playback Doctor + Incident Engine + Notification Delivery + compact dashboard. Nothing else.

## 10. Non-Goals (Kill List, expanded — see Section 35)

Not a theme, not a general statistics dashboard, not a Sonarr/Radarr replacement, not a Jellyseerr/Overseerr replacement, not a generic Docker/NAS monitor, not a full corruption scanner (redundant with MediaDash/media-integrity-scanner), not an AI chatbot, not a metadata scraper, not a log viewer.

## 11. Core Architecture

```
                         JELLYFIN 12.x
                            │
             ┌──────────────┼──────────────┐
             │              │              │
        ISessionManager  ILibraryManager  /health, /metrics
        PlaybackStart/    (NEEDS VERIF.)   (built-in)
        Stopped events
             │              │              │
             └──────────────┼──────────────┘
                            ↓
                 SENTINEL COLLECTOR (IHostedService)
                 subscribes to verified session events;
                 polls /health as a secondary signal
                            ↓
                 NORMALIZATION LAYER
                 → PlaybackEvent { session, media, client,
                   TranscodeReason[], device profile }
                            ↓
                      RULE ENGINE (deterministic C#)
                            ↓
                   EVIDENCE STORE (own SQLite DB)
                            ↓
                    DIAGNOSIS ENGINE
                    (Confidence: Confirmed/Very likely/
                     Likely/Possible/Unknown)
                            ↓
                    INCIDENT ENGINE
                    (dedup, severity, lifecycle)
                            ↓
                 NOTIFICATION POLICY ENGINE
                            ↓
                  NOTIFICATION SERVICE
                            ↓
          ┌─────────┬─────────┬─────────┬─────────┐
          ↓         ↓         ↓         ↓         ↓
        Email    Discord   Telegram    ntfy   Webhook
        (MailKit)                            (Gotify Phase 2)
                            ↓
                     DELIVERY QUEUE + RETRY
                            ↓
                    DELIVERY HISTORY
                            ↓
                  PROVIDER HEALTH
                            ↓
                Jellyfin Dashboard UI
                (IHasWebPages config-page slot)
```

**Critical architectural decision — persistence: own SQLite DB (Option A).** Justification: this is a *proven* pattern (Playback Reporting plugin already does exactly this), it fully isolates Sentinel from Jellyfin's own database — important given the confirmed 12.0 breaking-DB-change history and ongoing migration risk — and it gives Sentinel full control over indexes, retention, and schema evolution without needing Jellyfin core cooperation. Option B (writing into Jellyfin's DB) was rejected: no documented, stable extension point for third-party tables exists, and doing so would couple Sentinel's survival to Jellyfin's internal schema, which just proved itself willing to break. Option C (hybrid) is unnecessary complexity for the value gained.

**Plugin entrypoint decision:** `IPluginServiceRegistrator` for DI registration + a dedicated `IHostedService` for the background collector, per the verified 12.x pattern — not `IServerEntryPoint`, which is being phased out.

## 12. Data Model

All entities live in Sentinel's own SQLite DB.

**PlaybackEvent** — Purpose: raw normalized record of one playback session's outcome. Fields: `Id`, `SessionId`, `UserId`, `ItemId`, `Client`, `DeviceName`, `StartedAt`, `EndedAt`, `PlayMethod` (DirectPlay/DirectStream/Transcode), `TranscodeReasons` (JSON array of `TranscodeReason` enum values), `VideoCodec`, `AudioCodec`, `SubtitleFormat`, `Bitrate`, `Resolution`. Retention: configurable (7/30/90/365/unlimited), indexed on `(ItemId, Client, StartedAt)` for compatibility-matrix queries. Security: no raw file paths logged.

**Diagnosis** — Purpose: the rule engine's output for one `PlaybackEvent` or correlated group. Fields: `Id`, `PlaybackEventId` (nullable if server-level), `Code` (e.g. `SUBTITLE_BURN_IN`, `HARDWARE_TRANSCODE_UNAVAILABLE`), `Confidence` (enum, not float), `EvidenceJson`, `Recommendation`, `CreatedAt`. Indexed on `Code` for pattern aggregation.

**Incident** — Purpose: deduplicated, lifecycle-tracked problem. Fields: `Id`, `Code`, `Severity` (enum: Info/Notice/Warning/Important/Critical), `Status` (Detected/Investigating/Acknowledged/Resolved/Reopened/Suppressed), `FirstDetectedAt`, `LastDetectedAt`, `OccurrenceCount`, `AffectedUserIds`, `AffectedItemIds`. Indexed on `(Status, Severity)` for dashboard queries.

**NotificationProvider** — Fields: `Id`, `Type` (Email/Discord/Telegram/Ntfy/Gotify/Webhook), `Name`, `ConfigJson` (secrets encrypted at rest if Jellyfin's plugin config storage supports it — **NEEDS VERIFICATION** whether Jellyfin plugin config offers any encryption-at-rest primitive; if not, document this as a known limitation, not a false claim of security), `Enabled`.

**NotificationPolicy** — Fields: `Id`, `SeverityThreshold`, `CategoryFilter`, `ProviderIds` (list), `QuietHoursStart`/`QuietHoursEnd`, `DigestInterval` (null/hourly/daily/weekly).

**NotificationTemplate** — Fields: `Id`, `ProviderType`, `TitleTemplate`, `BodyTemplate` (safe placeholder substitution only — `{{title}}`, `{{severity}}`, etc. — never arbitrary code execution).

**NotificationEvent** — Purpose: one incident-state-change that *could* trigger delivery. Fields: `Id`, `IncidentId`, `TriggerType` (Created/Escalated/Resolved/Reopened), `CreatedAt`.

**NotificationDelivery** — Fields: `Id`, `NotificationEventId`, `ProviderId`, `Status` (Pending/Sent/Failed/Retrying), `Attempts`, `LastError` (sanitized — never raw tokens/secrets), `DeliveredAt`.

**NotificationAttempt** — Fields: `Id`, `NotificationDeliveryId`, `AttemptNumber`, `AttemptedAt`, `HttpStatus`, `ErrorSummary`.

Retention defaults: `PlaybackEvent`/`Diagnosis` 90 days (configurable), `Incident` 1 year (configurable), `NotificationDelivery`/`Attempt` 30 days (configurable) — delivery logs don't need to outlive their usefulness for debugging.

## 13. Event Model

Primary trigger: `ISessionManager.PlaybackStopped` (verified event) — this is when `TranscodingInfo`/`TranscodeReason` data is most complete for a finished session. `PlaybackStart` is used to detect long-running transcode sessions in progress (for live incident detection, not just post-hoc). Library events are a **Phase 3+ concern** pending verification of `ILibraryManager`'s actual event surface — not relied on for MVP.

## 14. Rule Engine

Deterministic, C#, no ML/AI in the loop. Rule shape:

```csharp
public sealed record DiagnosticRule(
    string Code,
    Func<PlaybackEvent, bool> Predicate,
    Confidence Confidence,
    string Explanation,
    string Recommendation);
```

Example (subtitle burn-in), built entirely from **verified** primitives:

```csharp
new DiagnosticRule(
    Code: "SUBTITLE_BURN_IN",
    Predicate: e => e.PlayMethod == PlayMethod.Transcode
                 && e.TranscodeReasons.Contains(TranscodeReason.SubtitleCodecNotSupported)
                 && e.SubtitleFormat == "PGS",
    Confidence: Confidence.VeryLikely,
    Explanation: "Your device can't render PGS (image-based) subtitles natively, so Jellyfin had to re-encode the video to burn them in.",
    Recommendation: "Switch to a text-based subtitle format (SRT/ASS) for this title, or disable subtitles on this client.");
```

Twenty concrete diagnosis examples the MVP rule set should cover (all derived from the verified `TranscodeReason` enum plus session/device data — no invented signals):

1. `SUBTITLE_BURN_IN` — `SubtitleCodecNotSupported` + PGS/image subtitle + Transcode → confidence Very likely.
2. `VIDEO_CODEC_UNSUPPORTED` — `VideoCodecNotSupported` + Transcode → confidence Confirmed (direct enum match).
3. `AUDIO_CODEC_UNSUPPORTED` — `AudioCodecNotSupported` + Transcode → Confirmed.
4. `CONTAINER_UNSUPPORTED` — `ContainerNotSupported` → Confirmed.
5. `EXTERNAL_AUDIO_FORCED_TRANSCODE` — `AudioIsExternal` set → Likely (external audio tracks often force remux/transcode on certain clients).
6. `SECONDARY_AUDIO_UNSUPPORTED` — `SecondaryAudioNotSupported` → Confirmed.
7. `TOO_MANY_STREAMS` — `StreamCountExceedsLimit` → Confirmed.
8. `BITRATE_CAP_EXCEEDED` — Transcode with no codec-mismatch reason present but bitrate above client's negotiated ceiling (from `DeviceProfile`) → Possible (bitrate-only cause is a weaker inference).
9. `REPEATED_CLIENT_TRANSCODE_PATTERN` — same client+codec combo transcoded ≥5 times in 7 days with no direct-play success → Likely, surfaced as an incident not a per-session diagnosis.
10. `TRANSCODE_REASON_MISSING` — Transcode occurred but `TranscodeReasons` is empty (known Jellyfin log gap, issue #12193) → Confidence forced to Unknown, evidence explicitly states the data was unavailable rather than guessing.
11. `NEW_CLIENT_VERSION_REGRESSION` — a client that previously direct-played a codec starts transcoding it after a client app version bump (requires historical `PlaybackEvent` comparison) → Possible, needs ≥2 historical data points.
12. `HDR_TONE_MAPPING_TRANSCODE` — HDR source + non-HDR-capable client profile + Transcode → Likely.
13. `RESOLUTION_DOWNSCALE_ONLY` — Transcode present but all `TranscodeReasons` relate to resolution/bitrate ceiling, not codec → Likely, recommendation differs (network/device limit, not format issue).
14. `DIRECT_PLAY_FAILED_MID_SESSION` — session starts DirectPlay then a later event shows Transcode for the same session (if session updates are observable mid-stream — **NEEDS VERIFICATION** whether `PlaybackProgress` exposes play-method changes) → Possible until verified.
15. `HARDWARE_TRANSCODE_UNAVAILABLE` — inferred only from indirect signals (CPU-bound transcode where hardware acceleration is configured but the session used software encoding) — **experimental, Phase 2**, requires log correlation (unverified capability, Section 3).
16. `AUDIO_CHANNEL_DOWNMIX` — audio channel count in output < source with no other reason → Possible.
17. `CLIENT_UNKNOWN_CAPABILITY_GAP` — client reports a `DeviceProfile` that doesn't declare support for a codec the server otherwise considers common → Possible, low confidence by design (client-reported profiles can be incomplete, not a server fact).
18. `REMOTE_ACCESS_BANDWIDTH_LIMIT` — Transcode plus session flagged as remote (non-LAN) → Possible, distinct recommendation (external bandwidth, not local hardware).
19. `SUBTITLE_FORMAT_UNSUPPORTED_TEXT` — non-PGS subtitle format still triggering `SubtitleCodecNotSupported` → Confirmed, different recommendation than #1 (format conversion vs. burn-in acceptance).
20. `MULTIPLE_REASONS_COMPOUND` — two or more `TranscodeReasons` present simultaneously → surfaced as a compound diagnosis listing all contributing factors rather than picking one arbitrarily, confidence = the lowest individual confidence among contributing rules.

Diagnoses #10, #14, #15, #17, #18 explicitly carry lower confidence or are Phase 2 precisely because their supporting data is unverified or known-unreliable — this is the "no hallucination" rule applied at the product level, not just the research level.

**Known Core Issues knowledge base (added to MVP scope).** Distinct from the rule engine above: a small, hand-curated, versioned table mapping specific confusing symptoms to *already-filed, verified Jellyfin core GitHub issues*, surfaced as a separate diagnosis type ("Known Jellyfin Bug", not "Sentinel Diagnosis") so the two are never confused. This is deliberately cheap to build (a lookup table, not a data pipeline) and directly reinforces the product thesis — sometimes the honest, trust-building answer is "this isn't your configuration, it's a known upstream bug," not a fabricated root cause. Initial entries, all verified during this project's own research: `TranscodeReason` field intermittently missing from logs (jellyfin/jellyfin#12193), "More Like This" showing identical suggestions regardless of source item (jellyfin/jellyfin#16088), parental-rating bypass in the special-features API (jellyfin/jellyfin#17014), missing ARIA live region for subtitles breaking screen-reader support (jellyfin/jellyfin-web#7456). This table must be manually maintained (issues get fixed, closed, reopened) — it is explicitly not a live GitHub API poll for MVP, to avoid an unnecessary external dependency; a stale entry pointing to an already-fixed issue is a low-severity, easily corrected failure mode.

## 15. Evidence System

Every `Diagnosis.EvidenceJson` is a list of concrete, checkable facts pulled directly from the triggering `PlaybackEvent` — e.g. `["PlayMethod = Transcode", "TranscodeReasons contains SubtitleCodecNotSupported", "SubtitleFormat = PGS"]`. The UI renders this as a checklist under the diagnosis, exactly as specified. No diagnosis is ever displayed without its evidence list — this is enforced structurally (the `Diagnosis` entity has no path to exist without a non-empty `EvidenceJson`).

## 16. Diagnosis Engine

Confidence is a 5-value enum (`Confirmed`, `VeryLikely`, `Likely`, `Possible`, `Unknown`), never a numeric percentage — there is no statistically defensible way to produce a percentage from a handful of enum-match rules, and a fake-precise number would violate the no-hallucination principle at the UX level. `Confirmed` is reserved for direct 1:1 enum matches (e.g. `VideoCodecNotSupported` present → codec unsupported, no inference needed). Everything requiring pattern-matching across multiple events drops at least one confidence tier.

## 17. Incident System

An `Incident` is the deduplication unit: repeated `Diagnosis` records with the same `Code` for the same media/client combination within a rolling window collapse into one `Incident` with an `OccurrenceCount`, not N separate alerts. Lifecycle: `Detected → (Investigating) → Acknowledged → Resolved → (Reopened if it recurs after resolution)`. State transitions are what the Notification Policy Engine actually reacts to (Section 28), not raw `PlaybackEvent`s.

## 18. Playback Doctor

Pipeline (matches Section 10 of the brief, now grounded in verified primitives):

```
PlaybackStopped event (verified)
      ↓
Session Analysis (SessionInfo, DeviceProfile — verified)
      ↓
Media Analysis (stream codecs/formats — verified, from MediaSourceInfo)
      ↓
Client Analysis (Client/DeviceName/ClientCapabilities — verified)
      ↓
Transcode Analysis (TranscodeReason[] — verified, with reliability caveat)
      ↓
Root Cause (Rule Engine, Section 14)
      ↓
Recommendation (plain language, Section 31 tone)
```

## 19. Library Health

**Explicitly out of MVP.** Given MediaDash and media-integrity-scanner already do multi-point test-playback corruption detection and two-phase resource-budgeted scanning, Sentinel building a third corruption scanner would violate the golden rule (Section 54 of the brief) — this idea already exists, reasonably well solved, at low but real adoption. Phase 3 scope, if pursued at all, is narrow: correlate *existing* integrity signals (if the user also runs one of those plugins and its findings are readable, or from Sentinel's own lightweight `ffprobe`-only fast pass) to actual playback failure incidents — "this file's damage caused these 3 failed sessions" — rather than reimplementing deep scanning.

## 20. Client Compatibility

Buildable from verified data (`SessionInfo`/`DeviceProfile` history), but requires weeks of accumulated `PlaybackEvent` history before it's statistically meaningful — this is why it's Phase 4 ("Historical Intelligence"), not MVP. Must strictly distinguish `known` (declared in `DeviceProfile`), `observed` (actually happened in a session), and `inferred` (pattern across sessions) — never claim a client "can't" do something from a single failed session.

## 21. Plugin Health

**Decision update (post-MVP-planning, informed by the ecosystem gap analysis):** basic plugin-update correlation is pulled forward into **Phase 2**, not Phase 5. Rationale: Jellyfin 12.0's confirmed, currently-active breaking migration (Section 3/4 — multiple real plugins with no 12.x build yet, official troubleshooting guidance is still manual bisection) makes this pain maximal right now, not in a year. It also requires no new data pipeline — it reuses the same collector/incident/notification infrastructure already built for Playback Doctor, just with a different trigger (plugin install/update timestamp, available via the standard plugin manager) correlated against a spike in exception log entries or `PlaybackEvent` failures. This produces a second, independent "aha" moment ("errors increased right after Plugin X updated to version Y") without waiting on weeks of accumulated history, unlike Section 20's Client Compatibility Matrix.

Phase 5 remains reserved for deeper, harder plugin-dependency heuristics (e.g. detecting two plugins that both patch the same Jellyfin subsystem) — no invented "plugin dependency graph" API exists for this, so it stays speculative and later than the basic update-correlation feature.

## 22. Migration Assistant

Explicitly **not** built in v1 (matches brief's own instruction). Given the confirmed, currently-active 12.0 migration pain (Section 3/4), this could become a high-value Phase 6+ module, but only as an *informational* compatibility-checker (cross-reference installed plugin manifests against known-broken-on-12.x lists), never an automated upgrade executor.

## 23. AI Architecture

Strict pipeline, AI last and optional:

```
Raw Jellyfin data → Deterministic rule engine (Sections 14-16) → Evidence → Diagnosis → [optional LLM] → Human-readable explanation
```

The LLM (if configured) receives the *already-determined* diagnosis code, confidence, and evidence list as structured input and is asked only to phrase it in plain language (Section 31 tone) — it never receives raw logs and is never asked to guess a root cause itself. Supports Ollama and any OpenAI-compatible endpoint (mirrors the pattern already used by `jellyfin-plugin-ai-search`), no hard dependency, fully optional, disabled by default. This is Phase 6, not MVP.

## 24. Privacy

Privacy-first by default, matching the brief: no cloud calls unless the admin explicitly configures an external LLM endpoint or a notification provider (which is inherently an outbound integration the admin chose). Configurable retention per data category (Section 12). `NotificationDelivery.LastError` and all logs must never contain tokens/webhook URLs/passwords — sanitization is enforced at the point of writing the log entry, not as an afterthought filter.

## 25. Security

Secrets (SMTP password, Discord webhook URL, Telegram bot token, ntfy/Gotify tokens) live in `NotificationProvider.ConfigJson`. **NEEDS VERIFICATION**: whether Jellyfin's plugin configuration persistence offers any encryption-at-rest primitive plugins can use — if not, this must be documented as a known limitation (config file readable by anyone with filesystem access to the Jellyfin config directory, which is already a privileged location) rather than a false claim of encryption. Template rendering (Section 25 of the notification extension) uses safe placeholder substitution only — never `eval`/dynamic code execution — to close the obvious template-injection risk.

## 26. Threat Model

Primary vectors considered: (1) a malicious/crafted filename or media title used as a `{{...}}` template placeholder value, delivered to Discord/Telegram — mitigated by treating template output as plain text/escaped markdown, never raw HTML/script; (2) a compromised or misconfigured webhook URL causing Sentinel to leak incident data (media titles, usernames) to an unintended third party — mitigated by requiring explicit admin configuration per provider and a mandatory test-send step before a provider goes live; (3) log/DB growth as a denial-of-service against the host — mitigated by retention limits and scan/notification rate limiting (Sections 12, 16 of the notification extension).

## 27. Performance

CPU/RAM budget: the collector is event-driven (subscribes to `PlaybackStopped`, does not poll session state continuously), so steady-state cost is near-zero between events. The rule engine runs synchronously against a single `PlaybackEvent` (cheap, in-memory enum/string comparisons) — no batch scanning in the hot path. Any future integrity-correlation or client-compatibility aggregation work (Sections 19-20) runs as a low-priority `IScheduledTask`, not inline with playback. For a 100,000+ item library, the *event-driven* design means Sentinel's cost scales with concurrent playback sessions, not library size — this is a deliberate architectural choice to sidestep the brief's stated concern in Section 25, rather than trying to out-optimize a full-library scan approach.

## 28. UI/UX

MVP dashboard, built on the **verified** `IHasWebPages` config-page pattern (not the unverified `jellyfin-plugin-pages` extension):

```
Jellyfin Sentinel
────────────────────────────
Playback        🟠 3 incidents
Notifications   🟢 Healthy

ATTENTION REQUIRED
🔴 Hardware transcoding unavailable — 3 recent sessions [Phase 2]
🟡 Repeated subtitle burn-in on Fire TV — 14 sessions [click to expand]
```

Clicking an incident shows: severity, status, first/last detected, affected sessions, the evidence checklist (Section 15), the plain-language explanation (Section 31), and Acknowledge/Resolve actions. Given the config-page slot is fairly constrained (verified), the MVP UI is intentionally simple — a list + detail view, not a rich multi-tab SPA — until `jellyfin-plugin-pages`-style extension is verified stable enough to build on.

## 29. Notifications

Full architecture per the notification extension prompt, grounded in the fourth research pass:

**Key finding that reshapes this section:** Jellyfin already has an official, actively maintained, popular generic notification plugin (`jellyfin-plugin-webhook`, 246★, Handlebars-templated, supports Discord/Slack/Gotify/Telegram/generic JSON via community templates). **Decision:** Sentinel does **not** reuse or extend that plugin, and does **not** rebuild "generic webhook + templating" as its primary value proposition. Sentinel ships its own small, purpose-built provider set whose entire reason to exist is delivering *incident objects* (severity, evidence, confidence, recommendation) — a payload shape `jellyfin-plugin-webhook`'s native-event model has no concept of. This was an explicit fork-in-the-road (documented, not silently resolved): the alternative — firing a custom notification type for `jellyfin-plugin-webhook` templates to render — was rejected because it's unverified whether Jellyfin's (removed) notification pipeline still allows third-party plugins to register consumable notification types for *other* plugins; building Sentinel's own thin, self-contained providers is lower-risk and keeps Sentinel usable standalone.

**MVP providers** (decision, with justification): **Email (MailKit)**, **Discord**, **Telegram**, **generic Webhook**. MailKit is VERIFIED as the correct, current, MIT-licensed replacement for the obsolete `System.Net.Mail.SmtpClient`. Discord/Telegram are the two highest-adoption channels in this exact audience (confirmed via Uptime Kuma's 90+ provider list, which explicitly includes both as top-tier options for the self-hoster demographic Sentinel shares with Uptime Kuma users). Generic Webhook is a hard requirement regardless of channel choices — it's the escape hatch for anything not natively supported.

**Build order decision:** implement the **generic Webhook provider first**, ahead of Discord/Telegram, even though it's the least "flashy" channel. Justification: it lets early adopters immediately bridge Sentinel incidents into their *existing* `jellyfin-plugin-webhook` (official, 246★, Handlebars-templated) or `apprise-api` setup on day one, multiplying real-world reach before Sentinel has built a single channel-specific integration itself. It's also architecturally the simplest provider, which validates the full `INotificationProvider` → policy → retry → delivery-log pipeline (Section 12) end to end before adding channel-specific complexity (embed formatting, bot APIs).

**Phase 2 providers:** **ntfy** (VERIFIED: simple HTTP POST to a topic, Bearer/Basic auth, self-hostable, Apache-licensed — very low integration cost, high value for the homelab audience) and **Gotify** (VERIFIED: simple `POST /message` with an app token header, self-hosted only).

**Apprise — explicit decision: do not embed.** Apprise is a **Python** library; embedding a Python runtime inside a .NET Jellyfin plugin process is impractical and matches no existing Jellyfin plugin pattern found in research. If Apprise support is ever wanted, the correct integration is calling a separately-run **`apprise-api`** Docker container (VERIFIED to exist, `caronc/apprise-api`) via its `POST /notify` HTTP endpoint — architecturally identical to the generic Webhook provider with an Apprise-shaped payload. This is a documented **Phase 4** option, never an in-process dependency. This directly validates the brief's own instruction in Section 8 to document rather than force it.

**Rate limits (for retry policy design):** Discord ≈30 req/60s per webhook, 429 responses carry `Retry-After` (must be honored) — exact numeric ceiling should be re-verified against Discord's own docs before hardcoding (community-sourced number, not Discord's own page, per the research pass). Telegram: 429 responses carry an official `retry_after` field — this one is authoritative and should be the actual retry-driving signal rather than a hardcoded guess for Telegram specifically.

**Notification data model:** see Section 12 (`NotificationProvider/Policy/Template/Event/Delivery/Attempt`).

**Policies, quiet hours, digests, escalation, acknowledgement, delivery log, retry, offline queue, templates, action links, provider health, self-test** — all implemented exactly per the extension prompt's specification (Sections 12-28 of the extension), scoped as: **policies + severity filtering + rate limiting + dedup + basic delivery history + retry + templates + test-send** in MVP; **quiet hours + digests + ntfy/Gotify + acknowledgement** in Phase 2; **escalation + provider health dashboard + richer templates** in Phase 3; **Home Assistant/Matrix/Slack/Teams/Pushover/Apprise** in Phase 4.

**Core separation-of-concerns rule (Section 31 of the extension), enforced structurally:** `PlaybackDetector` → `IncidentService` → `NotificationPolicyEngine` → `NotificationService` → `INotificationProvider` implementations. A detector class never has a compile-time reference to `DiscordProvider` or any concrete channel type.

## 30. Testing Strategy

**Unit tests:** every `DiagnosticRule` in Section 14 gets a table-driven test with a synthetic `PlaybackEvent` fixture and an asserted `(Code, Confidence)` pair — this doubles as the brief's requested "regression test per known diagnosis" (Section 33). Confidence/severity/dedup logic tested in isolation from any real Jellyfin dependency. **Integration tests:** a fake/in-memory `ISessionManager` implementation firing synthetic `PlaybackStopped` events to verify the full collector → rule engine → incident → (mocked) notification pipeline end to end, without a real Jellyfin server. **No live-Jellyfin-server test harness exists in this ecosystem currently found** — this is a real gap the project should accept rather than pretend to solve; manual verification against a real Jellyfin instance is required before each release, matching the org-wide `GitHub/CLAUDE.md` testing guidance ("kein Test-Suite etabliert, lieber manuell verifizieren").

## 31. MVP

**Included:** Playback Incident Detection (event-driven, `PlaybackStopped`-triggered), Root Cause Analysis (rules #1-#4, #6, #7, #10, #19, #20 from Section 14 — the ones built on `Confirmed`/`VeryLikely` confidence primitives only; the `Possible`/experimental rules ship as Phase 2 once the core loop is validated against a real server), the **Known Core Issues knowledge base** (Section 14 addendum), Evidence display, Incident dedup/lifecycle, a compact dashboard (Section 28), and Notification delivery via generic Webhook (built first) → Discord → Telegram → Email with severity filtering, rate limiting, retry, and mandatory test-send.

**Included in the immediately-following Phase 2 build (not deferred to later phases):** basic **Plugin-Update Correlation** (Section 21) — pulled forward from its original Phase 5 slot because it needs no new data pipeline and the Jellyfin 12.0 migration pain is maximal right now, not in a year.

**Explicitly excluded from this build entirely (later phases, if ever):** Library Health/Integrity (Section 19 — MediaDash/media-integrity-scanner already solve this reasonably well), Client Compatibility Matrix (Section 20 — needs weeks of history that doesn't exist on day one), deeper Plugin-dependency-graph heuristics (Section 21, the harder remainder), Migration Assistant (Section 22), AI explanation layer (Section 23), quiet hours/digests/escalation/ntfy/Gotify (Section 29). Also explicitly out of Sentinel's product identity regardless of phase: duplicate detection and bulk-metadata editing — both are real, evidenced ecosystem gaps (see the companion `JELLYFIN_ECOSYSTEM_GAP_ANALYSIS.md`), but they are data-correction problems, not diagnostic problems, and belong in separate plugins, not Sentinel.

Plain-language tone requirement (Section 31 of the original brief) is enforced in the `Explanation` field of every `DiagnosticRule` — never raw enum names in user-facing text, always the "your TV can't do X, so Jellyfin had to do Y" framing, with a collapsible "technical details" section underneath for power users.

## 32. Roadmap

**Phase 0 — Research/Architecture (this document).** Done.

**Phase 1 — MVP diagnostic engine.** Collector, rule engine (Confirmed/VeryLikely rules only), evidence store, own SQLite DB, basic incident dedup. Risk: `TranscodeReason` reliability gap (issue #12193) — mitigate by degrading confidence gracefully, never failing silently. Effort: medium. Exit criteria: correctly diagnoses subtitle-burn-in and codec-unsupported cases against a real Jellyfin 12.x server with manually-verified test playback sessions.

**Phase 2 — Playback Doctor completion + Notification Delivery + Plugin-Update Correlation.** Remaining `Possible`-confidence rules, full notification subsystem (Webhook/Discord/Telegram/Email MVP set + ntfy/Gotify), policies, rate limiting, retry, dedup-aware alerting, **plus basic Plugin-Update Correlation (Section 21)** pulled forward from its original Phase 5 slot. Risk: Discord/Telegram rate-limit numbers need re-verification against current official docs before hardcoding retry backoff. Effort: medium-high. Exit criteria: an admin can configure at least 2 notification channels, receive a real deduplicated incident notification, acknowledge/resolve it from the dashboard, and see an incident automatically raised when a plugin update is immediately followed by an error spike.

**Phase 3 — thin Library Health correlation (not scanning) + quiet hours/digests/escalation.** Effort: medium. Exit criteria: if the admin also runs an integrity scanner, Sentinel can cross-reference its findings against playback incidents; otherwise this phase can ship without a scanner at all.

**Phase 4 — Historical Intelligence (Client Compatibility Matrix, temporal trend analysis, event correlation) + Apprise-via-apprise-api + Home Assistant/Matrix/Slack.** Requires 30-90 days of accumulated real `PlaybackEvent` data to be meaningful — cannot ship earlier regardless of engineering effort. Effort: high. Exit criteria: compatibility matrix distinguishes known/observed/inferred correctly and doesn't overclaim from single-session failures.

**Phase 5 — Advanced Plugin Health.** Basic update-correlation already shipped in Phase 2 (Section 21); this phase covers the harder remainder — e.g. detecting two plugins patching the same Jellyfin subsystem. Effort: medium-high. Risk: no formal plugin-dependency-graph API exists; this stays heuristic, not authoritative.

**Phase 6 — Optional AI explanation layer.** Effort: low (thin layer on top of an already-complete diagnosis). Exit criteria: LLM output never contradicts or replaces the deterministic diagnosis, only rephrases it.

**Phase 7 — Advanced automation (Level 1/2 remediation actions: rescan library, restart plugin, clear cache).** Effort: medium, mostly UX/confirmation-flow work since the underlying Jellyfin scheduled-task/plugin-manager APIs already exist. Exit criteria: no destructive action ever executes without explicit per-action confirmation.

## 33. Risks

1. **`TranscodeReason` field reliability** (confirmed gap, issue #12193) — the entire Playback Doctor value proposition depends on this field; must be treated as "usually present," not guaranteed, with graceful confidence degradation, not a hard dependency.
2. **Jellyfin 12.x ecosystem churn** — confirmed, active, ongoing. Sentinel targets 12.x from day one specifically because building against the outgoing 10.x line now would be building on a deprecating foundation.
3. **FFmpeg log correlation is unverified** — any feature depending on it (hardware-transcode-unavailable diagnosis) must stay experimental/Phase 2+ until a prototype confirms feasibility; do not commit a roadmap date to it.
4. **No encryption-at-rest confirmed for plugin config secrets** — must be documented honestly to users, not silently assumed safe.
5. **No live-Jellyfin CI test harness exists in the ecosystem** — release quality depends on manual verification discipline, a process risk, not just a technical one.

## 34. Open Questions

- Exact `ILibraryManager` event surface (Section 3) — needed before any Library-related feature beyond Phase 0.
- Whether `TranscodingJobHelper`/`TranscodingJob` are DI-accessible to third-party plugins (Section 3) — determines whether FFmpeg log correlation is even architecturally possible, separate from whether it's reliable.
- Whether Jellyfin's plugin config persistence has any encryption-at-rest option (Section 25).
- Whether `DeviceProfile` exposes OS/platform granularity beyond app name (Section 20 — affects how precise the compatibility matrix can be).
- features.jellyfin.org vote/comment counts for the server-health feature request (Section 5) — the site is JS-rendered and wasn't fully accessible; a manual browser check would sharpen the demand signal.
- Exact current Discord embed field-count/length limits and precise rate-limit numbers (Section 29) — community-sourced, should be re-verified against Discord's own developer docs before the retry policy ships.

## 35. Kill List

- **Another theme/reskin** — SleekFin and others already serve this well; zero product value for Sentinel to compete here.
- **Generic statistics dashboard** — Playback Reporting, jellyfin-helper, Jellystat, Streamystats, Tracearr all already do this; adding another would be pure duplication with no diagnostic value-add.
- **Full corruption/integrity scanner** — MediaDash and media-integrity-scanner already solve this reasonably well at the "test-playback" depth the brief itself asks for; rebuilding it violates the golden rule (Section 54 of the brief).
- **Generic webhook/notification templating engine as the primary product** — jellyfin-plugin-webhook (official, 246★) already owns this; Sentinel's notification layer exists only to carry incident semantics that plugin has no concept of, not to compete on channel count.
- **Duplicate/media-search request system** — out of Sentinel's problem domain entirely (that's Jellyseerr's job).
- **Full NAS/Docker monitoring** — out of scope; Sentinel consumes Jellyfin's own `/health`/`/metrics`, it doesn't reinvent Uptime Kuma.
- **Automated Jellyfin version upgrades** — explicitly excluded per the brief; Migration Assistant (if ever built) stays informational-only.

## 36. First 20 GitHub Issues

1. `chore: scaffold plugin project from jellyfin-plugin-template targeting 12.x ABI`
2. `feat: implement IPluginServiceRegistrator + collector IHostedService skeleton`
3. `feat: own SQLite DB schema + migrations for PlaybackEvent/Diagnosis/Incident`
4. `feat: subscribe to ISessionManager.PlaybackStopped, normalize into PlaybackEvent`
5. `spike: verify TranscodeReason field population reliability across recent Jellyfin 12.x point releases`
6. `feat: DiagnosticRule engine skeleton + rule registration`
7. `feat: implement rules #1-#4 (subtitle burn-in, video/audio codec unsupported, container unsupported)`
8. `feat: implement rules #6-#7, #10, #19-#20 (secondary audio, stream count, missing-reason handling, compound diagnosis)`
9. `test: table-driven unit tests for every shipped DiagnosticRule with synthetic PlaybackEvent fixtures`
10. `feat: Incident dedup engine (rolling-window collapse of repeated Diagnosis records)`
11. `feat: IHasWebPages config-page dashboard — incident list + detail view`
12. `feat: NotificationProvider abstraction (INotificationProvider) + provider registry`
13. `feat: Email provider via MailKit (STARTTLS/TLS/auth)`
14. `feat: Discord webhook provider with embed formatting + 429/Retry-After handling`
15. `feat: Telegram bot provider with parse_mode + retry_after handling`
16. `feat: generic Webhook provider with configurable auth headers`
17. `feat: NotificationPolicy engine (severity filter, category filter) + rate limiting`
18. `feat: retry queue with exponential backoff for transient delivery failures`
19. `feat: test-notification action per provider with human-readable failure explanations`
20. `docs: CONTRIBUTING.md, ARCHITECTURE.md, SECURITY.md + issue/feature-request templates`

## 37. First 10 Implementation Tasks

1. Scaffold the plugin repo from `jellyfin/jellyfin-plugin-template`, set `targetAbi` for current 12.x, confirm it loads (empty plugin, no logic) on a real test Jellyfin 12.x instance.
2. Add the SQLite persistence layer (schema from Section 12) and verify it initializes correctly on plugin load without touching Jellyfin's own DB.
3. Wire the `IHostedService` collector to `ISessionManager.PlaybackStopped` and log every received event's raw `TranscodeReason` payload — this is the manual-verification step that either confirms or refutes assumptions from Section 3 against a live server.
4. Build the `PlaybackEvent` normalization layer from the raw session data observed in step 3.
5. Implement the rule engine skeleton and the first `Confirmed`-confidence rules (#2-#4, #6-#7 from Section 14) — these require no inference, just direct enum matches, and are the fastest path to a working end-to-end diagnosis.
6. Implement Incident dedup on top of the rule engine's output.
7. Build the minimal `IHasWebPages` dashboard page showing the incident list (read-only first, no actions yet).
8. Implement the `INotificationProvider` abstraction and the generic Webhook provider first (simplest, no external SDK, validates the whole delivery pipeline shape).
9. Add Discord and Telegram providers on top of the now-proven pipeline.
10. Add Acknowledge/Resolve actions to the dashboard and wire incident state transitions to the (already-built) notification policy engine.

## 38. Final Recommendation

Build Jellyfin Sentinel, but strictly as the MVP defined in Section 31 — Playback Doctor plus a small, purpose-built notification layer, nothing else. The confirmed gap (Section 7) is real and unowned; the confirmed technical primitives (`TranscodeReason`, `SessionInfo`, verified event hooks) are sufficient to build a genuinely differentiated diagnosis engine without depending on any unverified API surface. Resist the urge to build Library Health, Client Compatibility, or Plugin Health early — those either duplicate existing, reasonably-solved tools (integrity scanning) or need data history that literally cannot exist before the MVP has been running for weeks. The single "aha" moment to design and test toward before writing any more code than the MVP requires: an admin sees "🔴 3 playback incidents," clicks one, and reads a plain-language, evidence-backed explanation of a subtitle-burn-in or codec-mismatch transcode that Jellyfin's own dashboard never surfaced in that form. If that moment doesn't land in manual testing against a real server, no amount of additional module scope will fix the product.
