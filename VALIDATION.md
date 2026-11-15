# MediaForge 3.1 validation - 15 November 2026

MediaForge - Modular Media Management Suite. First tool: Duplicate Finder 3.0.

- Full regression suite: 126 passed; two junction fixture checks skipped because this environment cannot create junctions.
- Duplicate Finder feature suite: 26 passed.
- Modular launcher suite: 15 passed, including registry validation, search, one window per tool, reopening, initialisation, isolated module directories and retry after factory errors.
- Final single-EXE smoke checks: passed with an invalid installed-runtime root, from a directory containing only MediaForge.exe. Bundled .NET 10.0.12; no installed shared-runtime modules loaded.
- Duplicate Finder GUI: 100,000 rows and 50,000 groups, load 0.68 seconds, bulk selection 1.77 seconds, seven rendered rows with virtualisation enabled. All safety/history/review smoke assertions passed.
- Light/dark launcher renders inspected. Existing Duplicate Finder module smoke checks passed.
- Publish succeeded with zero compiler errors. NuGet vulnerability-feed checks reported NU1900 warnings because this environment cannot reach api.nuget.org; dependency vulnerability auditing was not completed.
- EXE size: 143409711 bytes. SHA-256: 1b2a2b38de918782650fc24dcd2beab7ec51880f47836cf0346cf1d9ade71519
- Local evidence: artifacts/mediaforge-release.log, artifacts/mediaforge-final-build.log, artifacts/mediaforge-launcher-final/suite-smoke.json and artifacts/mediaforge-duplicate-final-smoke/smoke.json.

Real NAS disconnection testing remains outside this validation. GitHub CI results are recorded by the repository workflow separately.

Earlier standalone application validation follows as historical context.

---
# V3 validation - 13 November 2026

Current executable: dist/SimilarVideoFinder.exe (version 3.0.0).
Size: 143374025 bytes. SHA-256: 06BA569D54D5C36DA7334F11348BEDCCA2636FC848E5CD5AFA3B7300A239C6C2

- Full regression suite: 126 passed. Junction creation is unavailable in this managed environment; the two fixture-dependent checks are visibly skipped and unrelated checks continue.
- V3 feature suite: 26 passed (direct witnesses, full-group discard scope, protected references, cancellation, quarantine/restore and overwrite protection, review identity, retention/clear/stale cache management, exclusions, track warnings, keep rules and visual nonrepresentative matching).
- Release build: succeeded with no compiler warnings or errors. The build script bundles with a neutral intermediate suffix and renames only the final PE to .exe to avoid intermediate executable locking in this environment.
- The release executable was copied into artifacts/v3-standalone, containing only SimilarVideoFinder.exe. Smoke process exit code 0; Success true. Runtime .NET 10.0.12; core assembly embedded; no installed .NET shared-runtime modules were loaded. SQLite and FFmpeg were extracted automatically by the application/runtime.
- Standalone WPF smoke: 100,000 rows, 50,000 groups; load 0.6178348 seconds; bulk selection 1.8185731 seconds. Seven rows rendered (virtualization enabled).
- Smoke assertions passed for path selection synchronization, filtered bulk selection, direct retained copies, references, numeric sorting, refreshed deletion results, group counts/collapse, history opt-in/out, dark mode and typed whole-group/network reviews.
- Rendered light/dark screenshots inspected for main review, comparison, preferences and whole-group confirmation. The comparison test swaps the two paths and decodes at an arbitrary 50% position.
- Evidence: artifacts/v3-regression.log, artifacts/v3-features.log, artifacts/v3-bundle.log and artifacts/v3-final2-smoke/smoke.json. Screenshots are alongside the smoke JSON.

Live NAS disconnection, mapped-drive/UNC alias behavior and slow filesystem calls were not validated against a physical share. Tests/NasChecks.cs adds an opt-in disposable real-share test for hashing, confirmation routing and unavailable retained-copy protection; it has not been run without a supplied share.

Earlier validation below is historical and does not describe the current executable.

---
# Release 2.0 validation — 9 October 2026

Release: `dist/SimilarVideoFinder.exe`, Windows x64, version 2.0.0.0, 143,344,752 bytes.

SHA-256: `79AEDAE480720AB0E903C97478F67F6FAB55EEB2AA4FB2985B7A09535A7FB073`.

The full regression suite passed **128 checks**. Report: `artifacts/tests-20261009-133847/validation.json`.

- Matching regressions include conflicting group years, meaningful edition tags, existing episode cases, exact SHA-256 and visual matching.
- Privacy checks cover history OFF by default, memory-only operation, explicit persistence, restart and opt-out. The application remembers only the history/theme switches when history is OFF; existing saved history is preserved but ignored.
- Reference checks cover path boundaries, inventory inclusion, bulk selection, direct reference evidence, current-file revalidation and protection at the deletion backend.
- Deletion checks cover retained copies, survivor regrouping, cancellation, audit preflight and late audit-write failure without losing completed outcomes.
- Network checks cover UNC/extended UNC classification, extended local paths, skipping recycling, required permanent-deletion consent and separation from local-drive fallback consent. The initial network review requires typing DELETE; it does not trigger a second network confirmation.
- Media checks cover colour PNG previews, cancellation, audio/subtitle metadata, HDR/bit depth and equality of batched versus individual frame fingerprints. Batched extraction took 0.34 seconds versus 1.02 seconds for individual frame calls in the disposable fixture; identical SHA-256 copies reused fingerprints.

The exact standalone executable passed GUI acceptance with **100,000 rows / 50,000 groups**, in a folder containing only the EXE, with an invalid installed-runtime root and a restricted PATH. It used its bundled .NET 10.0.12 runtime; no installed .NET module was loaded. Report: `artifacts/v2-network-acceptance/smoke.json`.

- Result loading: 0.57 seconds; bulk selection: 0.22 seconds; eight rendered row containers.
- Initially empty selection, selection synchronisation, filter-aware bulk selection and retained-copy protection passed.
- Numeric sorting, collapsible groups, selected-count/size summaries, reference protection and post-deletion refresh passed.
- History default/opt-in/opt-out, dark-theme preference and typed network-deletion review passed.
- Light/dark result, reference, evidence and deletion-review screenshots were visually inspected, including `app-network-deletion-dark.png` in the latest acceptance directory.

The release build completed without warnings. The workspace shortcut now targets the V2 executable; `build.ps1` recreates that target on subsequent builds.

## Limits of validation

Actual NAS throughput, live share disconnections and mapped-drive behaviour against a real share remain unverified. Network deletion was exercised through an injected backend and synthetic GUI review. This environment could not recycle the disposable native test file; the test verified that unavailable recycling leaves it intact pending explicit permanent-deletion consent. Permanent deletion tests used disposable files only. No user media was scanned or deleted. GUI checks used in-process controls and WPF renders, not external desktop automation.

# Release 1.1 validation — 8 October 2026

Release: `dist/SimilarVideoFinder.exe`, Windows x64, 143,317,247 bytes.

SHA-256: `6F931FEAFDE1F094143BDC9B4AABA062EC13043E94F1AA4CC7E1069511E5B5C5`.

17 focused bulk-selection checks and 11 existing deletion checks passed. These cover largest/smallest/first retained-copy policies, filters, case-insensitive paths, deterministic ties, overlapping groups, retained-copy protection, changed files, declined permanent deletion and native Windows recycling.

The exact standalone executable passed GUI acceptance with 100,000 rows and 50,000 groups. Bulk selection took 0.16 seconds, selected 50,000 files, and left a retained copy in every group. Filtered selection, selection synchronisation and initially empty selection passed. Only eight row containers were rendered. Report: `artifacts/bulk-selection-verified/smoke.json`. The bulk-selection screenshot was visually inspected. The release build completed without warnings.

Large-selection validation now uses indexed path membership; selected and retained files are still revalidated immediately before each deletion. Bulk selection changes checkboxes only and requires the existing deletion review and confirmation.

The original full-suite results below remain the baseline for unchanged matching and scanning behavior; that complete suite was not rerun for this update.

# Release 1.0 validation — 8 October 2026

Release: `dist/SimilarVideoFinder.exe`, Windows x64, 143,311,190 bytes.

SHA-256: `ECEA938A88DA6EBC1BBF08A9B529725B7E6069D6AE19D36396784B6284237F6E`.

## Automated regressions

73 checks passed. Full report: `artifacts/tests-20261008-141623/validation.json`.

- All five supplied filename pairs match: Top Gear, The Simpsons, Dance Moms, Big Mouth and SpongeBob, including malformed metadata.
- The folder-local episode example produces only Folder 1's S01E03 group and Folder 2's S01E01 group. Parent/subfolder scopes remain separate.
- Different seasons, episodes and explicit years are rejected; episode ranges and multi-episode sets normalise correctly. Meaningful film-edition tags remain significant.
- Full SHA-256 finds renamed exact copies, rejects equal-size changed files and reuses cached results. Forced rescans, changed timestamps, cancellation and SQLite restart recovery are covered.
- Generated H.264, H.265, MPEG-4, resized and letterboxed/pillarboxed copies match by sampled frames; unrelated content and shared-intro-only videos remain separate.
- Corrupt videos, unavailable folders, long/Unicode paths, overlapping roots, junction cycles and decoder timeouts are covered.
- Deletion tests use newly created disposable files only: retained-copy protection, changes to selected/retained files, declined permanent fallback, partial failures and overlapping groups. Native Windows recycling and the non-recyclable callback veto both pass.
- 100,000 synthetic filename records complete within the test's 30-second limit and 700 MB additional-memory bound; observed earlier full-suite run was about 0.7 seconds and 89 MB.

## Standalone GUI acceptance

The exact release executable was copied into a directory containing only that executable and launched with an invalid installed-runtime root and PATH restricted to Windows system utilities. It used its bundled .NET 10.0.12 runtime, embedded core assembly, extracted SQLite native library and checksum-verified bundled video tools. No installed .NET module was loaded.

GUI acceptance report: `artifacts/release-smoke/smoke.json`. All checks passed:

- 100,000 result rows / 50,000 groups load in 0.30 seconds; only nine row containers are rendered.
- Initial deletion selection is empty, and selection synchronises across searches for the same path.
- Ready, populated-results, evidence-comparison and both deletion-confirmation windows were rendered offscreen and visually inspected. Permanent deletion starts disabled and requires typing DELETE.
- Release build completes without warnings. The delivery directory contains one EXE; runtime and FFmpeg installation are unnecessary.

## Limits of validation

Actual NAS throughput, live share disconnections and mapped-drive/UNC aliasing against an available share remain unverified because the configured project share was inaccessible. The installed runtimes were left untouched; isolation was verified within the executable's process. GUI verification used in-process controls and WPF renders, since the external computer-use helper could not start in this execution environment. No user media was scanned or deleted.
