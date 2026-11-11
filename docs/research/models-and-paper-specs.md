# Supported models: official pages, drivers and paper handling

Research date: 2026-11-03. Sources are official TA Triumph-Adler / UTAX pages and documents; Kyocera documents are
used only where marked. Page references are printed page labels of the operation guides (OG).

## Driver offering (identical for all three models)

| Package | Version | OS listed by TA | Official URL | Size (catalogue API) |
|---|---|---|---|---|
| KX Universal Print PCL/PostScript (v3) | 8.7.0917 | Windows 10, 11, Server 2016–2025 | https://www.triumph-adler.com/caas/v1/ta/media/102720/data/04e63668c3001927fbccf7c5c22a6cc3/Kx87_UPD_8.7.0917.zip | 203 308 603 bytes |
| KX XPS Universal Print Driver (KXv4) | 6.1.1603 | Windows 8, 10, 11, Server 2012–2022 | https://www.triumph-adler.com/caas/v1/ta/media/103622/data/02a1879a81ae6c73a9753c16e9ab05aa/Kxv4_v6.1.1603_TA_RC7_WHQL.zip | 155 412 996 bytes |

* The TA product pages for P-4030DN, P-4531DN and P-5032DN, the UTAX en-de/de-de pages and the UTAX Italy
  P-4531DN page all link the **same media id and hash path** (UTAX pages on host www.utax.com; identical ETags).
* The download centre search is client-side; its API is
  `https://www.triumph-adler.com/api/catalogue/product?languageCode=en&slug=<model>&siteKey=ta-com-catalogue`.
* The caas "hash path" segment and the ETag are not file hashes; SHA-256 must be computed after download.
* Older model-specific drivers (XPS, PCL5/PCL6 mini-drivers, KPDL, WinUSB) are listed for legacy Windows versions
  only and are not used.

## Paper handling (official ranges)

| Model | Kyocera equivalent (EU DoC) | MP tray custom range | Standard cassette custom range | Ticket 82.296 × 170.18 mm |
|---|---|---|---|---|
| P-4030DN | FS-2100DN (by list position, UTAX DoC 2017) | 70–216 × 148–356 mm (OG A-16) | 140–216 × 210–356 mm (OG A-14, A-16) | MP tray only |
| P-4531DN | ECOSYS P3045dn (explicit, TA DoC 2017) | 70–216 × 148–356 mm (OG A-14) | 105–216 × 148–356 mm (OG A-14); datasheet limits the P-4531DN cassette to A5R and larger | MP tray only |
| P-5032DN | ECOSYS P3150dn (by list position, TA/UTAX DoC 2023) | 70 × 148 – 216 × 356 mm (OG 11-21) | 105 × 148 – 216 × 356 mm (OG 11-21, 3-15) | MP tray only |

* MP tray: 60–220 g/m² (cardstock up to 230 / 209.5 g/m² depending on model), 100 sheets of 80 g/m²; thick paper
  (≥ 106 g/m²) limited to 5 sheets and needs media type "Thick".
* The ticket must be fed **portrait**: rotated (170.18 × 82.296 mm) it would violate the 148 mm minimum length.
* Optional cassettes (PF-320 / PF-3110: min width 92 mm) cannot feed the ticket. Optional bulk feeders
  (PF-315+ / PF-3100: 76–216 × 148–305 mm) fit by range but replace the MP tray and are untested.
* Duplex size ranges are not documented by TA/UTAX; irrelevant because tickets are printed single-sided.

## Physical printer setting required (cannot be done by software)

All three operation guides say the MP tray paper size must be set on the printer's operation panel:
`Menu > Paper Settings > MP Tray Set. > Paper Size = Custom` (and `Media Type`, e.g. Thick for heavy stock).
A size mismatch between the job and the MP tray setting can cause paper jams (UTAX OG P-4030DN p.4-26).
P-5032DN additionally offers `MP Tray Priority = Always` (feeds from the MP tray whenever it holds paper,
regardless of the driver setting; OG 8-10). Whether narrow 82 mm ticket stock feeds and registers reliably must be
confirmed on each model (hardware acceptance checklist).

## Source documents

* UTAX English OG P-4030D/DN family (TA English OG returned HTTP 502 repeatedly):
  https://www.utax.com/caas/v1/ta/media/113344/data/7869fe630eeb3afd61d4809d49c8ac97/IH_P4030D_4030DN_4530DN_5030DN_6030DN_UT_UK.pdf
  (cross-checked with the TA German OG, media 113298 — identical ranges).
* TA English OG P-4531DN family:
  https://www.triumph-adler.com/caas/v1/ta/media/113522/data/0015e7165caacac98da7cc71fa6d0b6b/IH_P-4531DN_P-5031DN_P-5531DN_P-6031DN_TA_UK.pdf
* TA English OG P-4532DN/P-5032DN/P-5532DN Rev 1:
  https://www.triumph-adler.com/caas/v1/ta/media/113636/data/d9395b434f59131c3bde688e252769ab/EN_IH_P-4532DN_P-5032DN_P-5532DN_TA_Rev_1.pdf
* Kyocera English OG FS-2100D/2100DN/4100DN/4200DN/4300DN Rev. 2 (added 2026-11-10 for the FS-2100D/DN; p. A-14 and
  A-16 give MP tray 70–216 × 148–356 mm and an FS-2100D/DN cassette minimum of 140 × 210 mm, the same as the P-4030DN):
  https://www.kyoceradocumentsolutions.eu/content/dam/download-center-cf/eu/documents/usermanual/FS_2100D_2100DN_4100DN_4200DN_4300DN_OG__ENG__Rev_2_pdf.download.pdf
* EU declarations of conformity with OEM statements: UTAX P-4030D(N) family (media 113308), TA P-4531DN
  (media 113456), TA/UTAX P-5032DN family Rev 4 (media 113790).
* Discrepancy noted: the TA P-5032DN datasheet/web data give an MP-tray minimum length of 139.7 mm and Kyocera
  gives 140 mm; the OGs give 148 mm. The configuration uses the conservative 148 mm.
