# Forced Subtitle Generator

MediaForge scans an English audio track for foreign-language speech and writes an English sidecar such as `Cars 2.en.forced.srt`. It can combine Italian, Japanese and other languages supported by the multilingual Whisper model in the same file. The module works on a video or recursively on a folder, including Windows-accessible network shares.

## Setup

Use a 64-bit Python installation (Python 3.11 is a good starting point). In PowerShell, install the inference runtime into that Python environment:

```powershell
python -m pip install faster-whisper==1.2.1
```

In MediaForge, open **Forced Subtitle Generator**. Set **Python executable** to the same interpreter. The default is `python.exe` on PATH. It also accepts a full path to a virtual environment's `Scripts\python.exe`.

Choose a preset and select **Download selected model** to install it directly in MediaForge. No video or Python installation is needed for the download itself. Alternatively, **Generate forced SRT** downloads a missing preset automatically before starting the Python worker. Downloads show the current file, downloaded bytes and percentage, and can be cancelled with **Cancel**. Small, Medium and Large v3 are multilingual CTranslate2 Whisper models; downloads may use several GB of disk space. Medium remains the default.

After a model is downloaded, choose a video or folder and select **Generate forced SRT**. Try a single film first. CPU is the default. Inference runs on your machine, and media and audio stay local. Python and faster-whisper are still required for inference, as described above; installing a model does not install that runtime.

## Portable working folder and model downloads

The default folders are beside the actual EXE, regardless of the directory it is launched from:

```text
MediaForge.exe
MediaForge-Data/
  models/       downloaded, verified model folders
  work/         temporary subtitle work (removed after each run)
```

Use **Model download folder** or **Choose storage…** to change where models are stored, and **Open model storage** to open the selected folder. The EXE folder must be writable for temporary work. Keep the `MediaForge-Data` folder with the EXE when moving the application if you want the models available offline. A custom download-folder choice applies to the current window; it can also be selected again after reopening the tool.

MediaForge downloads model files directly from the matching public `Systran/faster-whisper-*` repository on Hugging Face. It pins every file to the same repository revision and verifies sizes and published SHA-256/Git blob checksums before installing the model. All model files, including available vocabulary and preprocessor configuration, are retained together. Existing model folders are never overwritten. Incomplete or cancelled transfers are not treated as installed models; retrying starts a fresh download. If an explicitly requested re-download finds a complete matching installation, it verifies and reuses it.

Completed presets are detected locally on later runs, without contacting Hugging Face. The subtitle worker is always given the downloaded folder in local-only/offline mode, so normal generation does not fall back to a hidden model download. Local corruption or missing model files can be addressed with **Download selected model** again. A fresh verified copy is installed without deleting the older folder. Download errors are shown in the log; if a firewall blocks Hugging Face or its model CDN, allow those hosts or choose a previously downloaded local model.

## Use your own model

Select a complete model folder with **Choose model…**, including models you already keep in the previous app-data location or elsewhere. The picker expects the folder containing `model.bin`, `config.json`, and `tokenizer.json`. Keep any vocabulary and preprocessor files from the same download alongside them. Clear the local model field to use the preset again. The download button selects the downloaded preset by clearing a local override when the download completes.

Use a **multilingual Whisper model in CTranslate2 (faster-whisper) format** that supports speech-to-English translation. A raw PyTorch, Safetensors or Whisper.cpp GGML/GGUF checkpoint needs a compatible CTranslate2 export. English-only and transcription-only models such as Whisper Turbo are unsuitable for this translation workflow. Local model selection uses the supplied files without downloading weights.

## NVIDIA hardware acceleration

Set **Device** to **NVIDIA CUDA** to use the GPU for Whisper language identification and translation, with either a preset or a local model. FFmpeg audio extraction and Silero speech detection still run on the CPU. Use compatible NVIDIA drivers and the CUDA/cuBLAS and cuDNN versions required by your installed CTranslate2 build. See the [faster-whisper GPU setup](https://github.com/SYSTRAN/faster-whisper/tree/v1.2.1#gpu).

The log shows the chosen device and compute type. CUDA uses FP16 when supported, then checks INT8 with FP32 and FP32. If the CUDA model cannot load, check the logged error for details. Select CPU explicitly to run without CUDA.

## Audio and output

- Automatic audio selection prefers tagged English tracks, giving preference to non-commentary and default tracks. A single untagged track is assumed to be English. Use the 1-based audio track field for an untagged English dub in a multi-track file, or to choose a particular English track. Use automatic mode for folders containing differently ordered tracks.
- The first pass uses voice activity detection and identifies the language of each short speech segment independently. The second pass translates confident non-English segments, keeps their positions on the original timeline, and merges the results into a single ordered SRT. You do not need to enter a list of languages or run the module once per language.
- Existing sidecars with the exact output name are skipped, and videos are opened read-only. A video with no usable foreign-language cues gets no new sidecar. The log reports the languages found and any errors. The destination folder must be writable.
- Temporary decoded audio uses about 230 MB of local disk per hour of video. Cancel stops the current worker and keeps completed SRTs.
- Subtitle playback and automatic selection depend on your player. The sidecar filename uses conventional English/forced tags.

## Review the result

Language detection works best on clear phrases. Brief lines, speech mixed with music, overlapping voices and language changes within one short segment can be missed or misidentified. Whisper supports many languages, with varying quality. Review the generated cues before relying on them. This tool infers foreign dialogue from the soundtrack. It cannot recover the editorial choices of an official forced track, such as dialogue deliberately left untranslated, or read signs and other on-screen text.
