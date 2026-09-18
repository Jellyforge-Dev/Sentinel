# Setting up Jellyfin Sentinel

This document is for people who want to install and run Sentinel today. Read the "Current
limitations" section before you do — this is pre-release software that has not yet been verified
against a live Jellyfin server.

## Requirements

- Jellyfin **12.1** or later. Sentinel targets the `net10.0`/12.1 plugin ABI; it will not load on
  10.x servers.
- No other Jellyfin plugins are required. Sentinel does not depend on or conflict with any
  specific plugin.

## Installing via the plugin repository

Once a release has been published (see "Current limitations" below if none exists yet):

1. In Jellyfin, go to **Dashboard → Plugins → Repositories → Add Repository**.
2. Set **Repository Name** to `Jellyfin Sentinel` (or anything you like — this is just a label).
3. Set **Repository URL** to:
   ```
   https://raw.githubusercontent.com/Jellyforge-Dev/Sentinel/main/manifest.json
   ```
4. Go to **Dashboard → Plugins → Catalog**, find **Sentinel**, and install it.
5. Restart Jellyfin.

If the plugin doesn't appear in the catalog after adding the repository, reload the plugin page
without cache (`Ctrl+F5` on Windows/Linux, `Shift+Cmd+R` on macOS) — Jellyfin's dashboard
sometimes caches the catalog list.

## Installing manually (for testing before a release exists)

If you're testing an unreleased build:

1. Clone this repository and check out the branch/commit you want to test.
2. Build and publish:
   ```bash
   dotnet publish Jellyfin.Plugin.Sentinel/Jellyfin.Plugin.Sentinel.csproj -c Release
   ```
3. Create a folder named `Sentinel` inside your Jellyfin server's `plugins/` directory (this path
   is platform-specific — see [Jellyfin's own documentation](https://jellyfin.org/docs/general/administration/configuration)
   for where that is on your system).
4. Copy **every file** from `Jellyfin.Plugin.Sentinel/bin/Release/net10.0/publish/` — including
   the `runtimes/` subfolder — into that `Sentinel` folder. Copying only the `.dll` will make the
   plugin fail to load, since it depends on the SQLite packages listed in
   [`Jellyfin.Plugin.Sentinel/build.yaml`](./Jellyfin.Plugin.Sentinel/build.yaml).
5. Restart Jellyfin and check its server log for a line from Sentinel confirming it started and
   showing the database path it resolved (see below).

## What Sentinel actually does once installed

Sentinel currently has **no user interface**. There is no dashboard page, no configuration
screen, and no notifications. What it does, silently, in the background:

1. Every time a playback session ends on your server, Sentinel checks whether Jellyfin
   transcoded it and, if so, why (using Jellyfin's own `TranscodeReason` data).
2. It writes a record of that session, and any diagnosis it could make, into its own SQLite
   database — separate from Jellyfin's database.
3. It logs a line to Jellyfin's own server log for every diagnosis it makes, and one line at
   startup showing where its database file lives.

To see what Sentinel has found, you currently have to open its database file directly with any
SQLite browser (e.g. [DB Browser for SQLite](https://sqlitebrowser.org/)) and look at the
`PlaybackEvent` and `Diagnosis` tables. The exact file path is printed in Jellyfin's server log
right after Sentinel starts — search the log for "Sentinel" after a restart.

A proper admin dashboard is planned but not yet built — see the roadmap in
[`JELLYFIN_SENTINEL_MASTER_PLAN.md`](./JELLYFIN_SENTINEL_MASTER_PLAN.md).

## Current limitations — read this before installing on a server you care about

- **Not yet tested against a live Jellyfin server.** Every part of this plugin has been unit- and
  integration-tested against simulated Jellyfin objects, but nobody has installed it on a real,
  running Jellyfin 12.1 server yet. It should install and run without issue, but "should" is not
  "has."
- **No dashboard, no notifications.** You have to read the SQLite database directly (see above).
- **Only a narrow set of diagnoses so far.** Sentinel currently recognizes unsupported
  video/audio codecs, unsupported containers, unsupported secondary audio tracks, too many
  streams, and "transcoded but Jellyfin didn't say why." It does not yet explain every possible
  reason a transcode happens (e.g. bitrate-driven transcodes aren't covered yet) — an
  unrecognized transcode is simply not diagnosed, not misdiagnosed.
- **The plugin repository above may have no installable version yet.** If `manifest.json` lists
  no versions, that means no release has been published — check the
  [repository's Releases page](https://github.com/Jellyforge-Dev/Sentinel/releases) or use the
  manual installation method above instead.

## Uninstalling

Remove the plugin from **Dashboard → Plugins → My Plugins**, restart Jellyfin, and (optionally)
delete Sentinel's data folder — its path was logged at startup, as described above. Uninstalling
does not touch Jellyfin's own database.

## For maintainers: cutting a release

There is no CI automation for this yet — releases are cut manually:

1. Bump `version` in [`Jellyfin.Plugin.Sentinel/build.yaml`](./Jellyfin.Plugin.Sentinel/build.yaml)
   and in [`Directory.Build.props`](./Directory.Build.props) (`Version`/`AssemblyVersion`/`FileVersion`).
2. `dotnet publish Jellyfin.Plugin.Sentinel/Jellyfin.Plugin.Sentinel.csproj -c Release`.
3. Zip **exactly** the files listed under `artifacts:` in `build.yaml` (that list is a verified,
   strict copy-filter matching what JPRM would produce — don't just zip the whole publish
   folder without checking it still matches).
4. Compute the zip's MD5 checksum (`md5sum <file>.zip` or equivalent).
5. Create a GitHub Release with that tag/version, uploading the zip as a release asset.
6. Add a new entry to the `versions` array in [`manifest.json`](./manifest.json): `version`
   (match `build.yaml`), `changelog`, `targetAbi` (match `build.yaml`), `sourceUrl` (the release
   asset's download URL), `checksum` (the MD5 from step 4), `timestamp` (UTC, ISO 8601).
7. Commit and push `manifest.json` to `main` — Jellyfin servers with the repository already added
   will pick up the new version automatically.
