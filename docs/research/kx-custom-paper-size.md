# Ticket paper size on the KX drivers: investigation

Date: 2026-11-04. Trigger: the first branch run (DESKTOP-MN53VQV, P-4030DN on USB026) was blocked with
`DriverDoesNotListForm`. The queue used the **KX v3** driver `P-4030DN KX` 8.5.2405.0, which is accepted but not
preferred. No code was changed for this investigation.

Evidence labels: **[OBS]** observed (branch report, or files inspected here), **[DOC]** vendor or Microsoft
documentation, **[INF]** inference, **[UNVERIFIED]** must be measured.

## 1. Three ways a size can reach the driver

| Mechanism | Where it lives | Who can create it | Documented API |
|---|---|---|---|
| Windows form (`AddForm`, the forms database) | Per computer (print server), all users | Administrators | Yes: `AddForm`/`SetForm` |
| KX driver custom page size (Basic tab → *Page Sizes* → New, at most 20) | Inside the driver's own settings. The KX guide separates "client custom page sizes" (*"saved on a client computer"*) from server ones. Whether a client keeps them per user or per computer is **not documented** | Anyone who opens Printing Preferences | **None.** The driver's dialog is the only way; the storage format is private |
| A size carried in the DEVMODE or PrintTicket itself: `dmPaperSize = DMPAPER_USER` with `dmPaperWidth`/`dmPaperLength`, or Print Schema `psk:CustomMediaSize` | In each DEVMODE: queue defaults, per-user preferences, or each job | Whoever writes the defaults (the tool already does this) | Yes: DEVMODE and Print Schema (PrintTicket) |

## 2. KX v3 (8.x): what we know

* **[OBS]** It does not list the `AMWAG Ticket` form, although the form exists with the exact size and a full
  imageable area, and the ticket (831 × 1702 tenths of a mm) is inside the driver's custom range (DC_MINEXTENT /
  DC_MAXEXTENT 700..2160 × 1480..3560).
  So **creating a Windows form does not make it available in KX v3**.
* **[OBS]** It leaves `dmFormName` empty in the DEVMODEs it returns (`'' (paper 9)` = A4). The fix of 2026-11-04
  accepts that.
* **[OBS]** Someone on that PC created a KX custom page size of 3.27 × 6.70 in. The employee's own preferences
  selected it (paper 541), and its PrintTicket was `ns0000:CustomPaper000` at exactly 83 058 × 170 180 µm. The queue
  defaults were still A4.
  **[INF]** It was made in the employee's Printing Preferences, which is consistent with per-user storage but does not
  prove it.
* **[DOC]** KX Driver 8.2 user guide (PA6000x, 2022-07), section 6 "Page Sizes":
  * Custom sizes are made only in the *Page Sizes* dialog.
  * *"Custom sizes are not available when PDF is selected as the PDL."*
  * Printing with them: load the paper into the multipurpose tray and select the custom name in *Print size*.
* **[DOC]** Same guide, section 11 "Enabling client custom paper size": *"Client custom paper size refers to page
  size settings saved on a client computer."*
* **[DOC]** Profiles can be imported and exported as `.kxp` files, but only from the driver's dialog.
  * *"Settings made in Device Settings cannot be saved to a profile."*
  * The guide does not say whether a profile carries custom page sizes.
  * `.kxp` is a private binary format (`KXP\0…` header, inspected here).
* **[DOC]** No KX v3 API, command-line tool or documented setting creates a custom page size.
  * The KYOCERA Client Tool and the Product Library's *Custom Install* set device or installation defaults; neither is
    documented to create driver page sizes.
  * The only ways to automate it would be the undocumented registry or private DEVMODE data, which are ruled out.
* **[UNVERIFIED]** Whether KX v3 honours `DMPAPER_USER` with width and length.
  * It reports a custom range, which suggests GDI custom sizes, but that is not proof.
  * Measure it read-only with `DocumentProperties(DM_IN_BUFFER|DM_OUT_BUFFER)` (nothing is written), then with
    `PTConvertDevModeToPrintTicket`.
* **[UNVERIFIED]** Whether its PrintTicket provider accepts `psk:CustomMediaSize`.
  * Measure it with `PTMergeAndValidatePrintTicket` / `PTConvertPrintTicketToDevMode`.
  * Then re-read the result: the HRESULT never proves the delta was applied (windows-platform-apis.md §PT).

**Conclusion for KX v3:** creating and naming a custom page size cannot be automated by any documented means. A size
carried in the DEVMODE (`DMPAPER_USER`) might work, but is unmeasured. Even if it works, ticket software that picks
paper by name would find no "AMWAG Ticket" in this driver.

## 3. KXv4 6.1.1603 (the preferred, WHQL, bundled driver): what we know

Inspected in `drivers/Kxv4_v6.1.1603_TA_RC7_WHQL.zip`, PCL6 x64 (`PrnDrv/PCL Driver/64bit/win81 and newer`):

* **[OBS]** All three models use GPD files: P-4030DN `utlp2100npcl6.gpd`, P-4531DN `p-4531dnpcl6.gpd`, P-5032DN
  `p-5032dnpcl6.gpd`.
* **[OBS]** Each GPD declares `*Feature: PaperSize` with `*Option: CUSTOMSIZE`, in `*MasterUnits` 1200 dpi:

  | Model | `*MinSize` | `*MaxSize` | Ticket 83.058 × 170.18 mm |
  |---|---|---|---|
  | P-4030DN | 3308 × 6993 = 70.0 × 148.0 mm | 10204 × 16818 = 216 × 356 mm | inside |
  | P-4531DN, P-5032DN | 3308 × 6993 = 70.0 × 148.0 mm | 10204 × 43228 = 216 × 915 mm | inside |

* **[OBS]** The custom size is printed as an exact PCL XL `CustomMediaSize` in tenths of a mm. The MP tray is the GPD
  input slot mapped to `Manual`.
* **[OBS]** The driver's JavaScript constraints (`kyv4-constraints.js`) add a `psk:CustomMediaSize` option ("User
  Defined Size", with width and height parameters) to the PrintCapabilities. So the standard Print Schema custom size
  is supported.
* **[DOC]** Microsoft, *Supporting user-defined paper sizes*: user-defined sizes are forms an administrator defines
  for the computer. A GPD printer supports them only when its PaperSize feature has a `CUSTOMSIZE` option with
  `*MinSize`/`*MaxSize`. The KXv4 GPDs have it.
* **[DOC]** Xerox documents the same for its Windows v4 drivers: a form added under the print server's Forms tab
  "is added to the paper size list" when it is within the device range.
* **[OBS, this PC]** Microsoft Print to PDF (a v4 GPD driver) lists the forms of its own GPD. It leaves out the forms
  another vendor's driver wrote (the Kyocera ECOSYS P6235cdn XPS driver's `ISOC5`, `Oficio II`, `16K` …). This PC
  has no administrator-defined form, so user-form listing has **not** been observed here.
* **[OBS, bench 2026-11-04, section 6]** KXv4 lists `AMWAG Ticket` once the form exists, keeps it with the MP tray
  through validation, and resolves a request by form name alone to it. All three models were measured.
  **Not yet verified:** printing it from the MP tray on real hardware.

**Conclusion for KXv4:** this is the documented route to full automation. The machine-wide form (which the tool
already creates) becomes a named paper in the driver, as measured on the bench. The tool can select it by name and
verify it, and ticket software can select "AMWAG Ticket" by name.

## 4. Proposed design (not implemented)

### Automatic path: put the ticket queue on the preferred KXv4 driver when the current driver cannot offer the ticket

**Implemented on 2026-11-05** (`listsWindowsForms` in the configuration, `DriverIgnoresTicketForm`, rollback with
`QueueDriverRestored`; tests in `DriverSwitchTests`), as described below. Three differences:

* **Every KX v3 queue moves:** a queue on a driver marked `listsWindowsForms: false` moves also when it has a custom page
  size of the ticket size (decided 2026-11-05). Its name differs from the form, so applications cannot select it by
  name, and it may exist for one Windows account only.

* **Saved state:** the save point is kept in memory for the elevated run. The previous defaults are restored in that
  run; they are not written to `%ProgramData%`.
* **USB device:** it keeps its KX v3 driver binding. Only the queue moves.


The planner already has `InstallDriver` and `ChangeQueueDriver`. Today a queue on an *accepted* driver is never
changed. Add one rule:

* **When:** the queue's accepted driver offers neither the form by name nor a paper of the ticket size, the preferred
  driver differs, and the preferred driver is approved and obtainable.
* **What:** plan `InstallDriver` (if needed) → `ChangeQueueDriver` to the preferred driver → `ConfigureGlobalDefaults`
  / `ConfigureUserDefaults`.
* **Otherwise:** a queue whose driver already offers the ticket (including a KX v3 queue with a matching custom page
  size, the fix of 2026-11-04) is left alone.

* **Before changing:**
  * Snapshot the queue's driver name, the global DEVMODE (level 8) and the current user's DEVMODE (level 9) to
    `%ProgramData%\AMWAG\PrinterSetup\Backups`.
  * Never remove the old driver package.
* **Verify:** the new driver lists the ticket paper, the validated defaults keep it, the read-back matches, and the
  PrintTicket gives 83 058 × 170 180 µm (± 1 mm) from the MP tray. Then the employee prints the test ticket.
* **Rollback:** if any check fails, restore the old driver name and the saved global DEVMODE, re-validate it with the
  old driver, and say so.
  * Uses documented `SetPrinter` level 2 / level 8 only.
  * Other Windows accounts' own preferences are only revalidated by the driver; the tool corrects them when those
    users run it (an existing limitation).
* **Diagnostics stays write-free.** The report gains the driver's full paper list (already logged since 2026-11-04),
  the PrintCapabilities media sizes and custom range, and which path was taken.
* **Tests:** a fake KX v3 profile (no forms, empty `dmFormName`) and a fake KXv4 profile (lists forms). Scenarios
  cover: switch and verify; switch, then rollback on a failed read-back; a v3 queue with a matching custom paper left
  alone; all three models.

Risks to accept explicitly:

* Ticket software tuned to the v3 driver may print differently on v4 (XPS → PCL XL pipeline). *Check ticket software*
  and the test ticket cover this.
* `P-xxxx KX` stays an accepted driver.

### Fallback: smallest guided flow (only if KXv4 cannot be used, or the bench test fails)

One card, at most once per Windows account:

1. "Create the ticket size once." A button opens the KX driver's own Printing Preferences for the ticket queue,
   through `DocumentPropertiesW` with `DM_IN_PROMPT`, a documented API, as the employee.
2. The card shows the exact clicks and values: *Basic → Page Sizes → New → Name `AMWAG Ticket` → 3.27 × 6.70 in
   (83.1 × 170.2 mm) → Save → OK → OK*.
3. When the dialog closes, the tool re-reads the driver's papers. It finds the size within 1 mm (implemented
   2026-11-04), then writes, validates and verifies the defaults.
4. If no matching paper appears, it shows the closest size it found and offers *Try again*.

The account scope must be measured: if KX v3 keeps custom sizes per user, every employee account needs step 1 once.

## 5. Measurements needed before implementing

| # | Where | What | Changes |
|---|---|---|---|
| 1 (**done**, section 6) | Development PC (administrator) | Install TA KXv4 6.1.1603 PCL6 from the bundled package. Add a queue *AMWAG KXv4 probe* (P-4030DN) on port `nul:`, plus the `AMWAG Ticket` form. Read DC_PAPERNAMES/DC_PAPERS/DC_PAPERSIZE, the PrintCapabilities, and `DocumentProperties` validation. Read the PrintTicket of DEVMODEs selecting the form. Then remove the queue, form and driver package. | Reversible bench change on this PC only |
| 2 | Branch PC (read-only) | Diagnostics on the KX v3 queue. Read the paper list. Validate, without writing, a DEVMODE with `DMPAPER_USER` 831 × 1702, and a PrintTicket with `psk:CustomMediaSize` 83 058 × 170 180. | None (needs a new read-only probe in Diagnostics) |
| 3 | Branch PC, second Windows account | Does the KX v3 custom page size (paper 541) appear for another account? | None |
| 4 | Real P-4030DN / P-4531DN / P-5032DN | Ticket printed from the MP tray with KXv4 and the form; measured against the stock | Physical test |

## 6. Bench results (development PC, 2026-11-04)

### Setup

* **PC:** Windows 11 Home 26H2, build 26300, x64, administrator via UAC.
* **Package:** `Kxv4_v6.1.1603_TA_RC7_WHQL.zip`. SHA-256 matched the manifest. The catalog `prnkycl1.cat` is valid,
  signed by *Microsoft Windows Hardware Compatibility Publisher*. Driver 6.1.1603.0, published as `oem51.inf`.
* **Probe printers:** `P-4030DN / P-4531DN / P-5032DN v4 KX (PCL6)`, first on `nul:`, then on Standard TCP/IP ports to
  192.0.2.10–12 (TEST-NET-1: nothing answers).
* **Tools:** a probe that uses the app's own `WindowsPrintSpooler` (DeviceCapabilities, DocumentProperties validation
  that stores nothing, PrintTicket conversion), and the published app in silent Diagnostics and Repair.

The results were the same for all three models.

### What the driver offers

| Measurement | Without the form | With the `AMWAG Ticket` user form (83 058 × 170 180 µm) |
|---|---|---|
| DC_PAPERNAMES / DC_PAPERS | 23 papers, no ticket | **24 papers: 183 `AMWAG Ticket` 830 × 1701** (DC_PAPERSIZE truncates; within 1 mm) |
| PrintCapabilities PageMediaSize | `psk:CustomMediaSize` "User Defined Size" | **`ns0000:UserForm182`, DisplayName `AMWAG Ticket`, 83 058 × 170 180 µm** |
| PrintCapabilities custom range | 70 019 × 148 018 .. 215 984 × 355 981 µm (P-4030DN); height up to 914 992 µm (P-4531DN, P-5032DN) | same |
| DC_MINEXTENT / DC_MAXEXTENT | 920 × 1480 .. 2970 × 4200 | 830 × 1480 .. 2970 × 4200 |

* DC_MINEXTENT / DC_MAXEXTENT on KXv4 are **only the bounding box of the listed papers**: 6¾ Envelope 92 mm wide, A6
  148 mm high, A3 297 × 420 mm.
* They are **not** the custom range. The ticket (83.1 mm) is narrower than 92 mm until the form exists.

### Validation

`DocumentProperties` with `DM_IN_BUFFER|DM_OUT_BUFFER`; nothing is stored. Then `PTConvertDevModeToPrintTicket`:

| Requested | Driver returned | PrintTicket |
|---|---|---|
| form `AMWAG Ticket`, paper 183, MP tray (bin 4), portrait | kept: `AMWAG Ticket` 183, source 4 | `ns0000:UserForm182` 83 058 × 170 180 µm, `psk:Manual`, portrait |
| `dmFormName = AMWAG Ticket` only (how name-selecting software asks) | `AMWAG Ticket` 183 | `ns0000:UserForm182` 83 058 × 170 180 µm |
| `DMPAPER_USER` 831 × 1702 (with or without the form) | kept: paper 256, 831 × 1702, size flags set | `psk:CustomMediaSize` 83 100 × 170 200 µm |
| `dmFormName = AMWAG Ticket` without the form | replaced by Letter | `psk:NorthAmericaLetter` |

### Writing the defaults

| Write | Result |
|---|---|
| App: `SetPrinter` level 8 on a handle opened with `PRINTER_ACCESS_ADMINISTER` (elevated) | **Error 5, access denied**, on all three printers. The app's silent Repair failed the same way (`ElevationRequired`). |
| `SetPrinter` level 8, handle opened with `PRINTER_ALL_ACCESS` (elevated) | OK; read back `AMWAG Ticket` 183, source 4, portrait, simplex, 100 % |
| `SetPrinter` level 2 (pDevMode only, no security change), `PRINTER_ALL_ACCESS` (elevated) | OK; same read-back |
| App: `SetPrinter` level 9 (per user), `PRINTER_ACCESS_USE`, **not elevated** | OK; the effective DEVMODE then gives `ns0000:UserForm182` from `psk:Manual` |

### Cleanup and state check

* **Removed:** the probe queues, ports and drivers, the `AMWAG Ticket` form, the driver package `oem51.inf` (checked as
  `prnkycl1.inf` 6.1.1603 from UTAX/TA first; the unrelated inbox Kyocera package `oem217.inf` was left), and
  `%ProgramData%\AMWAG` (logs copied to the bench folder first).
* **Identical to before:** drivers, printers, ports, driver packages and `%ProgramData%`.
* **Left by Windows:** the driver's own forms `216 x 340 mm` and `Oficio II`, which the KXv4 installation adds to the
  forms database (harmless).
* **Left in HKCU:** the per-user cache values `HKCU\Printers\DevModes2` and `ConvertUserDevModesCount` for the deleted
  probe printers. They are inert and undocumented, so they were not edited.

### What this means for the design

1. **Fully automatic configuration is possible with KXv4.** The tool already creates the machine-wide form and
   installs the driver silently; the driver then offers `AMWAG Ticket` by name to every user and every kind of
   application.
2. **App bug, independent of the KX question (fixed 2026-11-05).** Writing the queue defaults needs a handle opened
   with `PRINTER_ALL_ACCESS`; `PRINTER_ACCESS_ADMINISTER` alone is refused. `WindowsPrintSpooler.SetDevMode` now opens
   with `PRINTER_ALL_ACCESS` for level 8.
   * The queue-driver and queue-port changes already open with `PRINTER_ALL_ACCESS`.
   * Without this fix, Setup and Repair cannot set the printer-wide defaults on this Windows build. It may also
     affect other builds and drivers; that was not measured.
3. **App gap (fixed 2026-11-05).** The custom-size range check (`TicketSizeOutsideDriverRange`) used
   DC_MINEXTENT/DC_MAXEXTENT. On KXv4 that falsely put the ticket outside the range whenever the form did not exist
   yet. `DriverSizeRange` now takes the range from the PrintCapabilities `psk:CustomMediaSize` parameters, with the DC
   extents only as a fallback; the finding's `rangeSource` names the source.
4. **Verified with the fixed app (2026-11-05).** Second bench on the same PC, built from the same package. Setup:
   * The three probe printers on TEST-NET ports.
   * The `AMWAG Ticket` form not created beforehand.

   Results, the same for P-4030DN, P-4531DN and P-5032DN:
   * The app's silent Repair created the form and wrote the queue defaults: `AMWAG Ticket` paper 183, MP tray 4,
     portrait, single-sided, 100 %.
   * Repair and Diagnostics both exited with 1 (ready with warnings). The only warning was the unreachable test address.
   * Every verification check passed, including the effective defaults and the PrintTicket
     (`ns0000:UserForm182`, 83 058 × 170 180 µm).
   * Everything was removed afterwards. One port needed a second removal once its queue was fully deleted.
5. **Still to verify:**
   * Switching an existing KX v3 queue to KXv4 (`ChangeQueueDriver`) and rolling back. This needs the KX 8.x driver on
     the bench.
   * A real printed ticket from the MP tray on each model.
   * The KX v3 read-only probe at the branch (section 5, item 2), which only matters for the fallback.

## Sources

* Microsoft: [Supporting user-defined paper sizes](https://learn.microsoft.com/en-us/windows-hardware/drivers/print/supporting-user-defined-paper-sizes)
* Kyocera: [KX Driver 8.2 user guide (PA6000x)](https://www.kyoceradocumentsolutions.us/content/dam/download-center-americas-cf/us/documents/user-guides/PA6000xSeriesENUGR2022_07_KX_Driver_8_2_pdf.download.pdf), sections 5 (Profiles), 6 (Page Sizes), 11 (Enabling client custom paper size)
* Xerox: [Creating and Saving Custom Sizes for the Windows V4 Print Driver](https://www.xerox.com/technical_product_documentation/VersaLink_B7100_Series/VersaLink_B71XX_Updated/Tofo_EN/Xerox/0000832888.html)
* Bundled package: `drivers/Kxv4_v6.1.1603_TA_RC7_WHQL.zip` (GPDs, `kyv4-constraints.js`, `prnkycl1.inf`)
* Branch report of 2026-11-04 (session 3d0a31a7bdba), and `windows-platform-apis.md` / `windows-spooler-apis.md` in this folder
