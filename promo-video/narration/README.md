# Narrated tutorial pipeline (Egyptian Arabic)

Builds `../composition-eg/` (the narrated HyperFrames project) from the narration, so the picture follows the voice.

| File | Owns |
|---|---|
| `script.json` | The narration: one entry per spoken line. `text` = subtitles, `say` = what the voice reads when it must differ (English names in Arabic letters, numbers as words), `anchors` = the word whose spoken moment triggers an on-screen action, `pause` = silence after the line. |
| `scenes.json` | Scene metadata: learning objective, importance, visual focus, cursor and zoom per line, and the step captions shown beside the app window. |
| `tts-config.json` | Voice settings (engine, model, reference clip, steps, speed, pause tightening, seed) and the Python environments. |
| `beats.mjs` | Animation timing rules: every click, tick, zoom and sound as a function of the measured line times and anchor words. |
| `build.mjs` | Lays out the timeline, aligns anchors with Whisper's word times, writes captions, SRT, voiceover mix, ducked music bed, `index.html`, `timing.json`, `storyboard.md`, `script.md`. |
| `tts.py` / `asr.py` | Speech generation (cached per line) and Whisper transcription (intelligibility + word timings). |
| `qa.mjs` | Objective checks: intelligibility, subtitle sync and text, clipped speech, silences, action order, render loudness. |
| `voice_metrics.py` | Voice comparison: pitch, rate, inner pauses, clipping, error rate (`out/voice-comparison.md`). |

## Regenerate

One-time: `bash setup-tts.sh` (creates the Python environments in `%USERPROFILE%\.amwag-video-tts`, about 12 GB with
models; needs an NVIDIA GPU, uv, ffmpeg and Node.js 22).

```bash
bash run.sh
```

Edit a sentence in `script.json` and run it again: only changed lines are re-spoken, and timings, captions, sounds and
the SRT are recalculated. Each render is written to `../deliverables/AMWAG-Printer-Setup-Tutorial-EG-v<N>.mp4` next to
its `.srt`; earlier versions are kept. Then `node qa.mjs ../deliverables/<file>.mp4`.

To try another voice, set `"voice"` in `tts-config.json` to one of the configured voices (or add one) and run again.
