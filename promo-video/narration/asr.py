"""Transcribe generated narration with Whisper and measure how well it matches the script.

Runs in the "asr" environment (see setup-tts.sh):
    python asr.py --audio-dir out/voice --script script.json --out out/asr.json [--ids a,b]

For every line it records Whisper's text, word timestamps, and a character error rate (CER) against the script text
after Arabic normalisation. The CER is an objective intelligibility signal: it is not a judgement of how natural the
voice sounds, which needs a human listener.
"""
import argparse
import json
import re
import sys
import unicodedata
from pathlib import Path

import numpy as np
import soundfile as sf
import torch
from transformers import pipeline

DIACRITICS = re.compile(r"[ؐ-ًؚ-ٰٟۖ-ۭـ]")
PUNCT = re.compile(r"[^\w\s]", re.UNICODE)


def normalize(text: str) -> str:
    """Arabic-aware normalisation so spelling variants Whisper produces do not count as errors."""
    t = unicodedata.normalize("NFKC", text)
    t = DIACRITICS.sub("", t)
    t = re.sub("[إأآٱ]", "ا", t)
    t = t.replace("ى", "ي").replace("ة", "ه").replace("ؤ", "و").replace("ئ", "ي")
    t = PUNCT.sub(" ", t)
    return re.sub(r"\s+", " ", t).strip().lower()


def cer(ref: str, hyp: str) -> float:
    r, h = ref.replace(" ", ""), hyp.replace(" ", "")
    if not r:
        return 0.0 if not h else 1.0
    prev = list(range(len(h) + 1))
    for i, rc in enumerate(r, 1):
        cur = [i] + [0] * len(h)
        for j, hc in enumerate(h, 1):
            cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (rc != hc))
        prev = cur
    return prev[-1] / len(r)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--audio-dir", required=True)
    ap.add_argument("--script", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--ids", default="")
    ap.add_argument("--model", default="openai/whisper-large-v3-turbo")
    ap.add_argument("--field", default="say", help="script field to compare against: say (what was spoken) or text")
    args = ap.parse_args()

    script = json.loads(Path(args.script).read_text(encoding="utf-8"))
    lines = {l["id"]: l for s in script["scenes"] for l in s["lines"]}
    only = [i for i in args.ids.split(",") if i]
    audio_dir = Path(args.audio_dir)
    files = sorted(audio_dir.glob("*.wav"))
    if only:
        files = [f for f in files if f.stem in only]

    device = "cuda:0" if torch.cuda.is_available() else "cpu"
    asr = pipeline(
        "automatic-speech-recognition",
        model=args.model,
        torch_dtype=torch.float16 if device != "cpu" else torch.float32,
        device=device,
    )

    out_path = Path(args.out)
    results = json.loads(out_path.read_text(encoding="utf-8")) if out_path.exists() else {}
    for f in files:
        audio, sr = sf.read(str(f), dtype="float32")
        if audio.ndim > 1:
            audio = audio.mean(axis=1)
        if sr != 16000:
            # Whisper works at 16 kHz; passing 24 kHz audio makes its word timestamps run about 1.5x too long.
            import torchaudio.functional as AF
            audio = AF.resample(torch.from_numpy(audio), sr, 16000).numpy()
            sr = 16000
        res = asr(
            {"raw": audio, "sampling_rate": sr},
            return_timestamps="word",
            generate_kwargs={"language": "arabic", "task": "transcribe"},
        )
        words = [
            {"w": c["text"].strip(), "s": round(float(c["timestamp"][0] or 0), 3), "e": round(float(c["timestamp"][1] or c["timestamp"][0] or 0), 3)}
            for c in res.get("chunks", [])
            if c["text"].strip()
        ]
        key = f.stem  # "<voice>__<line id>" for samples, "<line id>" for narration
        line = lines.get(f.stem.split("__")[-1], {})
        ref = line.get(args.field) or line.get("text", "")
        score = cer(normalize(ref), normalize(res["text"]))
        results[key] = {"file": f.name, "text": res["text"].strip(), "words": words, "cer": round(score, 4), "duration": round(len(audio) / sr, 3)}
        print(f"{key}\tCER {score:.3f}\t{res['text'].strip()}", flush=True)

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(results, ensure_ascii=False, indent=1), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
