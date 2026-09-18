# Jellyfin Ecosystem — Full Gap Analysis (v12.x and earlier)

Research date: 2026-09-18. Method: five parallel deep-research passes across GitHub, official docs, features.jellyfin.org, community forums. Every claim below is sourced; anything unverifiable is marked UNKNOWN. This supersedes nothing in `JELLYFIN_SENTINEL_MASTER_PLAN.md` — it's a broader, product-agnostic survey of the whole ecosystem, done on request to find real added-value opportunities beyond Sentinel's specific scope.

---

## Ranked gap list (strongest evidence first)

### 1. Native SSO/OIDC + MFA — the strongest gap found in this entire research effort, stronger than anything in Sentinel's own space

- The dominant SSO plugin, `9p4/jellyfin-plugin-sso` (1,500★), was **archived in May 2026** — maintainer explicitly quit, 38 open issues, API-only config (no admin GUI), self-described "100% alpha," no MFA support ever.
- Successor forks exist but are fragmented and small: `Ezeqielle/jellyfin-plugin-oidc` (30★), `aussierk/jellyfin-plugin-oidc` (33★), plus several more under 35★ each. None has consolidated as the new default.
- Native core SSO is blocked on an admitted architectural rewrite (full ASP.NET Core Identity overhaul) with **no timeline** — confirmed via `jellyfin/jellyfin` org discussion #16470, 166 reactions, 14 participants, maintainers stating the prerequisite work hasn't started.
- LDAP (official, 224★, actively maintained) exists but **bypasses MFA entirely**.
- **Net result: MFA/2FA is unavailable across every authentication path in the entire Jellyfin ecosystem.** No project — official, community, dead, or alive — has solved this.
- Why this matters: for a self-hosted service exposed to the internet (which a meaningful fraction of Jellyfin installs are), this is a real security gap, not a convenience feature.

### 2. In-dashboard bulk/batch metadata editing

- Confirmed via two independent research passes. Jellyfin's own metadata manager allows multi-select but only edits the last-clicked item — confirmed by a matching `jellyfin-web` issue (#105) and two long-open feature requests (features.jellyfin.org #144, #364).
- Every existing tool is either a CLI script (`theronypony/jellyfin-bulk-editor`), a separate hosted web app (`jellytags`), or tag-only (`jellyfin-tag-studio`) — none is an integrated dashboard plugin.
- High confidence: real, evidenced, currently unmet.

### 3. Multi-server management — real demand, universally alpha-quality

- `LLukas22/Jellyswarrm` (904★, by far the most popular attempt at this) is a reverse proxy unifying multiple Jellyfin servers, but self-admits core features are broken (QuickConnect incomplete, WebSocket unreliable, no adaptive bitrate).
- The plugin alternative (`JPKribs/jellyfin-plugin-serversync`, 8★) explicitly warns "use at your own risk."
- Two open official feature requests exist (#47, #407). Native clustering is ruled out by the community itself — SQLite can't support it at the database level.
- Real, evidenced, but a genuinely hard problem (distributed state), not a weekend project.

### 4. Dead MusicBrainz metadata provider + broken core music-metadata merging

- The dedicated MusicBrainz provider plugin (`Jim-Duke/jellyfin-plugin-musicbrainz`) is dead since 2020 (0★, last push 2020-05-22).
- Jellyfin's *built-in* MusicBrainz support has five separate open, unresolved bug reports (#3623, #5778, #7422, #11678, #12337) about metadata not saving/merging correctly across languages.
- Confirmed, currently broken, actively complained about.

### 5. Duplicate detection — fragmented, no dominant/safe solution

- 6+ small, overlapping tools found (report-only, auto-delete-by-IMDb-ID, tag-only, script-based, cross-platform standalone). None dominant, no official plugin, one open official feature request.
- A well-built, **non-destructive-by-default** plugin could plausibly win this category on safety and polish rather than novelty — this is a "compete on quality" opportunity, not a green field.

### 6. Cross-user collaborative-filtering recommendations

- Everything found (`jellyfin-plugin-localrecs`, `Jellyfin-Recommeentations`, jellyfin-helper's recommendation tab) is single-user, local-watch-history-based.
- No project does "users who watched X also watched Y" across the server's whole user base. `localrecs`' explicit privacy-first framing suggests this may be a deliberate tradeoff by that author rather than a pure oversight — moderate, not high, confidence this is a real unattempted gap.
- Also: native "More Like This" has a confirmed, currently open core bug (#16088) showing identical suggestions regardless of source item.

### 7. AI-generated content summaries/taglines

- Not found as a standalone project anywhere across two research passes, despite this pattern existing in other media-server ecosystems. Moderate confidence this is genuinely unattempted rather than just poorly indexed.

### 8. Config-as-code / fleet management for Jellyfin specifically

- Everything found is generic Docker-Compose/Ansible/Terraform scaffolding usable for Jellyfin, not a Jellyfin-aware tool treating libraries/users/plugin state as declarative config. Low urgency, niche audience (multi-instance operators).

### Weaker/lower-confidence gaps (noted for completeness, not recommended as primary targets)

- **Live TV/DVR EPG flexibility** (no Xtream Codes API support) — real limitation, but demand evidence is weak/UNKNOWN.
- **Accessibility** (screen readers don't read subtitles — `jellyfin-web` issue #7456, open) — real and unowned, but belongs in the `jellyfin-web` frontend codebase, not fixable properly by a server plugin. Better filed as an upstream contribution.
- **Multi-target-language subtitle translation** (vs. generation) — plausible gap, low-moderate confidence, may exist under an unsurfaced name.
- **Forced-subtitle-track automation** — low confidence, needs a dedicated follow-up search.

---

## Explicitly NOT gaps — well-solved, do not rebuild

| Area | Solved by | Why it's closed |
|---|---|---|
| Backup/restore | **Native Jellyfin 10.11+ feature** (Dashboard → Backups) | Historically the most-requested missing feature — now built-in. Third-party scripts are largely superseded. |
| Media integrity/corruption scanning | MediaDash (63★), media-integrity-scanner, checkrr | Three active, credible tools with real corruption-detection test coverage. |
| Storage cleanup | jellysweep (252★ — highest star count of any single-purpose tool found in this whole research effort) | Active, popular, growing category (Reclaimerr, Flexerr also exist). |
| Intro/credits skip | intro-skipper (2,775★) | Dominant, actively maintained, community-owned after original went archived. |
| Multiple-version merging | Merge Versions (432★) | Mature, actively updated for 12.x compatibility. |
| Audio loudness normalization | JellyfinReplayGain | Working plugin solution to a real, evidenced request. |
| Multi-profile/PIN parental controls | JellyProfiles / Bonfire-JellyProfiles (117★) | Substantially closes what was a real gap; young but functional. |
| UI/theming/home screen | Skin Manager (660★), Home Screen Sections (504★), Media Bar, SleekFin, Jellyfin Enhanced (1,806★), 5+ more themes | Fully saturated category. |
| Subtitle sync/timing | 5 independent implementations found | Solved repeatedly, not unsolved. |
| Anime metadata | Official AniDB/AniList providers | Well served. |
| Search at scale | Meilisearch plugin (329★) | Mature, active. |
| Generic event → notification delivery | jellyfin-plugin-webhook (official, 246★) | Actively maintained, Handlebars-templated, wide channel support via community templates. |
| Playback root-cause diagnosis | **Nobody** — confirmed gap (see `JELLYFIN_SENTINEL_MASTER_PLAN.md`) | This is Sentinel's own target; still the strongest gap in the *diagnostics* space specifically. |

## Client-side gaps — real, but no server plugin can ever fix them

SyncPlay's lack of cross-client support (Swiftfin/Fladder/Jellyflix all have open feature requests for it), Chromecast audio/HDR quality quirks, native mobile app download-for-offline completeness, Android TV Dolby Vision regressions, and the screen-reader/ARIA subtitle gap all live in client codebases (`jellyfin-web`, `jellyfin-androidtv`, `jellyfin-ios`, third-party clients) — not the server. A Jellyfin *plugin* architecturally cannot fix any of these. Listed here so they aren't mistaken for future plugin scope.

---

## Honest bottom line

If the goal is purely "biggest, best-evidenced, currently-unowned gap in the Jellyfin ecosystem" — **it is not Sentinel's problem space, it's SSO/OIDC/MFA.** A flagship 1,500★ project just died, the fragmented successors are all sub-35★, and Jellyfin core itself says native SSO needs a rewrite it hasn't started. That is a harder, more evidenced signal than anything found in the diagnostics space. It is also a much harder engineering problem (auth, security-critical, needs to not lock admins out of their own server) and a completely different skill/interest area than Sentinel.

Within Sentinel's own adjacent space (health/diagnostics/admin), nothing found today changes the earlier conclusion: Playback Doctor + Incident + Notification remains the best-evidenced, technically-grounded, currently-unowned niche. Bulk metadata editing and duplicate-detection-done-safely are the two next-best-evidenced opportunities if you want a second, independent project rather than expanding Sentinel's scope.
