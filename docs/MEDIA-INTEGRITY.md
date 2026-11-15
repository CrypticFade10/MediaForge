# Media Integrity Checker

Open **Media Integrity Checker** from the MediaForge launcher, or run `MediaForge.exe --tool media-integrity`.

Add local or network folders using the folder picker or by pasting a path and choosing **Add path**. All subfolders are included. Overlapping folder selections are deduplicated by path. Junctions and symbolic links are skipped and reported; inaccessible folders are listed separately. Both video and audio extensions are included and the extension list is editable.

## Checks

**Full decode** is the default. FFprobe inspects the container, then FFmpeg decodes all non-cover-art video streams and all audio streams to a null output. No converted media is saved, and the input files are opened read only. Subtitle, attachment and data streams are excluded. This tests decoding through to the end; it does not guarantee that every player supports the file or that a cleanly truncated file is complete. A decoded duration substantially shorter than the container duration is a warning for review.

**Quick container inspection** runs FFprobe only. A pass means that container metadata could be inspected, not that every frame plays. Each result and export records which check was performed.

## CPU and GPU

CPU decoding is the default. Optional GPU decoding uses the Windows D3D11VA backend, which can work with supported NVIDIA, Intel and AMD adapters and codecs. Availability depends on the installed drivers and each stream's format. There is no guaranteed speed improvement: GPU overhead, network bandwidth or storage can be the limit.

GPU checks inspect FFmpeg's decoder messages for confirmation that D3D11 frames were selected. A hardware failure, warning, stall, missing completion or unconfirmed hardware use causes a fresh full CPU check. A CPU retry result is labelled CPU and its GPU-attempt diagnostics remain available; a failed GPU attempt alone is not a corrupt-media verdict. Audio-only files use CPU decoding directly. The report records the decoder and whether a CPU retry occurred. Confirmed GPU support is tested opportunistically; CPU fallback can be used on machines with no compatible GPU.

Start with one parallel file for NAS libraries. One to four files can be checked concurrently. Each FFmpeg decode limits ordinary codec threads to reduce CPU contention. A stall timeout defaults to 120 seconds without advancing frame/time progress. A stalled helper is killed and reported as inconclusive. Cancel terminates running helper process trees, preserves completed results and marks interrupted files as cancelled. Files not started before cancellation are not assigned a pass or failure. An individual Windows filesystem call against an unavailable network share can still take time to return.

## Results

The default filter shows issues and incomplete checks; choose **All results** to see passes. Results distinguish:

| Status | Meaning |
|---|---|
| Passed | The selected check completed without reported errors or warnings. |
| Warnings | The check completed with diagnostics requiring review, or decoded duration was shorter than metadata. |
| Media errors | Recognised container/bitstream decoding errors were reported. |
| Inaccessible | A file could not be read or became unavailable; this is not a corruption verdict. |
| Unsupported | No eligible streams, a missing decoder, encryption or another recognised unsupported format. |
| Timed out | The helper stopped making progress; integrity is unknown. |
| Changed | The file size or modification time changed while checking; run it again. |
| Cancelled | An active check was interrupted. |
| Unable to check | Helper startup, output, runtime or unclassified errors prevented a reliable verdict. |
| Folder issue | An unavailable folder, inaccessible entry or skipped filesystem link during discovery. |

Select a result to read its summary and bounded decoder diagnostics. **Open containing folder** selects that path in Explorer. The checker has no deletion or repair actions.

**Export report** saves all results, regardless of the visible filter, as CSV or JSON. JSON includes options, timestamps and cancellation state. CSV protects against spreadsheet formulas in filenames/messages. Reports stay in memory until explicitly exported and are not automatically cached, including when Duplicate Finder history is enabled. Closing the tool discards its session results.

## Validation

`build.ps1` runs the dedicated `--integrity` regression checks alongside the existing Duplicate Finder suites. These include real generated FFmpeg fixtures for video, audio, multiple tracks, malformed/truncated containers, Unicode paths, source-file preservation and GPU-or-CPU fallback. Deterministic runner fixtures cover hardware confirmation, warnings, missing decoders, I/O and runtime failures, changed files and active scan cancellation. Real FFmpeg processes verify stall termination and cancellation. The packaged `--suite-smoke` check also runs a scan through the UI, checks filtering/diagnostics/export, and renders both themes.
