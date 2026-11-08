# AMWAG Printer Setup — employee training video

**Narrated Egyptian Arabic tutorial (5:16):** `deliverables/AMWAG-Printer-Setup-Tutorial-EG-v1.mp4` with its `.srt`,
script, storyboard, voiceover and `QA-REPORT.md`. Built by the narration pipeline in `narration/` (see
`narration/README.md`) into the `composition-eg/` project; regenerate with `bash narration/run.sh`.

The short silent promo versions are below.

Two 60-second, 1920×1080 videos that walk a branch employee through the app:

| File | Language |
|---|---|
| `AMWAG-Printer-Setup-Training-EN.mp4` | English |
| `AMWAG-Printer-Setup-Training-AR.mp4` | Arabic (right-to-left, the app shown in Arabic) |

What the video teaches, in order: open `AMWAG Printer Setup.exe` → wait while it finds the printer and installs the
driver → choose **Yes** when Windows asks → wait for **Ready to print tickets** → **Print Test Ticket** and check the
printed ticket → optionally **Check ticket software** → if something is wrong, **Repair** or **Copy report** for IT.
The end card credits the developer, Eng. Abdelhmeed Elshorbagy (name as in the app footer).

All screens are recreated from the app's own source (`src/Amwag.PrinterSetup.App/UI`, `Localization/Strings*.cs`,
`UI/Theme.cs`) and `config/amwag-printer-config.json` (ticket 3.27 × 6.70 in, P-4030DN v4 KX driver). The Windows
permission prompt is an illustration, not a capture.

## Changing the video

The video is a [HyperFrames](https://hyperframes.heygen.com) project (HTML + GSAP rendered to MP4).

- `composition/` — the English project and **the only place to edit scenes**:
  `compositions/intro.html`, `open.html` (step 1), `app.html` (steps 2–6), `outro.html`. Each text has both languages
  in `data-en` / `data-ar` attributes.
- `build-index.cjs` — scene timing and the sound cue list. It writes `composition/index.html` and regenerates
  `composition-ar/` (do not edit that folder).
- `brag-plan.md` — the storyboard.

```bash
node build-index.cjs
```

```bash
cd composition && npx hyperframes check && npx hyperframes render --quality delivery --output ../AMWAG-Printer-Setup-Training-EN.mp4
```

```bash
cd composition-ar && npx hyperframes check && npx hyperframes render --quality delivery --output ../AMWAG-Printer-Setup-Training-AR.mp4
```

Fonts: Segoe UI is read from Windows (`local()`), so render on a Windows PC; a cloud render would substitute another
font. Music: a custom instrumental generated for this video with ElevenLabs Music v2.5 through Higgsfield
(`assets/music/amwag-custom-bed.mp3`). Sound effects are CC0 (Kenney.nl).
