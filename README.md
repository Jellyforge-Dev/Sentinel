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
