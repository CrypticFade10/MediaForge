# Forced Subtitle Generator

MediaForge scans an English audio track for foreign-language speech and writes an English sidecar such as `Cars 2.en.forced.srt`. It can combine Italian, Japanese and other languages supported by the multilingual Whisper model in the same file. The module works on a video or recursively on a folder, including Windows-accessible network shares.

## Setup

Use a 64-bit Python installation (Python 3.11 is a good starting point). In PowerShell, install the inference runtime into that Python environment:

```powershell
python -m pip install faster-whisper==1.2.1
```

In MediaForge, open **Forced Subtitle Generator**. Set **Python executable** to the same interpreter. The default is `python.exe` on PATH. It also accepts a full path to a virtual environment's `Scripts\python.exe`.

Choose a video or folder and select **Generate forced SRT**. Try a single film first. CPU is the default. Small is faster, Medium is the default quality/speed trade-off, and Large v3 is more resource-intensive. NVIDIA CUDA uses the [faster-whisper GPU runtime requirements](https://github.com/SYSTRAN/faster-whisper/tree/v1.2.1#gpu). The first run downloads the chosen model into the local Hugging Face cache. Inference runs on your machine, and media and audio stay local.

## Audio and output

- Automatic audio selection prefers tagged English tracks, giving preference to non-commentary and default tracks. A single untagged track is assumed to be English. Use the 1-based audio track field for an untagged English dub in a multi-track file, or to choose a particular English track. Use automatic mode for folders containing differently ordered tracks.
- The first pass uses voice activity detection and identifies the language of each short speech segment independently. The second pass translates confident non-English segments, keeps their positions on the original timeline, and merges the results into a single ordered SRT. You do not need to enter a list of languages or run the module once per language.
- Existing sidecars with the exact output name are skipped, and videos are opened read-only. A video with no usable foreign-language cues gets no new sidecar. The log reports the languages found and any errors. The destination folder must be writable.
- Temporary decoded audio uses about 230 MB of local disk per hour of video. Cancel stops the current worker and keeps completed SRTs.
- Subtitle playback and automatic selection depend on your player. The sidecar filename uses conventional English/forced tags.

## Review the result

Language detection works best on clear phrases. Brief lines, speech mixed with music, overlapping voices and language changes within one short segment can be missed or misidentified. Whisper supports many languages, with varying quality. Review the generated cues before relying on them. This tool infers foreign dialogue from the soundtrack. It cannot recover the editorial choices of an official forced track, such as dialogue deliberately left untranslated, or read signs and other on-screen text.
