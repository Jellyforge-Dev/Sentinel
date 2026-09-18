# Setting up Jellyfin Sentinel

This document is for people who want to install and run Sentinel today. Read the "Current
limitations" section before you do — this is pre-release software. `v0.1.1.0` is confirmed
loading and running on a real Jellyfin 12.1 server, but the actual playback-diagnosis pipeline
(a real transcode producing a database row) is not yet confirmed live.

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
5. **In the copy inside the `Sentinel` plugin folder** (not in your build output), delete every
   subfolder under `runtimes/` except the six Sentinel actually supports — `win-x64`, `win-arm64`,
   `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`. `dotnet publish` produces many more RID
   folders than that (e.g. `win-x86`, `linux-arm`, `linux-musl-x64`), and Jellyfin's plugin loader
   globs every `*.dll` file **anywhere** under the plugin folder — including ones you'll never run
   on — and tries to load each one as a managed .NET assembly. Any leftover native `.dll` there
   crashes the whole plugin with `BadImageFormatException`. This is real, not theoretical: it's
   exactly how `v0.1.0-alpha` broke on a live server.
6. **Then rename the two Windows native SQLite files** — `runtimes/win-x64/native/e_sqlite3.dll`
   and `runtimes/win-arm64/native/e_sqlite3.dll` — to `e_sqlite3.dll.win`. This is the other half
   of the same problem: even the *correct*-platform Windows file matches Jellyfin's `*.dll` glob
   unless renamed. See the comment above `artifacts:` in `build.yaml` for the full explanation.
   Linux (`.so`) and macOS (`.dylib`) files don't need renaming — their extensions never match
   that glob.
7. Restart Jellyfin and check its server log for a line from Sentinel confirming it started and
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

## Verifying the diagnosis pipeline actually works

Confirming the plugin loads (see "Current limitations") is not the same as confirming it
correctly diagnoses a playback problem. To check that end to end:

1. Play a piece of media on a client/codec combination you know forces a transcode (e.g. an HEVC
   file on a client that only supports H.264, or a client with a low bitrate cap).
2. Stop playback.
3. Open `sentinel.db` (path from the server log, see above) with a SQLite browser and check the
   `PlaybackEvent` table for a new row, then the `Diagnosis` table for a matching row with a
   `Code` like `VIDEO_CODEC_UNSUPPORTED` and a non-empty `EvidenceJson`.

If a `PlaybackEvent` row appears but no `Diagnosis` row does, that's not necessarily a bug — it
means none of Sentinel's current rules matched (see "Only a narrow set of diagnoses so far"
above). If neither row appears, something in the collection pipeline itself needs investigating —
please open an issue with the relevant lines from Jellyfin's server log.

## Current limitations — read this before installing on a server you care about

- **`v0.1.0-alpha` was tested against a live Jellyfin 12.1 server and failed** — it was disabled
  with status "Malfunctioned" because of the native-library loading problem explained above.
  **`v0.1.1.0` fixes it and is confirmed loading and running on a real Jellyfin 12.1 server.**
- **A second live-only bug was found and fixed: forcing a real transcode produced no diagnosis at
  all, for a reason that had nothing to do with rule coverage.** Live testing showed a
  bitrate-driven transcode leaving `PlaybackEvent.PlayMethod` empty and `TranscodeReasons` at `0`
  in `sentinel.db`, even though Jellyfin's own dashboard clearly showed an active transcode with a
  specific reason. Root cause, verified against real Jellyfin 12.1 source
  (`Emby.Server.Implementations.Session.SessionManager`): Jellyfin's own `OnPlaybackStopped`
  always calls `RemoveNowPlayingItem(session)` — which resets `session.PlayState` to a brand-new,
  empty object and clears `session.TranscodingInfo` to `null` — **before** firing the
  `PlaybackStopped` event every Sentinel version up to this point relied on exclusively. This
  affected every recorded session, not just bitrate-driven ones; the earlier explanation in this
  document ("remux doesn't trigger a rule, so no diagnosis is correct") was an unverified guess
  that happened to sound plausible and has been retracted. The fix (present from the version this
  bullet ships in) also listens to `PlaybackProgress`, caches the last known play method and
  transcode reason per session while it's still live, and uses that cached snapshot once the
  session stops. Confirmed by a test that reproduces the exact real-world sequence and fails
  without the fix; **not yet re-confirmed against a real server for this specific fix.**
- **A `TRANSCODE_REASON_MISSING` diagnosis after this fix is not, by itself, proof the fix is
  working.** This fix makes that rule reachable for the first time — before it, `PlayMethod` was
  always empty, so the rule (which requires `PlayMethod == Transcode`) could never match. Some
  clients omit the play method from their own progress reports, and Jellyfin's field for it
  defaults to the same value as `Transcode` when omitted, so a client with an incomplete progress
  report can trigger this diagnosis without ever actually transcoding. A `VIDEO_CODEC_UNSUPPORTED`
  (or similar) diagnosis with a real, non-empty `TranscodeReasons` value is the actual signal that
  the fix is working end to end.
- **No notifications yet, and only a minimal read-only dashboard.** The full incident dashboard
  (severity, acknowledge/resolve, notifications) from the original plan is not yet built — see
  the plugin's own configuration page for what exists today.
- **A wider, but still incomplete, set of diagnoses.** Sentinel currently recognizes unsupported
  video/audio codecs, unsupported containers, unsupported secondary audio tracks, too many
  streams, external audio tracks, HDR/dynamic-range mismatches, audio channel downmixing,
  bitrate-only transcodes, resolution-only transcodes, and "transcoded but Jellyfin didn't say
  why." Still not covered by any rule at all: video profile/level/bit-depth/framerate/rotation
  mismatches, reference-frame or anamorphic/interlaced video issues, audio profile/sample-rate/
  bit-depth mismatches, subtitle codec mismatches other than burn-in, and a few rarer reasons —
  a transcode caused by one of these is recorded (the raw reason is visible in the database) but
  won't show a diagnosis on the dashboard yet. Also not yet covered: subtitle burn-in (needs
  subtitle-stream data this plugin doesn't collect yet), repeated-transcode-pattern detection and
  client-version-regression detection (both need a not-yet-built incident/history system), and a
  few explicitly experimental or unverified diagnoses from the original project plan
  (hardware-transcode-unavailable, mid-session direct-play failure, client-capability-gap,
  remote-bandwidth-limit) that need further research before they can be built without guessing.
  An unrecognized transcode is
  simply not diagnosed, not misdiagnosed.
- **Known Jellyfin core bugs are now called out separately.** When a diagnosis code has a
  matching entry in Sentinel's curated Known Core Issues table (currently just
  `TRANSCODE_REASON_MISSING`, linked to jellyfin/jellyfin#12193), the dashboard shows it as a
  distinct "Known Jellyfin Bug" note, not as a Sentinel-inferred diagnosis — so the two are never
  confused about which one is actually vouching for the explanation.
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
   folder without checking it still matches). **The two Windows native SQLite files must be
   renamed** from `e_sqlite3.dll` to `e_sqlite3.dll.win` when staging them into the zip —
   `dotnet publish` does not do this renaming itself, it's a packaging-time step. See the long
   comment above `artifacts:` in `build.yaml` for why this is required at all. Because you're
   staging exactly the `artifacts:` list rather than the raw publish folder, the unsupported RID
   folders (`win-x86`, `linux-arm`, etc.) are never included in the first place — that risk only
   applies to the "manual install from raw publish output" path above, not to a properly-packaged
   release.
4. Compute the zip's MD5 checksum (`md5sum <file>.zip` or equivalent).
5. Create a GitHub Release with that tag/version, uploading the zip as a release asset.
6. Add a new entry to the `versions` array in [`manifest.json`](./manifest.json): `version`
   (match `build.yaml`), `changelog`, `targetAbi` (match `build.yaml`), `sourceUrl` (the release
   asset's download URL), `checksum` (the MD5 from step 4), `timestamp` (UTC, ISO 8601).
7. Commit and push `manifest.json` to `main` — Jellyfin servers with the repository already added
   will pick up the new version automatically.
