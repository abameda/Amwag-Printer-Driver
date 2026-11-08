#!/usr/bin/env bash
# Regenerate the Egyptian Arabic tutorial from narration/script.json.
#   bash run.sh            # voice (only changed lines) -> Whisper timings -> timeline -> check -> render -> deliverables
#   bash run.sh --no-render
# One-time setup: bash setup-tts.sh   (and ffmpeg + Node.js 22 on PATH)
set -euo pipefail
cd "$(dirname "$0")"
export PYTHONUTF8=1 HF_HUB_DISABLE_SYMLINKS_WARNING=1

py() { node -e "const c=require('./tts-config.json');const p=c.python['$1'];console.log(p.replace(/%([A-Z]+)%/g,(_,v)=>process.env[v]))"; }
VOICE=$(node -e "console.log(require('./tts-config.json').voice)")
ENGINE=$(node -e "const c=require('./tts-config.json');console.log(c.voices[c.voice].engine)")

echo "== 1/5 voice ($VOICE)"
"$(py "$ENGINE")" tts.py --config tts-config.json --voice "$VOICE" --script script.json --out out/voice

echo "== 2/5 Whisper word timings and intelligibility"
"$(py asr)" asr.py --audio-dir out/voice --script script.json --out out/asr.json --field say > out/asr.log
(awk -F'\t' '{print $1, $2}' out/asr.log | sort -t' ' -k3 -r | head -5) || true

echo "== 3/5 timeline, captions, audio mix"
node build.mjs

echo "== 4/5 HyperFrames check"
(cd ../composition-eg && npx hyperframes check)

[ "${1:-}" = "--no-render" ] && exit 0
echo "== 5/5 render"
mkdir -p ../deliverables
N=$( (ls ../deliverables/AMWAG-Printer-Setup-Tutorial-EG-v*.mp4 2>/dev/null || true) | wc -l)
OUTV="../deliverables/AMWAG-Printer-Setup-Tutorial-EG-v$((N + 1)).mp4"
(cd ../composition-eg && npx hyperframes render --quality delivery --output "../deliverables/tmp-render.mp4")
ffmpeg -v error -y -i ../deliverables/tmp-render.mp4 -c:v copy -af "loudnorm=I=-16:TP=-1.5:LRA=11" -c:a aac -b:a 192k -ar 48000 -movflags +faststart "$OUTV"
rm -f ../deliverables/tmp-render.mp4
cp out/subtitles.srt "${OUTV%.mp4}.srt"
ffmpeg -v error -y -i ../composition-eg/assets/voice/voiceover.wav -c:a pcm_s16le ../deliverables/voiceover-ar-EG.wav
echo "Done: $OUTV"
