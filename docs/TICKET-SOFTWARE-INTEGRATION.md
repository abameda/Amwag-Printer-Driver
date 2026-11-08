# Ticket software and printer defaults

## What AMWAG Printer Setup configures

The tool sets the **default printing preferences of the ticket printer queue**:

| Setting | Value |
|---|---|
| Paper size | `AMWAG Ticket` (83.058 mm × 170.18 mm, 3.27 in × 6.70 in) |
| Orientation | Portrait |
| Two-sided | Off (single-sided) |
| Scaling | 100 % |
| Paper source | Multipurpose (MP) tray, when the model needs it for this size |

It writes the queue default for all users and, if the signed-in employee already has personal printing
preferences for that queue, corrects those too. Other printers and the Windows default printer are not changed.

## The limitation

Printer defaults are only *defaults*. Any application may send its own settings with each print job, and Windows
then uses the application's settings, not the queue's. Common cases:

* **Desktop ticketing applications** often store a paper size, orientation, margins or tray in their own page
  setup or report layout (Crystal Reports, RDLC, FastReport and similar keep a paper size per report).
* **Browser-based ticketing** (Chrome, Edge): the browser's print dialog keeps its own paper size, margins and
  scale, remembers the last choice per printer, and a page can request a size with CSS `@page`.
* **Java applications** choose a media size through their own print API.

No tool can force an application to use the printer defaults; AMWAG Printer Setup therefore reports this as an
informational finding (`ApplicationMayOverrideDefaults`) instead of claiming the ticket software is covered.

## Recommended integration

1. In the ticket software, select the printer queue configured by the tool (its name is shown on the success
   screen and in the report).
2. Prefer the software's "use printer default" / "default paper" option. If it asks for a paper size, choose
   **AMWAG Ticket** — the form appears in the driver's paper list after setup.
3. Set margins to none/minimum and scaling to 100 % ("actual size"); disable "fit to page".
4. Browser-based ticketing: give the ticket page `@page { size: 83.058mm 170.18mm; margin: 0; }`, and in the print
   dialog choose the AMWAG queue, paper size AMWAG Ticket, margins None, scale Default (100 %). Kiosk-mode printing
   reuses the last settings chosen in the dialog, so set them once per Windows account. Browser enterprise policies
   can also preset printing options; confirm the exact policy names for the deployed browser version.
5. If several employees use separate Windows accounts on the same PC, each account inherits the queue default;
   accounts that already changed their personal preferences are corrected when that employee runs **Repair**.

## Verifying what the ticket software really sends

* **Check ticket software** (on the result screen): start it, then print one real ticket from the ticket software.
  The tool watches the queue, reads the settings the job was submitted with (paper size, orientation, paper
  source, scale, duplex) from the spooler, and lists any setting that differs from the AMWAG Ticket defaults.
  Nothing is changed by this check.
* Compare a real ticket with the **test ticket**: the test ticket uses only the queue defaults, so if the test
  ticket is correct but the real ticket is not, the ticket software is overriding the defaults.
* The physical acceptance checklist (`docs/HARDWARE-ACCEPTANCE-CHECKLIST.md`) includes this comparison per branch
  model.
