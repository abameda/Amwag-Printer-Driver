"""Generate narration audio, one WAV per script line, with a selectable Egyptian Arabic voice.

Runs inside the voice's own environment (see setup-tts.sh); run.sh picks the right Python for you:
    python tts.py --config tts-config.json --voice habibi-egy --script script.json --out out/voice
    python tts.py --config tts-config.json --voice voicetut-karim --lines samples.json --out out/samples --prefix voicetut-karim

Lines are cached: a line is regenerated only when its spoken text or the voice settings change (out/<dir>/manifest.json).
Every file is trimmed of leading and trailing silence so the timeline can place speech exactly.
"""
import argparse
import hashlib
import json
import os
import sys
import time
from importlib.resources import files
from pathlib import Path

import numpy as np
import soundfile as sf
import torch

TRIM_DB = -45.0     # level below which audio at the edges counts as silence
EDGE_PAD = 0.06     # seconds of room kept before the first and after the last sound


def trim(audio: np.ndarray, sr: int) -> np.ndarray:
    frame = int(sr * 0.01)
    if len(audio) < frame * 3:
        return audio
    n = len(audio) // frame
    rms = np.sqrt(np.mean(audio[: n * frame].reshape(n, frame) ** 2, axis=1) + 1e-12)
    db = 20 * np.log10(rms + 1e-12)
    loud = np.where(db > TRIM_DB)[0]
    if len(loud) == 0:
        return audio
    pad = int(EDGE_PAD * sr)
    start = max(0, loud[0] * frame - pad)
    end = min(len(audio), (loud[-1] + 1) * frame + pad)
    return audio[start:end]


def tighten(audio: np.ndarray, sr: int, max_gap: float, db: float = -40.0) -> np.ndarray:
    """Shorten pauses inside a line to at most max_gap seconds (Habibi copies the long pauses of its reference clip).

    Each long silence keeps max_gap/2 at either side, joined with a 20 ms crossfade, so breaths and word endings stay.
    """
    if not max_gap:
        return audio
    frame = int(sr * 0.01)
    n = len(audio) // frame
    if n < 3:
        return audio
    rms = 20 * np.log10(np.sqrt(np.mean(audio[: n * frame].reshape(n, frame) ** 2, axis=1)) + 1e-9)
    quiet = rms < db
    keep = int(max_gap / 2 * sr)
    fade = int(0.02 * sr)
    out, cursor, i = [], 0, 0
    while i < n:
        if not quiet[i]:
            i += 1
            continue
        j = i
        while j < n and quiet[j]:
            j += 1
        start, end = i * frame, j * frame
        if i > 0 and j < n and (end - start) > max_gap * sr:
            out.append(audio[cursor:start + keep])
            tail = audio[end - keep:end].copy()
            ramp = np.linspace(0.0, 1.0, fade, dtype=np.float32)
            joined = out[-1][-fade:] * (1 - ramp) + tail[:fade] * ramp
            out[-1] = np.concatenate([out[-1][:-fade], joined])
            cursor = end - keep + fade
        i = j
    out.append(audio[cursor:])
    return np.concatenate(out).astype(np.float32)


class Habibi:
    """Habibi-TTS (F5-TTS). Zero-shot: the voice comes from a short reference clip and its transcript."""

    def __init__(self, v: dict):
        from f5_tts.infer.utils_infer import load_model, load_vocoder, preprocess_ref_audio_text
        from cached_path import cached_path
        from hydra.utils import get_class
        from omegaconf import OmegaConf
        from habibi_tts.model.utils import dialect_id_map

        self.v = v
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        cfg = OmegaConf.load(str(files("f5_tts").joinpath("configs/F5TTS_v1_Base.yaml")))
        model, dialect = v.get("model", "Specialized"), v.get("dialect", "EGY")
        if model == "Unified":
            ckpt = cached_path("hf://SWivid/Habibi-TTS/Unified/model_200000.safetensors")
            vocab = cached_path("hf://SWivid/Habibi-TTS/Unified/vocab.txt")
            self.dialect_id = dialect_id_map[dialect]
        else:
            step = 200000 if dialect in ("MSA", "SAU") else 100000
            ckpt = cached_path(f"hf://SWivid/Habibi-TTS/Specialized/{dialect}/model_{step}.safetensors")
            vocab = cached_path(f"hf://SWivid/Habibi-TTS/Specialized/{dialect}/vocab.txt")
            self.dialect_id = None
        self.mel = cfg.model.mel_spec.mel_spec_type
        self.model = load_model(get_class(f"f5_tts.model.{cfg.model.backbone}"), cfg.model.arch, str(ckpt),
                                mel_spec_type=self.mel, vocab_file=str(vocab), device=self.device)
        self.vocoder = load_vocoder(vocoder_name=self.mel, is_local=False, device=self.device)
        ref = v["ref_audio"].replace("{habibi_assets}", str(files("habibi_tts").joinpath("assets")))
        self.ref_audio, self.ref_text = preprocess_ref_audio_text(ref, v["ref_text"])

    def __call__(self, text: str, seed: int):
        from habibi_tts.infer.utils_infer import infer_process

        torch.manual_seed(seed)
        np.random.seed(seed)
        wave, sr, _ = infer_process(
            self.ref_audio, self.ref_text, text, self.model, self.vocoder, mel_spec_type=self.mel,
            nfe_step=self.v.get("nfe_step", 32), cfg_strength=self.v.get("cfg_strength", 2.0),
            sway_sampling_coef=self.v.get("sway_sampling_coef", -1.0), speed=self.v.get("speed", 1.0),
            cross_fade_duration=self.v.get("cross_fade_duration", 0.15), device=self.device, dialect_id=self.dialect_id,
        )
        return np.asarray(wave, dtype=np.float32), sr


class VoiceTut:
    """VoiceTuT-TTS (Egyptian fine-tune of OmniVoice) with a built-in speaker."""

    def __init__(self, v: dict):
        from voicetut_tts import VoiceTutTTS

        self.v = v
        self.tts = VoiceTutTTS.from_pretrained(v.get("checkpoint", "mohammedaly22/VoiceTut-TTS"))

    def __call__(self, text: str, seed: int):
        torch.manual_seed(seed)
        np.random.seed(seed)
        tmp = Path(os.environ.get("TEMP", ".")) / f"voicetut_{os.getpid()}.wav"
        self.tts.synthesize(text, speaker=self.v["speaker"], output=str(tmp), num_step=self.v.get("num_step", 32),
                            guidance_scale=self.v.get("guidance_scale", 2.0), speed=self.v.get("speed", 1.0))
        audio, sr = sf.read(str(tmp), dtype="float32")
        tmp.unlink(missing_ok=True)
        return audio, sr


ENGINES = {"habibi": Habibi, "voicetut": VoiceTut}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True)
    ap.add_argument("--voice", required=True)
    ap.add_argument("--script", help="script.json: speaks every line")
    ap.add_argument("--lines", help="a JSON list of {id, say} to speak instead (voice samples)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--prefix", default="", help="file name prefix, e.g. the voice name for sample files")
    ap.add_argument("--ids", default="", help="comma-separated line ids to (re)generate; default all")
    args = ap.parse_args()

    cfg = json.loads(Path(args.config).read_text(encoding="utf-8"))
    v = cfg["voices"][args.voice]
    if args.script:
        script = json.loads(Path(args.script).read_text(encoding="utf-8"))
        lines = [{"id": l["id"], "say": l.get("say") or l["text"], **({"seed": l["seed"]} if "seed" in l else {})}
                 for s in script["scenes"] for l in s["lines"]]
    else:
        lines = json.loads(Path(args.lines).read_text(encoding="utf-8"))
    only = {i for i in args.ids.split(",") if i}
    if only:
        lines = [l for l in lines if l["id"] in only]

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    manifest_path = out / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else {}
    settings = json.dumps(v, sort_keys=True, ensure_ascii=False)

    engine = None
    for line in lines:
        name = f"{args.prefix}__{line['id']}" if args.prefix else line["id"]
        wav = out / f"{name}.wav"
        pinned = f"\nseed={line['seed']}" if "seed" in line else ""
        key = hashlib.sha256((settings + "\n" + line["say"] + pinned).encode("utf-8")).hexdigest()[:16]
        if wav.exists() and manifest.get(name, {}).get("hash") == key:
            continue
        if engine is None:
            t0 = time.time()
            engine = ENGINES[v["engine"]](v)
            print(f"loaded {args.voice} in {time.time() - t0:.1f}s", flush=True)
        # A line can pin its own seed in script.json (a clearer take chosen with asr.py); otherwise derive one from its id.
        seed = int(line["seed"]) if "seed" in line else int(v.get("seed", 7)) + sum(map(ord, line["id"])) % 1000
        t0 = time.time()
        audio, sr = engine(line["say"], seed)
        gen = time.time() - t0
        if audio.ndim > 1:
            audio = audio.mean(axis=1)
        audio = trim(audio, sr)
        audio = tighten(audio, sr, float(v.get("max_gap", 0)))
        peak = float(np.max(np.abs(audio))) if len(audio) else 0.0
        if peak > 0.98:
            audio = audio * (0.98 / peak)
        sf.write(str(wav), audio, sr, subtype="PCM_16")
        dur = len(audio) / sr
        manifest[name] = {"hash": key, "duration": round(dur, 3), "sr": sr, "rtf": round(gen / max(dur, 1e-3), 3), "voice": args.voice}
        manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=1), encoding="utf-8")
        print(f"{name}\t{dur:.2f}s\tRTF {gen / max(dur, 1e-3):.2f}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
