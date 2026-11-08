# QA report — AMWAG Printer Setup, Egyptian Arabic narrated tutorial (v1)

File: `AMWAG-Printer-Setup-Tutorial-EG-v1.mp4` — 5:16 (316.4 s), 1920×1080, 30 fps, H.264 + AAC.
This report separates what was **measured or checked** from what still **needs a human listener**.

## 1. Measured automatically (`narration/qa.mjs`, `out/qa.json`)

| Check | Result |
|---|---|
| Rendered duration vs. timeline | 316.40 s vs 316.35 s (AAC padding) |
| Loudness / true peak | −16.2 LUFS integrated, −1.5 dBTP |
| Near-silence over 2 s in the render | none |
| Speech intelligibility (Whisper large-v3-turbo, character error rate vs. the spoken text) | mean **2.4 %** over 40 lines; worst 7.6 % (`steps.2`: Whisper writes "USB" in Latin for «يو إس بي»), 6.8 % (`ready.3`: spelling «طبعة» for «طابعة»), 6.2 % (`cases.6`: «يندس» for «ويندوز») |
| Subtitles | 116 cues, 0 overlaps, none shorter than 0.8 s, longest 54 characters, cue text identical to the narration script |
| Narration cut by a scene change | 0 lines |
| Silence between lines | longest 1.0 s (before the recap); speech fills 93.5 % of the film |
| On-screen action anchors | 42, all timed from Whisper word timestamps of the generated speech (0 estimated) |
| Action order (23 pairs, e.g. dialog appears before Yes is clicked, each ticket check after the ticket feeds out) | all in order |
| HyperFrames check (lint, runtime, layout, contrast) | passed, 0 errors |

## 2. Defects found and fixed during production

| Problem | How it was found | Fix |
|---|---|---|
| Voice copied 0.8–1.3 s pauses from its reference clip at every comma | silence detection on samples | pauses inside a line tightened to ≤ 0.45 s (`max_gap`) |
| `intro.1` dropped «أهلًا بيك» | Whisper on the first 0.8 s | retake, seed pinned in `script.json` |
| `cases.4` skipped «اللي على الطابعة», blurred «الملصق» | Whisper CER 21 % | retake (2.3 %), seed pinned |
| `intro.3` blurred «يكمّل» | Whisper CER 11 % | retake (0 %), seed pinned |
| `ready.2` «بيرجع يقرا» merged into one sound in all 5 takes | Whisper | reworded to «بيراجع كل الإعدادات دي تاني» (3.7 %) |
| «Yes» read as «يقس» | 5 spellings tested with Whisper | spoken as «يِسْ» (heard «يس») |
| Closing title before the developer's name mumbled | Whisper | line replaced at your request with an IT-help line (3.4 %) |
| Whisper word times 1.5× too long (24 kHz audio) | timings past line ends | resample to 16 kHz before Whisper |
| 98 overlapping subtitles; two actions out of order | `qa.mjs` | subtitle timing pass rewritten; fixed by correct word times |
| Printed ticket overlapped the step caption; spotlight dimming flagged as low contrast | HyperFrames check | compact caption from the moment printing starts; app-window replica excluded from contrast audit (its colours are the app's own AA theme) |
| `run.sh` stopped silently before rendering | missing output file | `ls` under `pipefail` guarded |

## 3. Checked against the application (not invented)

Every instruction in the script traces to the app source or its docs:
- Six steps, their names and statuses — `Localization/Strings.Steps.cs`; step list on the right in Arabic.
- Finds USB and network printers; reuses or installs the approved driver after checking size, SHA-256 and signature — `README.md`, `docs/ARCHITECTURE.md`.
- Windows asks permission once, only when a machine-wide change is needed; no prompt when nothing changes; employees without admin rights need IT — `docs/DEPLOYMENT.md` §6, §8.
- Ticket defaults: portrait, single-sided, 100 % scale, multipurpose tray; ticket 3.27 × 6.70 in (83.06 × 170.18 mm) — `README.md`, `config/amwag-printer-config.json`.
- Closing the window during setup stops after the running step — `docs/TROUBLESHOOTING.md`.
- Test ticket: load paper in the multipurpose tray, Print, the four checks, Printed correctly / There is a problem, the guidance list and printer problems (out of paper, cover open) — `Localization/Strings.TestPrint.cs`.
- Check ticket software only reads and compares, changes nothing — `Strings.TicketCheck.cs`, `docs/TICKET-SOFTWARE-INTEGRATION.md`.
- Questions: choose the ticket printer, network address from the printer's status page, confirm the model only if the label matches — `Strings.Interface.cs`.
- Declined permission → Try again; not ready → Repair or Try again; Copy report for IT — `Strings.Results.cs`, `UI/Views/ResultView.cs` (button availability).

Visual review: 26 frames inspected at the moment of each key action (snapshots before render) and 4 frames from the final MP4; captions render right-to-left with correct shaping, clicks land on the spoken word.

## 4. Not verified — needs a human

- **How natural the voice sounds** (warmth, rhythm, emphasis). Whisper confirms the words are clear, not that they sound good. Please listen to the whole video once, ideally with a branch employee.
- **Pronunciation of loanwords** «يو إس بي», «أدمن», «الآي تي», «سِتاب»: transcribed correctly, but whether they sound natural is subjective.
- **Comprehension**: not tested with a real employee who has never used the app.
- **Windows permission prompts** are illustrations. If `AMWAG Printer Setup.exe` is not code-signed, the real prompt shows "Publisher: Unknown" and looks slightly different.
- **Voice rights**: the Habibi Egyptian model is Apache-2.0. The voice timbre comes from the reference clip shipped in the Habibi repository (an ElevenLabs voice-library sample). That is fine for internal training; check rights before publishing externally.
- The background music is the custom ElevenLabs Music track generated on your Higgsfield free plan (same caveat for external publishing).

## 5. Voice selection (objective comparison, `voice-samples/voice-comparison.md`)

| Voice | Mean CER | Median pitch | Licence of weights | Chosen |
|---|---|---|---|---|
| Habibi, Egyptian specialised model | **2.1 %** | 117 Hz (male) | Apache-2.0 | ✔ |
| Habibi, unified model, EGY dialect | 2.8 % | 100 Hz | CC-BY-NC-SA | |
| VoiceTuT Youssef | 4.4 % | 120 Hz | non-commercial (OmniVoice base) | |
| VoiceTuT Mohammed | 6.7 % | 110 Hz | non-commercial | |
| VoiceTuT Karim | 8.0 % | 130 Hz | non-commercial | |

Samples (4 script lines per voice) are in `voice-samples/` for you to listen to. They were made before pause tightening.
