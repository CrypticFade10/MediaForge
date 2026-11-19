<p align="center"><img src="assets/mediaforge.png" alt="MediaForge icon" width="160" /></p>

# MediaForge

**Modular Media Management Suite**

Distributed as **one standalone EXE**. Includes Duplicate Finder, Media Integrity Checker and Forced Subtitle Generator; future tools can be added without changing the launcher.

Open `dist/MediaForge.exe` on 64-bit Windows. The home screen lists installed tools and supports search and a shared light/dark theme. Open tools run in their own windows in the same application. Opening a tool again activates its existing window; closing it lets you reopen a fresh session. Close tool windows before exiting the launcher so ongoing file operations retain their existing close safeguards.

The EXE includes the .NET runtime, SQLite, FFmpeg and FFprobe. Duplicate Finder and Media Integrity Checker run with the bundled tools. Forced Subtitle Generator uses a separately installed Python inference runtime.

## Forced Subtitle Generator

Open the module for a video or a folder. It detects foreign-language speech in English audio and combines the translations in an external `.en.forced.srt` beside each video. See the [Forced Subtitle Generator guide](docs/FORCED-SUBTITLES.md) for setup, audio track selection and model settings.

## Build and extend

See [Media Integrity Checker](docs/MEDIA-INTEGRITY.md) for recursive media health checks, optional GPU decoding, CPU retries, result statuses and report exports.

Run `./build.ps1` in PowerShell on Windows. It downloads the pinned SDK and video tools when needed, checks their hashes, runs both regression suites and publishes `dist/MediaForge.exe`. Build dependencies and generated files are excluded from source control.

Use `MediaForge.exe --tool duplicate-finder` to open Duplicate Finder directly. Existing Duplicate Finder data remains at `%LOCALAPPDATA%\SimilarVideoFinder`; no migration or reset is required. The existing `--data-dir` and `--smoke` options remain supported. `--suite-smoke --data-dir <empty-test-folder>` checks the launcher and writes a report and screenshots.

See [Adding tools](docs/ADDING-TOOLS.md) for the module contract and registration steps. Modules are compiled into a release, so users continue to copy just one EXE.

## GitHub builds

The included GitHub Actions workflow builds and tests on Windows for pushes and pull requests, and stores the EXE as a downloadable build artifact. Pushing a version tag such as `v3.1.0`, or including `[release]` in a push commit message, also creates a GitHub release with the single EXE and its SHA-256 checksum after all checks pass. For the commit-message route, the release tag comes from the version in the App project; increment that version before creating another release. The EXE is intentionally excluded from Git history because it exceeds GitHub's regular file-size limit.

## Duplicate Finder 3.0

Open Duplicate Finder from the MediaForge home screen. Windows extracts native components to its local application cache at runtime; these are not files you need to distribute. The remaining notes describe the Duplicate Finder module, including historical names for earlier standalone releases.

## New review controls

- **Delete whole group** selects every eligible file in the chosen group. The final review requires typed `DELETE` and explains that a surviving copy is no longer required. Reference protection cannot be overridden. Ordinary checkbox and bulk cleanup selections still require directly matching retained copies. Selections apply to every occurrence of a path across searches.
- **Quarantine selected** copies, SHA-256 verifies and then removes each selected original. The chosen folder contains the copies and `.restore.json` manifests. **Restore quarantine** restores a verified copy without overwriting an existing file. Keep the manifests together with the quarantined files. Quarantine remains stored even when scan history is off.
- **Scan / keep preferences** sets excluded folders, wildcard filename exclusions (`*sample*;*trailer*`), minimum MiB/duration and keep preferences. Filename and size exclusions happen before expensive processing; a duration filter necessarily probes metadata. Keep rules support folder, resolution, codec, HDR/SDR, audio language and subtitle count, in addition to file size/first result. Metadata-based rules load metadata as needed. A preview shows proposed selections, retained matches and potential track losses before applying them. Missing metadata is flagged rather than assumed safe.
- **Compare evidence** combines direct evidence across all enabled searches for a pair. Either group member can be selected on either side. The timeline supports an arbitrary percentage as well as the 12 samples. Saved Different/Keep both/Reviewed decisions are symmetric and invalidated by path, size or timestamp changes. They are review annotations, not automatic deletion permissions. History off keeps decisions only for the session.
- **Manage cache** shows database statistics, removes stale file records or clears history, settings, cached work, decisions and saved deletion/error logs. Completed scan reports are limited to the latest 30. Stale-path checks may wait for the operating system when a network share is unavailable.
- Deletion/quarantine has file-level progress and cancellation between files. Cancel does not interrupt an in-progress quarantine copy or shell deletion. Completed outcomes remain visible.
- Scan progress shows elapsed time, cache hits, remaining file counts, stage estimates and measured hash I/O. Hash throughput is averaged across elapsed scan time and excludes FFmpeg reads.

The filename and visual indexes include all records. Overlapping groups preserve direct matches through nonrepresentative members without assuming that fuzzy matching is transitive. Groups may overlap more than before. Explicit conflicting years remain separated.

## Validation

The new feature tests are in `Tests/V3Checks.cs`; run the existing test executable with `--v3` to run them independently. The full regression harness continues when the environment cannot create its junction fixture, printing a visible SKIP.

`--nas-root \\server\share\test-folder` enables disposable real-share checks for complete hashing, permanent-confirmation routing and unavailable retained copies. It writes only a uniquely named fixture directory and leaves the retained fixture for inspection. No physical disconnect or real NAS validation is claimed without an actual supplied share. Mapped-drive alias and mid-I/O disconnection checks remain live-environment work.

The original architecture and matching notes follow; the V3 behavior above supersedes their older keep-one and representative-only descriptions.

---
# Similar Video Finder

A Windows x64 desktop app for large video libraries on local drives, USB drives and NAS shares. Distributed as one self-contained executable.

## Using the app

1. Launch `MediaForge.exe` and open Duplicate Finder. The first launch extracts its bundled, checksum-verified FFmpeg tools into your local app-data folder.
2. Add folders, or paste a local/UNC path and press Enter. Subfolders are included by default. Optionally add **protected reference folders**: these are scanned too, but their files are never selected or deleted. When references are configured, only other files with a direct reference match can be deleted.
3. Choose searches and click **Start scan**. Name searches are inexpensive. Complete hashing and video fingerprinting read media from your drives; one read worker is the default for NAS use.
4. Review the collapsible groups. Select a file row and use **Compare evidence**, **Open video** or **Open folder**. Comparison uses colour previews with zoom and shows audio/subtitle tracks, languages, channels, bit depth, HDR and bitrate when available. Sorts are numeric for size, duration, score and resolution; grouped sorting keeps each group together. Switch **Group results** off for a flat table. Filter by search type or path.
5. Use the **Delete** checkboxes only for files you want to remove. **Review deletion** shows the complete selection. Network-share files default to direct permanent deletion: the first review requires typing `DELETE`, and no recycling attempt or second network confirmation is made. Local files use the Recycle Bin where supported; unsupported local files require separate permanent confirmation.

**Select all but one per group** prepares the deletion checkboxes in bulk. Choose **Keep largest file** (default), **Keep smallest file**, or **Keep first result**. The action replaces the previous selection and considers only files in the current visible results; filtered-out files are not newly selected. Groups with fewer than two visible eligible files are skipped. File size is a preference, not a guarantee of quality. Overlapping search groups may leave extra copies unchecked so every group still retains a copy. You can adjust any checkbox before using **Review deletion**.

With references, the protected files are always retained even outside the filter, and every visible eligible non-reference match can be selected. Groups without a direct reference match are skipped. Collapsed groups are excluded from bulk selection. Reference protection applies in the GUI, bulk selection, deletion service and native backend; changed/unavailable matching references block deletion. Run a fresh scan after changing reference folders. Folder-local episode matching retains its folder-local scope.

**Dark mode** changes the main window and dialogs. **Save history: OFF / ON** is the global persistence switch, OFF by default. It controls remembered folders/settings, scan reports, fingerprints/content-hash cache, deletion audit and error logs. Only the two history/theme switches are remembered while history is off. Existing saved history is retained on disk when switched off, but is not loaded or extended. Explicit CSV exports remain available.

No files are selected for deletion automatically. Selecting a path in one search selects it everywhere. At least one current copy must remain in every group. Changed, missing or unavailable files are skipped; run another scan to refresh them. Completed outcomes remain in **Deletion outcomes**, including audit-write failures; surviving groups and counts refresh after deletion. Mixed network/local selections explicitly confirm network deletion while preserving separate consent for any local permanent fallback. Mapped network drives are recognized as well as UNC paths.

## Searches

| Search | Behaviour |
|---|---|
| Identical filename | Complete filename including extension, case-insensitive; a candidate only. |
| Identical contents | Equal size and complete SHA-256 hash. Partial samples are used only to exclude differences. |
| Similar filename | Normalised show/title tokens; quality, audio, codec and release tags ignored individually. Edition tags survive mixed brackets such as `[1080p Directors Cut]`. Conflicting years are rejected across the whole group, including groups with a yearless representative. `S01E01` and `1x01` are equivalent. Episode-based comparisons use the series title rather than variable episode-title text. |
| Same episode in same folder | Groups equal season/episode sets within each immediate containing directory, independently of title. Different folders, including a parent and its subfolder, are separate scopes. |
| Similar video content | Twelve sampled frame perceptual hashes, duration compatibility and at least nine informative matching frames. Finds complete re-encodes with unrelated names; candidates require review. |

All searches are independently selectable. Filename/content searches can compare across selected folders; the episode-in-folder search is strictly folder-local. Multi-episode files match equivalent episode sets, not individual episodes from that set. Edited versions, partial overlaps and audio-only similarity are outside this version's visual-matching scope. Static/very dark videos may have insufficient informative frames.

**Advanced settings:** editable extensions, filename threshold (default 90%), frame distance (default 10; lower is stricter), read workers (1–4), and Force rescan. Force rescan ignores completed cached work for that scan. Different encodes may contain different audio/subtitle tracks even when their video looks the same.

## Cache, recovery and history

With history OFF, SQLite runs entirely in memory. Completed work can be reused in the same session and is discarded when the app closes. No library database, scan settings, audit or error logs are written. The extracted video tools still live on disk, and `preferences.json` contains only the history and theme switches.

With history ON, the cache, settings, reports and audit are stored in `%LOCALAPPDATA%\SimilarVideoFinder`. Scans checkpoint changed hashes/fingerprints in bounded batches (64 files or about one second, and on cancellation/stage completion), avoiding unchanged cache writes. Restarting a scan reuses saved completed work; unfinished work restarts. Size, path, modification time and algorithm version must still match. **Previous scans** opens saved reports; failed and cancelled states are distinct. Version 2 refreshes older algorithm-version cache entries. Force rescan is available if media changed without updating its size or timestamp.

Fingerprinting performs twelve accurate seeks in one FFmpeg process. SHA-256-identical files reuse one fingerprint extraction. Changing preview positions cancels superseded decoding requests. Scan cancellation is checked between result-validation filesystem operations; an individual blocking Windows/NAS filesystem call must still return before cancellation can complete.

No media is uploaded and no server/account is required. FFmpeg helper processes have timeouts and are terminated on cancellation. Inaccessible folders, corrupt files and decoding failures appear under **Issues**. Reports display the first 1,000 issues and indicate any omitted count.

## Building and testing

Run `powershell -ExecutionPolicy Bypass -File .\build.ps1` from the project folder. The script downloads a checksum-verified .NET SDK into `build-tools`, obtains the pinned FFmpeg binaries if missing, runs the regression harness and publishes `dist\MediaForge.exe`. It needs network access for initial dependencies; end users need no downloads or preinstalled runtime. `-SkipTests` only skips the regression harness.

The source is split into Core (inventory, matching, SQLite, export, guarded deletion), App (WPF and bundled tools) and Tests (generated/disposable fixtures). Dependency notices and the FFmpeg licence are embedded in **About / help**.

For a noninteractive rendered GUI smoke test:

```powershell
.\dist\MediaForge.exe --smoke --data-dir .\artifacts\smoke-cache --output-dir .\artifacts\smoke --smoke-library <fixture-folder> --smoke-count 100000
```

This mode renders both light/dark themes offscreen, exercises synthetic selection/reference/group cleanup and history/theme switches, exports diagnostics/screenshots, and exits nonzero if acceptance checks fail. It performs no media deletion. Use a fresh disposable `--data-dir` because the acceptance test checks default-off history, then tests opting in and out. The regression harness deletes/recycles only the disposable fixtures it creates under `artifacts`.

Validation reports are under `artifacts`. Actual NAS transfer rates and live disconnection behaviour require testing against an available share.
