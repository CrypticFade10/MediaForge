"""Local speech detection and English translation worker for the MediaForge launcher."""

import argparse
import json
import mmap
import os
from pathlib import Path
import subprocess
import sys
from tempfile import NamedTemporaryFile

SAMPLE_RATE = 16000
VIDEO_EXTENSIONS = {".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".mpg", ".mpeg"}
ENGLISH_TAGS = {"en", "eng"}


def run_tool(command):
    result = subprocess.run(command, stdin=subprocess.DEVNULL, capture_output=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if result.returncode:
        raise ValueError(result.stderr.decode("utf-8", errors="replace").strip() or "Media tool failed.")
    return result.stdout


def choose_audio(streams, override):
    if override:
        if override > len(streams):
            raise ValueError(f"Audio track {override} is unavailable.")
        return streams[override - 1]
    english = [stream for stream in streams if stream_language(stream) in ENGLISH_TAGS]
    if english:
        # Prefer the default English track, and avoid commentary when tagged.
        return max(english, key=lambda stream: (
            not stream.get("disposition", {}).get("comment", 0)
            and "commentary" not in stream.get("tags", {}).get("title", "").casefold(),
            stream.get("disposition", {}).get("default", 0),
        ))
    if len(streams) == 1 and stream_language(streams[0]) in {"", "und"}:
        return streams[0]
    raise ValueError("No English audio track identified. Select a track manually if the English audio is untagged.")


def stream_language(stream):
    return stream.get("tags", {}).get("language", "").casefold().replace("_", "-").split("-")[0]


def find_dialogue(audio, spans, model, confidence):
    candidates = []
    uncertain = 0
    for index, (start, end) in enumerate(spans, 1):
        if index % 100 == 0:
            print(f"Pass 1: checked {index}/{len(spans)} speech segments.", flush=True)
        clip = audio[start:end]
        language, probability, _ = model.detect_language(audio=clip)
        if probability < confidence:
            uncertain += 1
        elif language != "en":
            candidates.append((start, end, language))

    if uncertain:
        print(f"Skipped {uncertain} speech segments with uncertain language.", flush=True)
    print(f"Pass 2: translating {len(candidates)} segment(s).", flush=True)
    for start, end, language in candidates:
        clip = audio[start:end]
        segments, _ = model.transcribe(
            clip, language=language, task="translate", condition_on_previous_text=False,
            beam_size=5, vad_filter=False, log_progress=False,
        )
        clip_length = len(clip) / SAMPLE_RATE
        for segment in segments:
            text = " ".join(segment.text.split())
            cue_start = max(0.0, segment.start)
            cue_end = min(clip_length, segment.end)
            if text and round(cue_end * 1000) > round(cue_start * 1000):
                offset = start / SAMPLE_RATE
                yield offset + cue_start, offset + cue_end, text, language


def timestamp(seconds):
    minutes, millis = divmod(round(seconds * 1000), 60000)
    hours, minutes = divmod(minutes, 60)
    seconds, millis = divmod(millis, 1000)
    return f"{hours:02}:{minutes:02}:{seconds:02},{millis:03}"


def write_srt(output, cues):
    # Stage on the destination filesystem so simultaneous runs cannot replace a sidecar.
    with NamedTemporaryFile(mode="w", encoding="utf-8", newline="\n", dir=output.parent,
                            prefix=".mediaforge-", suffix=".tmp", delete=False) as file:
        temporary = Path(file.name)
        try:
            for index, (start, end, text, _) in enumerate(cues, 1):
                file.write(f"{index}\n{timestamp(start)} --> {timestamp(end)}\n{text}\n\n")
            file.close()
            if os.name == "nt":
                temporary.rename(output)
            else:
                os.link(temporary, output)
        finally:
            file.close()
            temporary.unlink(missing_ok=True)


def speech_spans(audio, get_speech_timestamps, options):
    # Limit the VAD working set while retaining the original media timeline.
    block_size = 180 * SAMPLE_RATE
    for begin in range(0, len(audio), block_size):
        block = audio[begin:begin + block_size]
        for span in get_speech_timestamps(block, options):
            yield begin + span["start"], begin + span["end"]


def process_video(path, output, args, get_model):
    import numpy as np
    from faster_whisper.vad import VadOptions, get_speech_timestamps

    streams = json.loads(run_tool([
        args.ffprobe, "-v", "error", "-select_streams", "a", "-show_streams", "-of", "json", str(path),
    ])).get("streams", [])
    track = choose_audio(streams, args.audio_track)
    print(f"Decoding audio track {streams.index(track) + 1}.", flush=True)
    pcm = Path(args.work_dir) / "audio.f32"
    try:
        run_tool([
            args.ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-copyts",
            "-start_at_zero", "-i", str(path), "-map", f"0:{track['index']}",
            "-af", "aresample=16000:async=1:first_pts=0", "-ac", "1", "-ar", str(SAMPLE_RATE),
            "-c:a", "pcm_f32le", "-f", "f32le", str(pcm),
        ])
        if pcm.stat().st_size == 0:
            print("No audio samples.", flush=True)
            return False
        print("Pass 1: detecting speech and identifying its language.", flush=True)
        with pcm.open("rb") as file:
            with mmap.mmap(file.fileno(), 0, access=mmap.ACCESS_READ) as mapped:
                audio = np.ndarray(shape=(len(mapped) // 4,), dtype="<f4", buffer=mapped)
                options = VadOptions(min_speech_duration_ms=200, min_silence_duration_ms=300,
                                     max_speech_duration_s=6, speech_pad_ms=100)
                spans = list(speech_spans(audio, get_speech_timestamps, options))
                cues = list(find_dialogue(audio, spans, get_model(), args.confidence)) if spans else []
                del audio
    finally:
        pcm.unlink(missing_ok=True)

    if not cues:
        print("No confident foreign-language cues found.", flush=True)
        return False
    write_srt(output, sorted(cues))
    languages = ", ".join(sorted({language for _, _, _, language in cues}))
    print(f"Created {output} ({len(cues)} cues; detected languages: {languages}).", flush=True)
    return True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=Path)
    parser.add_argument("--ffmpeg", required=True)
    parser.add_argument("--ffprobe", required=True)
    parser.add_argument("--work-dir", required=True)
    parser.add_argument("--model", choices=["small", "medium", "large-v3"], default="medium")
    parser.add_argument("--device", choices=["cpu", "cuda"], default="cpu")
    parser.add_argument("--audio-track", type=int, default=0)
    parser.add_argument("--confidence", type=float, default=0.65)
    args = parser.parse_args()
    if args.audio_track < 0 or not 0 <= args.confidence <= 1:
        parser.error("Audio track must be nonnegative and confidence must be between 0 and 1.")

    try:
        from faster_whisper import WhisperModel
    except ImportError as error:
        print(f"Whisper runtime unavailable. Install it with: python -m pip install faster-whisper==1.2.1\n{error}")
        return 1

    model = None

    def get_model():
        nonlocal model
        if model is None:
            print(f"Loading Whisper {args.model} on {args.device} (first use may download model weights).", flush=True)
            model = WhisperModel(args.model, device=args.device,
                                 compute_type="int8" if args.device == "cpu" else "float16")
        return model

    root = args.path.resolve()
    if not root.exists():
        print(f"Path unavailable: {root}")
        return 1
    paths = [root] if root.is_file() else sorted(
        path for path in root.rglob("*") if path.suffix.lower() in VIDEO_EXTENSIONS and path.is_file()
    )
    if not paths:
        print("No supported video files found.")
        return 1

    created = skipped = no_cues = failed = 0
    for index, path in enumerate(paths, 1):
        output = path.with_suffix(".en.forced.srt")
        print(f"[{index}/{len(paths)}] {path}", flush=True)
        try:
            if output.exists():
                raise FileExistsError(output)
            if process_video(path, output, args, get_model):
                created += 1
            else:
                no_cues += 1
        except FileExistsError:
            skipped += 1
            print(f"Skipped existing {output}.", flush=True)
        except Exception as error:
            failed += 1
            print(f"Failed: {error}", flush=True)
    print(f"Completed: {created} created, {skipped} skipped, {no_cues} without cues, {failed} failed.")
    return int(failed > 0)


if __name__ == "__main__":
    sys.exit(main())
