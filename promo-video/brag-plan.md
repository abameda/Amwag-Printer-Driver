# Video Plan: AMWAG Printer Setup — employee walkthrough

## What is this app?
An internal AMWAG Travel tool: a branch employee opens one program and it finds the TA P-4030DN / P-4531DN / P-5032DN
ticket printer, installs the approved driver, creates the AMWAG Ticket paper size (3.27 × 6.70 in), checks every
setting and confirms "Ready to print tickets".

## The angle
A calm, confident how-to that doubles as a promo: the real app screens working on their own, with the employee doing
only three things — open it, choose Yes, print a test ticket. Every screen, string and value comes from the app source.

## Hook (0–5 s)
The AMWAG wave draws itself, then "New ticket printer at your branch?" → "AMWAG Printer Setup sets up the ticket
printer for your branch." (the app's own subtitle).

## User flow worth showing
Double-click the exe → splash → steps tick by themselves → Windows permission → Yes → "Ready to print tickets" →
Print Test Ticket → ticket slides out and is checked (corners, 50 mm bars, upright, MP tray) → optional ticket-software
check → Repair / Copy report if something is wrong.

## Tone
- Preset: app-store, with polished restraint
- Creative direction: friendly training film from AMWAG IT
- Interpretation: clear step captions held long enough to read, one product moment per step, smooth UI motion, no jokes.

## Format: landscape — 1920x1080, 30 fps
## Duration: 57 s (a tutorial; the brag 15–25 s cap does not fit six steps)
## Languages: English (`index.html`) and Arabic RTL (`index-ar.html`), same scenes, same timing.

## Visual identity (from src/Amwag.PrinterSetup.App/UI/Theme.cs and branding/)
- Ground: navy #0F2A4A (raised #22497A), secondary text #C9D5E6
- Action: amber #F5B642 (fill only, ink text) — logo amber #F8B549, logo blue #34AAE1
- Success #2E7D32, surface #F2F4F5, border #DCE1E5, text #15191D / #56616B
- Font: Segoe UI / Segoe UI Semibold (the app's own font, also covers Arabic)
- Strongest visual: the six-step list ticking green, and the printed test ticket

## Storyboard (root seconds)
1. Hook — 0.0–5.2 — wave mark draws; two lines (above). Soft whoosh into scene 2.
2. Open — 5.2–10.6 — Step 1 "Open AMWAG Printer Setup": file window from dist/, cursor double-clicks
   `AMWAG Printer Setup.exe` (2 clicks), splash "Printer Setup" loads to 100 %, zooms into the main window.
3. App — 10.6–49.6 — one continuous window, captions change:
   - Step 2 "Just wait — it works by itself": Detecting printer ✓, printer card fills row by row, driver check ✓,
     installing driver with "Checking file integrity and digital signature…".
   - Step 3 "When Windows asks, choose Yes": the app's blue notice (punch-in), then the Windows permission dialog,
     cursor clicks Yes.
   - Step 4 "Wait for 'Ready to print tickets'": install ✓, ticket size ✓ (size row highlighted), verification ✓,
     Ready ✓ → result screen with green check (the payoff, one bell).
   - Step 5 "Print a test ticket": click Print Test Ticket → load-paper screen → Print → sending/printing →
     the ticket slides out beside the window; the four checks tick one by one while the matching mark on the
     ticket lights up → click "Printed correctly".
   - Step 6 "Optional: check your ticket software": waiting for a print job → table rows "Matches" one by one.
4. Outro — 49.6–57.0 — "Something not right?" Repair = fixes the printer settings; Copy report = send it to IT.
   End card: AMWAG logo, "AMWAG Printer Setup", recap "Open it. Choose Yes. Print a test ticket." (held ≥ 3 s).

## Audio direction
- Role: warm bed under clear UI sounds
- Music: happy-beats-business-moves-vol-11 (114.8 BPM, 87.6 s), fade in 0.5 s, fade out over the last 2.5 s, ~0.32 volume
- SFX (CC0, Kenney): mouse clicks on every real click; soft drop "pops" on each green tick and table row (3 takes
  rotated); paper slide when the ticket feeds out and on two scene changes; one bell on "Ready to print tickets".
- Restraint: no sound without a visible cause; no sound on caption changes.

## Share copy (draft)
New ticket printer at your branch? Open AMWAG Printer Setup, choose Yes when Windows asks, and print a test ticket —
it does the rest.
