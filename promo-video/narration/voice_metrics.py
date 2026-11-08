"""Objective measurements of generated speech, to compare voices without claiming to judge naturalness.

    python voice_metrics.py out/samples [--asr out/samples-asr.json] > out/voice-comparison.md

Per file: duration, median pitch (F0; under ~165 Hz suggests a male voice), pitch spread, speaking rate, the longest
silence inside the speech (a sign of dropouts), clipping, and Whisper's character error rate if available.
"""
import json
import sys
from collections import defaultdict
from pathlib import Path

import numpy as np
import soundfile as sf


def f0_track(x: np.ndarray, sr: int, fmin=70.0, fmax=350.0):
    """Frame-wise pitch by normalised autocorrelation on voiced frames."""
    frame, hop = int(0.04 * sr), int(0.01 * sr)
    lo, hi = int(sr / fmax), int(sr / fmin)
    out = []
    for i in range(0, len(x) - frame, hop):
        f = x[i:i + frame]
        if np.sqrt(np.mean(f ** 2)) < 0.02:
            continue
        f = f - f.mean()
        ac = np.correlate(f, f, mode="full")[frame - 1:]
        if ac[0] <= 0:
            continue
        ac = ac / ac[0]
        lag = lo + int(np.argmax(ac[lo:hi]))
        if ac[lag] > 0.45:
            out.append(sr / lag)
    return np.array(out)


def longest_gap(x: np.ndarray, sr: int, db=-40.0) -> float:
    frame = int(0.01 * sr)
    n = len(x) // frame
    rms = 20 * np.log10(np.sqrt(np.mean(x[: n * frame].reshape(n, frame) ** 2, axis=1)) + 1e-9)
    best = run = 0
    for v in rms:
        run = run + 1 if v < db else 0
        best = max(best, run)
    return best * 0.01


def main() -> int:
    folder = Path(sys.argv[1])
    asr = {}
    if "--asr" in sys.argv:
        asr = json.loads(Path(sys.argv[sys.argv.index("--asr") + 1]).read_text(encoding="utf-8"))
    samples = json.loads((folder.parent.parent / "samples.json").read_text(encoding="utf-8")) if (folder.parent.parent / "samples.json").exists() else []
    says = {s["id"]: s["say"] for s in samples}
    rows, per_voice = [], defaultdict(list)
    for wav in sorted(folder.glob("*.wav")):
        voice, _, line = wav.stem.partition("__")
        x, sr = sf.read(str(wav), dtype="float32")
        if x.ndim > 1:
            x = x.mean(axis=1)
        f0 = f0_track(x, sr)
        dur = len(x) / sr
        chars = len(says.get(line, "").replace(" ", "")) or 1
        r = {
            "voice": voice, "line": line, "dur": dur,
            "f0": float(np.median(f0)) if len(f0) else 0.0,
            "f0_spread": float(np.percentile(f0, 90) - np.percentile(f0, 10)) if len(f0) else 0.0,
            "cps": chars / dur, "gap": longest_gap(x, sr), "clip": float(np.mean(np.abs(x) > 0.985) * 100),
            "cer": asr.get(wav.stem, {}).get("cer"),
        }
        rows.append(r)
        per_voice[voice].append(r)

    print("| voice | line | dur s | median F0 Hz | F0 spread Hz | chars/s | longest inner gap s | clip % | Whisper CER |")
    print("|---|---|---|---|---|---|---|---|---|")
    for r in rows:
        cer = "" if r["cer"] is None else f"{r['cer']:.3f}"
        print(f"| {r['voice']} | {r['line']} | {r['dur']:.2f} | {r['f0']:.0f} | {r['f0_spread']:.0f} | {r['cps']:.1f} | {r['gap']:.2f} | {r['clip']:.2f} | {cer} |")
    print("\n| voice | mean CER | worst CER | median F0 Hz | F0 across lines (max-min) | mean chars/s | worst inner gap s |")
    print("|---|---|---|---|---|---|---|")
    for v, rs in per_voice.items():
        cers = [r["cer"] for r in rs if r["cer"] is not None]
        f0s = [r["f0"] for r in rs if r["f0"]]
        print(f"| {v} | {np.mean(cers):.3f} | {max(cers):.3f} | {np.median(f0s):.0f} | {max(f0s) - min(f0s):.0f} | {np.mean([r['cps'] for r in rs]):.1f} | {max(r['gap'] for r in rs):.2f} |" if cers else
              f"| {v} | - | - | {np.median(f0s):.0f} | {max(f0s) - min(f0s):.0f} | {np.mean([r['cps'] for r in rs]):.1f} | {max(r['gap'] for r in rs):.2f} |")
    return 0


if __name__ == "__main__":
    sys.exit(main())
