#!/usr/bin/env bash
# One-time setup of the local voice tools used by the narration pipeline (Windows, Git Bash, NVIDIA GPU).
# Creates three isolated Python environments under %LOCALAPPDATA%\amwag-video-tts:
#   habibi   - Habibi-TTS (F5-TTS based, Egyptian "EGY" specialised model, Apache-2.0)
#   voicetut - VoiceTuT-TTS (Egyptian fine-tune of OmniVoice, built-in voices)
#   asr      - Whisper (transformers) for intelligibility checks and word timings of the generated speech
# PyTorch 2.8 + CUDA 12.8 is required for RTX 50-series (Blackwell) GPUs.
set -euo pipefail
ROOT="${AMWAG_TTS_HOME:-$USERPROFILE/.amwag-video-tts}"
TORCH_INDEX="https://download.pytorch.org/whl/cu128"
mkdir -p "$ROOT"

make_env() {
  local name="$1"
  if [ ! -x "$ROOT/$name/Scripts/python.exe" ]; then
    uv venv --python 3.11 "$ROOT/$name"
  fi
  uv pip install --python "$ROOT/$name/Scripts/python.exe" "torch==2.8.0" "torchaudio==2.8.0" --index-url "$TORCH_INDEX"
}

case "${1:-all}" in
  habibi|all)
    make_env habibi
    uv pip install --python "$ROOT/habibi/Scripts/python.exe" "habibi-tts==0.1.1" "pyarrow<21" ;;&
  voicetut|all)
    make_env voicetut
    uv pip install --python "$ROOT/voicetut/Scripts/python.exe" "git+https://github.com/k2-fsa/OmniVoice.git" "voicetut-tts @ git+https://github.com/MohammedAly22/VoiceTuT-TTS.git" ;;&
  asr|all)
    make_env asr
    uv pip install --python "$ROOT/asr/Scripts/python.exe" "transformers>=4.46,<5" accelerate soundfile numpy ;;
esac
echo "Voice tools ready in $ROOT"
