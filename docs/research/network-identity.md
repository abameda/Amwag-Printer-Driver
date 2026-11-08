# Network model identification for TA Triumph-Adler / UTAX P-4030DN, P-4531DN and P-5032DN (Kyocera family)

This was documentation research only. I sent no traffic to any printer, changed no settings on this machine, and executed no downloaded binary. I only read strings from them, hashed them and checked their signatures.

Confidence scale used below:
- **High**: a primary source, or several independent sources that agree.
- **Medium**: one credible secondary source, or a strong inference.
- **Low**: an anecdote or a single weak source.
- **UNVERIFIED**: no evidence found.

---

## 0. Summary and recommended order of checks

1. **SNMP (best option).** Send one SNMP v2c GET to UDP 161 with community `public` and these varbinds:
   - `1.3.6.1.2.1.1.2.0` (sysObjectID). On every Kyocera, TA and UTAX sample found, this is `1.3.6.1.4.1.1347.41`. It tells you the vendor family only, not the model.
   - `1.3.6.1.4.1.2699.1.2.1.2.1.1.3.1` (ppmPrinterIEEE1284DeviceId.1). Parse `MFG` and `MDL` from it.
   - `1.3.6.1.4.1.1347.43.5.1.1.1.1` (Kyocera kcprtGeneralModelName.1). Returns the bare model name.
   - `1.3.6.1.2.1.25.3.2.1.3.1` (hrDeviceDescr.1). Also returns the bare model name.
   - `1.3.6.1.2.1.1.1.0` (sysDescr.0). Gives a brand string only.
   - Serial number: `1.3.6.1.2.1.43.5.1.1.17.1` and/or `1.3.6.1.4.1.1347.43.5.1.1.28.1`.
2. **Decide the model** by an exact match of `MDL` against `P-4030DN`, `P-4531DN` or `P-5032DN`. Cross-check with kcprtGeneralModelName.1 or hrDeviceDescr.1. Optionally, compute the Windows hardware ID (section 4.5) and compare it with the approved manifest.
3. **Fallbacks, in this order:**
   - IPP Get-Printer-Attributes. This is often unavailable out of the box: IPP is **Off by default on P-4030DN**.
   - PJL `@PJL INFO ID` on port 9100. This uses the print path, and the answer only comes back if the raw port's "Bi-directional Printing" setting is On.
   - WSD or mDNS.

### Expected identity of the three target models

| Model | Windows hardware IDs in official TA INFs | Raw MFG+MDL that the checksum proves | Inferred split | Confidence |
|---|---|---|---|---|
| P-4030DN | `WSDPRINT\`, `USBPRINT\`, `LPTENUM\UTAX_TAP-4030DN1652` | `UTAX_TAP-4030DN` | MFG=`UTAX_TA`, MDL=`P-4030DN` | High (vendor PPD gives this split) |
| P-4531DN | `WSDPRINT\`, `USBPRINT\UTAX_TAP-4531DND6CF` (`LPTENUM\` in the v4 INF) | `UTAX_TAP-4531DN` | `UTAX_TA` + `P-4531DN` | Concatenation High, split Medium-High |
| P-5032DN | `WSDPRINT\`, `USBPRINT\TA_UTAXP-5032DNEED0` | `TA_UTAXP-5032DN` | `TA_UTAX` + `P-5032DN` | Concatenation High, split Medium |

- **Expected SNMP and IPP model strings** (`P-4030DN`, `P-4531DN`, `P-5032DN`): UNVERIFIED. No capture exists for these exact models. The pattern comes from a P-4532DN capture.
- **Expected sysDescr:**
  - P-5032DN: probably `TA_UTAX Printing System`, because its brand token matches the P-4532DN capture (Medium).
  - P-4030DN and P-4531DN: possibly `UTAX_TA Printing System` (Low).

---

## 1. SNMP v1/v2c

### 1.1 Is SNMP enabled, and with which community?

- **Default read community is `public`** (High). Source: Kyocera Command Center RX User Guide 2024.01 (C1DCCRXKDEN00), Management Settings > SNMP, p.10-18. That guide also covers the TA/UTAX "Embedded Web Server RX" models.
  - The same section says the write community default depends on region.
  - SNMPv1/v2c and SNMPv3 are switched on and off separately under Network Settings > Protocol.
  - URL: https://dam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/document/manual/product_086/CommanCenterRX_EN.pdf
- **P-4030DN factory defaults** (High). Source: TA Operation Guide, German edition, section "Netzwerkprotokolle einstellen", p.4-62. The English link (media/113348) returned HTTP 502 on four attempts.
  - SNMP **On**, SNMPv3 Off, Raw Port On, LPD On, HTTP On.
  - URL: https://www.triumph-adler.com/caas/v1/ta/media/113298/data/bc9ccdc599b49c1564804f126e3d09c5/IH_P-4030D_4030DN_4530DN_5030DN_6030DN_TA_D.pdf
- **P-4531DN and P-5032DN.** The TA English guides list SNMP and SNMPv3 as on/off settings and say SNMPv1/v2c/v3 is supported (P-4531DN p.4-78 and spec appendix; P-5032DN pp.8-18 to 8-19). They do not state the defaults, so those defaults are UNVERIFIED.
  - https://www.triumph-adler.com/caas/v1/ta/media/113522/data/0015e7165caacac98da7cc71fa6d0b6b/IH_P-4531DN_P-5031DN_P-5531DN_P-6031DN_TA_UK.pdf
  - https://www.triumph-adler.com/caas/v1/ta/media/113636/data/d9395b434f59131c3bde688e252769ab/EN_IH_P-4532DN_P-5032DN_P-5532DN_TA_Rev_1.pdf

### 1.2 OID definitions

| Object | OID (instance) | Definition source | Notes |
|---|---|---|---|
| sysDescr.0 | 1.3.6.1.2.1.1.1.0 | MIB-II | Not model-specific on Kyocera devices |
| sysObjectID.0 | 1.3.6.1.2.1.1.2.0 | MIB-II | IANA enterprise 1347 = "KYOCERA Corporation" (IANA registry) |
| hrDeviceDescr.1 | 1.3.6.1.2.1.25.3.2.1.3.1 | RFC 2790, hrDeviceEntry 3, DisplayString(0..64) | RFC says it should include the manufacturer; Kyocera returns the bare model |
| hrDeviceID.1 | 1.3.6.1.2.1.25.3.2.1.4.1 | RFC 2790 | Observed `1.3.6.1.4.1.1347.43.1.2.1` on Kyocera **and** TA, so not model-specific |
| prtGeneralPrinterName.1 | 1.3.6.1.2.1.43.5.1.1.16.1 | RFC 3805, prtGeneralEntry 16, OCTET STRING(0..127), read-write | RFC defines it as an administrator-specified name, so **do not** use it as the model |
| prtGeneralSerialNumber.1 | 1.3.6.1.2.1.43.5.1.1.17.1 | RFC 3805, prtGeneralEntry 17, read-write | Index is hrDeviceIndex |
| ppmPrinterIEEE1284DeviceId.1 | 1.3.6.1.4.1.2699.1.2.1.2.1.1.3.1 | PWG 5107.1-2005 (PPM MIB 1.0), OCTET STRING(0..1023), read-only, indexed by ppmPrinterIndex | Must exactly equal the IEEE 1284-2000 Device ID without the length bytes. MFG/MANUFACTURER and MDL/MODEL are mandatory. |
| ppmPortServiceNameOrURI.p.n | 1.3.6.1.4.1.2699.1.2.1.3.1.1.4.p.n | PWG 5107.1 | Can reveal the device's own IPP URI (section 3) |
| kcprtGeneralModelName.1 | 1.3.6.1.4.1.1347.43.5.1.1.1.1 | KYOCERA-Private-MIB, DisplayString, read-only, described as "The printer's model name", index hrDeviceIndex | Vendor installer also uses it (section 1.4) |
| serial (named kcprtSerialNumber by Datadog) | 1.3.6.1.4.1.1347.43.5.1.1.28.1 | Not in the old MIB revision. Labelled serial by Checkmk and Datadog. | In the P2040dw walk it equals prtGeneralSerialNumber.1 (Medium-High) |
| firmware (kcprtFirmwareVersion) | 1.3.6.1.4.1.1347.43.5.4.1.5.1.1 | KYOCERA-Private-MIB, index hrDeviceIndex + kcprtCpuIndex | |

Sources for the table:
- PWG 5107.1: https://ftp.pwg.org/pub/pwg/candidates/cs-pmpportmib10-20051025-5107.1.pdf
- RFC 3805 and RFC 2790: rfc-editor.org
- I did not find the official Kyocera MIB file on kyoceradocumentsolutions.com. The KYOCERA-Private-MIB object names come from a public compiled copy (github.com/cisco-kusanagi/mibs.snmplabs.com `json/KYOCERA-Private-MIB.json`). Confidence: object name Medium, OID semantics High.

### 1.3 Published example values from real devices (third-party captures)

**TA/UTAX P-4532DN** (High; this is the sibling of the P-5032DN). Source: LibreNMS `tests/snmpsim/utax.snmprec`, commit 86338589e38d25b1902cf264e5b45bf8355ed9c4, 2025-01-12, "Added support for UTAX printers (#16951)".

```
1.3.6.1.2.1.1.1.0              TA_UTAX Printing System
1.3.6.1.2.1.1.2.0              1.3.6.1.4.1.1347.41
1.3.6.1.2.1.25.3.2.1.3.1       P-4532DN        (hrDeviceDescr.2 = RAM DISK)
1.3.6.1.2.1.25.3.2.1.4.1       1.3.6.1.4.1.1347.43.1.2.1
1.3.6.1.4.1.1347.43.5.1.1.1.1  P-4532DN
1.3.6.1.4.1.1347.43.5.1.1.28.1 R9L0309954
1.3.6.1.4.1.1347.43.5.4.1.5.1.1 2TP_S000.003.402
```

LibreNMS detects UTAX devices using sysObjectID `.1.3.6.1.4.1.1347.41` together with an exact sysDescr of `TA_UTAX Printing System` (`resources/definitions/os_detection/utax.yaml`).

**Kyocera ECOSYS P2040dw, full walk** (High). Source: Printmaster `agent/scanner/testdata/kyocera_p2040_snmp.json`, commit 53d932f4f5cab37c76899137a94d89cb561c0a75, 2026-01-28, described as real device data.

```
sysDescr.0  KYOCERA Document Solutions Printing System     sysObjectID.0  .1.3.6.1.4.1.1347.41   sysName.0 ""
hrDeviceDescr.1 / prtGeneralPrinterName.1 / kcprtGeneralModelName.1 / ppmPrinterName.1 = ECOSYS P2040dw
prtGeneralSerialNumber.1 = 1347.43.5.1.1.28.1 = VD21457943      firmware = 2RX_3000.002.503
ppmPrinterIEEE1284DeviceId.1 = ID:ECOSYS P2040dw;MFG:Kyocera;CMD:PCLXL,PostScript Emulation,PCL5E,PJL;MDL:ECOSYS P2040dw;CLS:PRINTER;DES:Kyocera ECOSYS P2040dw;CID:KY_XPS_MonoA4FID;SER:VD21457943;
ppmGeneralNaturalLanguage.0=en-US  NumberOfPrinters.0=1  NumberOfPorts.0=3
ppmPrinterHrDeviceIndex.1=1  ppmPrinterSnmpCommunityName.1=public  ppmPrinterSnmpQueryEnabled.1=1(true)
ppmPort 1.1: name RAW, uri "",  type 11 (chPort9100), port 9100
ppmPort 1.2: name LPR, uri "lp", type 8 (chLPDServer), port 515
ppmPort 1.3: name IPP, uri "http://192.168.0.212/ipp", type 44, port 631
```

**Kyocera FS-1028MFP** (High). Source: LibreNMS `tests/snmpsim/kyocera.snmprec`. sysDescr `KYOCERA Document Solutions Printing System`, sysObjectID `1.3.6.1.4.1.1347.41`, model `FS-1028MFP`, serial `QUV9600664`. This shows the kcprt OIDs also exist on older firmware generations.

**`UTAX_TA Printing System`** (Low). This sysDescr appears once, as an SNMP banner in a third-party pentest CSV (github.com/leonteale/pentestpackage, blob 1f8b4e09â€¦). The model is unknown.

### 1.4 What Kyocera's own installer queries

These are strings read from the official TA KX 8.7 package. Nothing was executed. The Kyocera catalog `utax.cat` has a valid signature from "KYOCERA Document Solutions Inc."

- **KmInstall.exe:**
  - `.1.3.6.1.4.1.1347.43.5.1.1.1.1`, labelled "SnmpModelOID"
  - `.1.3.6.1.4.1.1347.43.5.1.1.28.1`
  - `.1.3.6.1.4.1.1347.40.10.1.1.5.1`, labelled "SnmpHostnameOID"
  - `.1.3.6.1.2.1.1.1.0`, labelled "OID_SYS_DESCR"
  - community `public`
- **KmDiscover.dll:**
  - default "DiscoverOID" is `.1.3.6.1.4.1.1347.43.5.1.1.1.1`
  - uses SNMP broadcast to `255.255.255.255` and `FF02::1`, and SOAP to `239.255.255.250`. Do not copy the broadcast approach.

This confirms the vendor treats 1347.43.5.1.1.1.1 as the model OID (High). Third-party users of the same OIDs: the Checkmk inventory plugin (commit 792e5f4aâ€¦) and the Datadog `kyocera-printer.yaml` profile (sysobjectid 1.3.6.1.4.1.1347.41; commit c3e51bf5â€¦).

### 1.5 Caveats

- **Do not detect the vendor by sysDescr text.**
  - Checkmk looks for the substring `KYOCERA`, which would miss `TA_UTAXâ€¦`.
  - LibreNMS's UTAX rule requires the exact string `TA_UTAX Printing System`, which would miss `UTAX_TAâ€¦`.
  - Use the 1347 sysObjectID prefix instead.
- **SNMPv1 multi-varbind GET:** if any OID is missing (for example, no PPM MIB on older firmware), the whole request fails with `noSuchName` (RFC 1157 Â§4.1.2). With v2c, each missing varbind instead returns `noSuchObject` or `noSuchInstance` (RFC 3416 Â§4.2.1). Use v2c, or one GET per OID with v1.
- **PPM MIB on the three TA models:** UNVERIFIED. It is present on ECOSYS P2040dw.

---

## 2. PJL `@PJL INFO ID` over TCP 9100

- **Request bytes**, using HP PJL Technical Reference Manual Ed.12 (2003) syntax `@PJL INFO category [<CR>]<LF>` with a UEL (`ESC%-12345X`) before and after:
  `1B 25 2D 31 32 33 34 35 58 40 50 4A 4C 20 49 4E 46 4F 20 49 44 0D 0A 1B 25 2D 31 32 33 34 35 58`
- **Response format** (HP manual pp.7-16 to 7-17):
  - an echo line `@PJL INFO ID<CR><LF>`
  - then one or more lines, with the model in double quotes, each ending `<CR><LF>`
  - then a form feed `<FF>` (0x0C)
  - An unsupported category returns `"?"`.
  - HP advises against using INFO ID to infer printer features.
  - URL: https://www.hp.com/ctg/Manual/bpl13208.pdf (High)
- **Kyocera supports PJL.** The 1284 `CMD` field includes `PJL` on P2040dw and M2040dn. The TA P-4030DN PPD also lists PJL.
- **Bidirectional raw port is required for a reply** (High). The Kyocera Command Center RX guides (2024.01 p.8-15/16; product_054 edition, Logical Printers) say "Bi-directional Printing" on the TCP/IP Raw port must be **On** for PJL and PostScript replies to reach the client. When it is Off, data the printer sends back is discarded. The factory default for logical printer LP1 is **UNVERIFIED**.
- **Field capture on a Kyocera MA2000w** (Medium, third-party, 2026-10-18). Source: github.com/yuannancheng/kyocera-client-tool-for-linux `docs/reverse-engineering/protocol-notes.md`, commit d45c05e8â€¦.
  - Framing was `UEL\r\n@PJL â€¦\r\nUEL`.
  - The reply was `@PJL INFO ID` followed by `"MA2000w"`, then FF. That is the bare model in quotes.
  - When the device was waking up, it sometimes dropped the tail of a batch of queries. Send one query, read until FF, and use a timeout.
  - The same notes say Kyocera's Windows client tool uses SNMP, not port 9100, on its network path.
- **TA reply strings** (expected `"P-4030DN"` and so on): UNVERIFIED. Whether a PJL-only job increments counters or ejects a page: UNVERIFIED.

---

## 3. IPP Get-Printer-Attributes

- **TA documentation** (High):
  - Default IPP port is **631** (P-4531DN p.4-78; P-5032DN p.8-20).
  - Plain IPP only works if "IPP Security" in Embedded Web Server RX is set to "Not Secure (IPP&IPPS)". IPP over SSL is a separate switch.
  - **P-4030DN: IPP is Off by default and SSL is Off by default** (German guide pp.4-102 and 4-103).
- **IPP path.** No TA or Kyocera document I found gives the path. Observed values:
  - ECOSYS M2040dn (real device model, OpenPrinting go-mfp `modeling/examples/Kyocera-ECOSYS-M2040dn.py`, commit 5d31f951â€¦):
    - `printer-uri-supported` = `ipp://<ip>:631/ipp/print`, `ipps://<ip>:443/ipp/print`
    - `printer-make-and-model` = `ECOSYS M2040dn` (no manufacturer prefix)
    - `printer-info` = `Kyocera ECOSYS M2040dn`
    - `printer-dns-sd-name` = `Kyocera ECOSYS M2040dn`
    - `printer-name` = `KM7B6A91`
    - `printer-device-id` = `ID:ECOSYS M2040dn;MFG:Kyocera;CMD:PCLXL,PostScript Emulation,PCL5E,PJL;MDL:ECOSYS M2040dn;CLS:PRINTER;DES:Kyocera ECOSYS M2040dn;SER:VCF9192281;URF:â€¦;` (High)
  - ECOSYS P2040dw: the PPM MIB advertises `http://<ip>/ipp` on port 631 (section 1.3, High).
  - `ipp://<ip>/printers/lp1` appears only in a forum anecdote about the "KM Port Setup Utility" (copytechnet.com) (Low).
  - **For the three TA models the path is UNVERIFIED.** Recommendation: read ppmPortServiceNameOrURI over SNMP first; if that fails, try `/ipp/print` and then `/ipp`.
- **Request structure** (RFC 8010 and RFC 8011):
  - HTTP POST with `Content-Type: application/ipp`
  - version (2 bytes), operation-id `0x000B` (Get-Printer-Attributes), request-id (4 bytes)
  - tag `0x01` (operation attributes): `attributes-charset` (charset, `0x47`) = `utf-8`; `attributes-natural-language` (`0x48`); `printer-uri` (`0x45`); `requested-attributes` (keyword `0x44`) = `printer-make-and-model`, `printer-device-id`, `printer-info`
  - then `0x03` (end of attributes)
- **Attribute definitions:**
  - `printer-make-and-model` is text(127), RFC 8011. Kyocera ignores the "make" part.
  - `printer-device-id` is text(1023), PWG 5107.2-2010 Â§5.2, and maps one-to-one to ppmPrinterIEEE1284DeviceId: https://ftp.pwg.org/pub/pwg/candidates/cs-pmp1284cmdset10-20100531-5107.2.pdf

---

## 4. IEEE 1284 Device ID and Windows hardware IDs

### 4.1 Device ID format on Kyocera-family devices

- **Observed** (High; P2040dw over SNMP, M2040dn over IPP and USB):
  `ID:<MDL>;MFG:Kyocera;CMD:PCLXL,PostScript Emulation,PCL5E,PJL;MDL:<MDL>;CLS:PRINTER;DES:Kyocera <MDL>;[CID:KY_XPS_MonoA4FID;]SER:<serial>;`
- **Parsing rules** (PWG 5107.1): keys are separated by `:`, values by `,`, entries end with `;`. Whitespace and control characters are ignored when parsing. MFG/MANUFACTURER and MDL/MODEL are mandatory. Values are vendor-assigned and not localized.
- **TA:** the vendor PPD for P-4030DN declares `*1284DeviceID: "MFG:UTAX_TA;MODEL:P-4030DN;COMMAND SET: POSTSCRIPT,PJL,PCL"` (OpenPrinting foomatic-db `db/maybe_broken/source/PPD/Utax/P-4030D series/EU/English/TA4030DN.ppd`, commit d76897bbâ€¦).
  - PPD 1284 lines are not always accurate. For example, the P-C3062DN PPD says `MFG:UTAX`, while its INF checksum proves `UTAX_TA`.
  - The full TA strings (CMD, DES, CID, SER) are UNVERIFIED.

### 4.2 Official TA INF evidence (exact lines)

Files (read-only copies extracted by the sibling kx87/kxv4 tasks):
- `Kx87_UPD_8.7.0917.zip`: SHA256 `862A5367A365F0FF61365BAF0D229A0F761E6F64EFE8013E8FF6DA8C2C109E48`, from https://www.triumph-adler.com/caas/v1/ta/media/102720/data/04e63668c3001927fbccf7c5c22a6cc3/Kx87_UPD_8.7.0917.zip
- `Kxv4_v6.1.1603_TA_RC7_WHQL.zip`: SHA256 `2226F16A2C5A8EC24B16266B24F8E73160A6C9CEFC2ACF46EFD2FFE922EA273A`, from https://www.triumph-adler.com/caas/v1/ta/media/103622/data/02a1879a81ae6c73a9753c16e9ab05aa/Kxv4_v6.1.1603_TA_RC7_WHQL.zip

`64bit\OEMSETUP.INF` (SHA256 `442B95E9â€¦1C9F`), lines 336-338, 505-506 and 572-573:

```
"P-4030DN KX"	= P_4030DN,WSDPRINT\UTAX_TAP-4030DN1652,P-4030DN
"P-4030DN KX"	= P_4030DN,LPTENUM\UTAX_TAP-4030DN1652,P-4030DN
"P-4030DN KX"	= P_4030DN,USBPRINT\UTAX_TAP-4030DN1652,P-4030DN
"P-4531DN KX"	= P_4531DN,WSDPRINT\UTAX_TAP-4531DND6CF,P-4531DN
"P-4531DN KX"	= P_4531DN,USBPRINT\UTAX_TAP-4531DND6CF,P-4531DN
"P-5032DN KX" = P_5032DN,WSDPRINT\TA_UTAXP-5032DNEED0,P-5032DN
"P-5032DN KX" = P_5032DN,USBPRINT\TA_UTAXP-5032DNEED0,P-5032DN
```

v4 `PCL Driver\64bit\win81 and newer\prnkycl1.inf` (SHA256 `A89DEAA8â€¦B491`), lines 224-226, 432-434 and 507-508:
- Same IDs, with install sections `UTLP2100N_PCL6`, `P-4531DN_PCL6` and `P-5032DN_PCL6`.
- `LPTENUM\UTAX_TAP-4531DND6CF` appears here in addition to the KX 8.7 IDs.
- The P-5032DN lines repeat the KX 8.7 IDs (`WSDPRINT\` and `USBPRINT\TA_UTAXP-5032DNEED0`).

### 4.3 What `LPTENUM\UTAX_TAP-4030DN1652` implies

- The body `UTAX_TAP-4030DN` is 15 characters, so no truncation happened. The suffix `1652` is the checksum.
- The checksum reproduces **only** from the raw string `UTAX_TAP-4030DN`, with a literal underscore. The string `UTAX TAP-4030DN` gives `03EB`.
- So the device reports MFG+MDL = `UTAX_TA` + `P-4030DN`, and the underscore is part of the manufacturer value, not a substituted space. The split point comes from the vendor PPD.
- The same reasoning gives:
  - P-4531DN: `UTAX_TA` + `P-4531DN` â†’ `D6CF`
  - P-5032DN: `TA_UTAX` + `P-5032DN` â†’ `EED0`. The variant `TA UTAX` gives `11DA`. The brand switch to `TA_UTAX` matches the `TA_UTAX Printing System` sysDescr of the P-4532DN.

### 4.4 Microsoft documentation of the ID construction

- **LPTENUM** (https://learn.microsoft.com/en-us/windows-hardware/drivers/print/printer-connected-to-an-lpt-port, ms.date 2022-09-08):
  - The ID is the enumerator prefix, then manufacturer, then model, then a 4-character CRC computed from the manufacturer and model strings.
  - Spaces become underscores.
  - The 1284 string is read with IOCTL_PAR_QUERY_DEVICE_ID.
- **USBPRINT** (https://learn.microsoft.com/en-us/windows-hardware/drivers/install/identifiers-generated-by-usbprint-sys):
  - Form `USBPRINT\NameModel(20)Checksum(4)`: the manufacturer+model concatenation is truncated to at most 20 characters, followed by a 4-character CRC from manufacturer and model, with spaces replaced by underscores.
  - Microsoft warns the OS-generated CRC "may not match â€¦ any other CRC algorithm" and recommends reading `setupapi.dev.log` instead.
- **WSDPRINT:**
  - The Learn page "Enable WSPrint 2.0 on a Device" (ms.date 2023-03-17) shows a device returning `<wprt:DeviceId>MFG:MS3D; CMD:XPS; MDL:Compat; â€¦</wprt:DeviceId>`, which matches the INF ID `WSDPRINT\MS3DCompatE2D2`.
  - The WSD Print Service Definition v1.2 (`WSDPrintServiceV12.doc` inside https://download.microsoft.com/download/E/9/7/E974CFCB-4B3B-40CC-AF92-4F7F84477F0B/Printer.zip, SHA256 `764087d4â€¦fb67`) makes DeviceId mandatory for every WSD printer. It must equal the IEEE 1284-2000 Device ID without the length bytes, and its MANUFACTURER and MODEL values are what Windows uses to build the PnP hardware ID.
  - `Printer.xsd`: `DeviceIdBaseType` has maxLength 1023.
- **Order of operations** (Medium; source is a non-official mirror of the old Microsoft "Printer Plug and Play IDs" / "Plug and Play Parallel Port Devices v1.0b" text, pic.hallikainen.org/techref/os/win/ddk/plugandplayid.htm):
  - Concatenate MANUFACTURER and MODEL.
  - Compute the checksum.
  - Truncate to 20 characters.
  - Append the checksum as 4 characters.
  - Worked example given there: HP LaserJet 4P â†’ `Hewlett-PackardHP_La7EE2`.

### 4.5 Checksum algorithm

Microsoft does not document the algorithm. The version below is derived empirically, but it reproduced every real-device ID I tested.

- It is a CRC-16 with reflected polynomial 0xA001, initial value 0, no final XOR, computed one byte at a time using two 16-entry nibble tables.
- **It is not standard CRC-16/ARC.** The last high-nibble entry is `0x4600` (octal `043000`) instead of the mathematically correct `0x4400`. The difference only shows when `(byte ^ crcLow)` is 0xF0 or above.
- The exact same tables appear in the public-domain DECUS "diff" CRC16 hash (attributed to Stu Wecker, DEC memo 130-959-002-00). Source: Open Watcom `bld/builder/c/diff.c` lines 1017-1050 at commit 3146fbd3dba9d57e77710e1e7fe3aff738b5b6c6.
- Steps:
  1. Compute the checksum over the **raw** MFG+MDL, before replacing spaces and before truncating.
  2. Replace spaces with `_`. Other characters stay as they are; parentheses and `-` are kept in real IDs.
  3. Truncate to 20 characters. The longest body in the INFs is exactly 20.
  4. Append the checksum as 4 uppercase hex digits.

C# reference implementation. I verified this with .NET SDK 10.0.401 against 7 test vectors; all passed.

```csharp
static readonly ushort[] Lo = {0x0000,0xC0C1,0xC181,0x0140,0xC301,0x03C0,0x0280,0xC241,0xC601,0x06C0,0x0780,0xC741,0x0500,0xC5C1,0xC481,0x0440};
static readonly ushort[] Hi = {0x0000,0xCC01,0xD801,0x1400,0xF001,0x3C00,0x2800,0xE401,0xA001,0x6C00,0x7800,0xB401,0x5000,0x9C01,0x8801,0x4600}; // NOT 0x4400
static ushort Checksum(string raw) { ushort c = 0;
  foreach (byte b in Encoding.ASCII.GetBytes(raw)) { int t = (b ^ c) & 0xFF; c = (ushort)((c >> 8) ^ Lo[t & 0xF] ^ Hi[t >> 4]); } return c; }
static string HardwareId(string en, string mfg, string mdl) { string raw = mfg + mdl, body = raw.Replace(' ', '_');
  if (body.Length > 20) body = body.Substring(0, 20); return $"{en}\\{body}{Checksum(raw):X4}"; }
```

**Worked example** for `UTAX_TAP-4030DN`. The CRC value after each character:
Uâ†’3FC0, Tâ†’AF3E, Aâ†’E0EE, Xâ†’B661, _â†’D037, Tâ†’2990, Aâ†’5CE9, Pâ†’B29D, -â†’B4B3, 4â†’62F4, 0â†’9363, 3â†’3C93, 0â†’797C, Dâ†’D278, Nâ†’**1652**

**Validation results:**
- All 312 unique IDs in the TA KX 8.7 and v4 INFs:
  - 201 have untruncated bodies. All 200 real-device IDs reproduce, each with exactly one space/underscore reading. The one failure is the synthetic universal-driver ID `LPTENUM\UTAX-42873DDE`. Standard CRC-16/ARC reproduces only 138.
  - 97 of 100 truncated IDs reproduce from the full model name taken from the INF description. The 3 untested are legacy `ORIGINALPrinting_Sysâ€¦` entries whose full names I don't know.
- Microsoft's own examples:
  - `MS3DCompat` â†’ `E2D2` (matches the WSDPRINT doc).
  - HP LaserJet 4P â†’ `7EE2` (matches the old spec).
  - The Learn example suffix `3115` is **not** produced by its printed model name "HP Color LaserJet 550" (that gives `5523`). It **is** produced by "HP Color LaserJet 8550" and "4500". The printed model name is probably a typo (my inference). Standard CRC-16/ARC gives `0640` for those.

Recommendation: match on the MDL string. Use the computed hardware ID only as a cross-check, and keep the test vectors above in unit tests. Behaviour with non-ASCII input or unusual whitespace is UNVERIFIED.

---

## 5. Discovery without scanning

### 5.1 WS-Discovery

- **Constants** from WS-Discovery 2005/04 (https://specs.xmlsoap.org/ws/2005/04/discovery/ws-discovery.pdf) (High):
  - UDP port 3702; IPv4 group 239.255.255.250; IPv6 group FF02::C
  - APP_MAX_DELAY 500 ms; MATCH_TIMEOUT = APP_MAX_DELAY + 100 ms
  - `To` = `urn:schemas-xmlsoap-org:ws:2005:04:discovery`
  - Action `http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe`
- **Print device type** (WSD Print Service Definition v1.2): `wprt:PrintDeviceType`, namespace `http://schemas.microsoft.com/windows/2006/08/wdp/print`.
  - Hello and ProbeMatch messages carry Types `wsdp:Device wprt:PrintDeviceType`, where `wsdp` = `http://schemas.xmlsoap.org/ws/2006/02/devprof`.
  - Learn pages show these namespaces as `https://â€¦`, which is a rendering artifact. The real namespace strings use `http://`, as in the original specs and in the Kyocera metadata.
- **Single-host (directed) discovery:**
  - The WS-Print v1.2 spec defines a stable Probe-over-HTTP URL: `http://<host>:80/StableWSDiscoveryEndpoint/schemas-xmlsoap-org_ws_2005_04_discovery`, plus an `https://<host>:443/â€¦` variant.
  - Learn's "Probe Message" page (ms.date 2018-05-31) says WSDAPI sends Probe by UDP multicast, does not support unicast UDP Probes, and uses HTTP/HTTPS for directed discovery.
  - Whether Kyocera or TA devices implement the stable URL: **UNVERIFIED**.
- **Kyocera, observed** (Medium-High):
  - MA2000w ProbeMatch (third-party notes cited in Â§2): Types `devprof:Device print:PrintDeviceType`, XAddrs `http://<ip>:5357/DeviceService/`. The same notes say multicast responses were unreliable.
  - ECOSYS M2040dn metadata (go-mfp `proto/wsd/samples_test.go`, commit b75d6bb6â€¦):
    - ThisModel: Manufacturer `Kyocera`, ModelName and ModelNumber `ECOSYS M2040dn`
    - ThisDevice: FriendlyName `Kyocera:ECOSYS M2040dn:KM7B6A91`, plus SerialNumber and FirmwareVersion
    - hosted print service at `http://<ip>:5358/WSDPrinter`
    - pnpx:HardwareId `VEN_0103&DEV_069D`; DeviceCategory `MFP MobilePrinter`
  - After that, `GetPrinterElements(wprt:PrinterDescription)` returns `wprt:DeviceId`. The fields of the ThisModel structure are documented in Learn's `WSD_THIS_MODEL_METADATA` page.
- **TA:**
  - WSD Print and Enhanced WSD are On by default on P-4030DN (German guide p.4-101).
  - The `WSDPRINT\` IDs in the TA INFs imply the WSD DeviceId carries the same MFG and MDL as USB (Medium-High).
  - The TA ThisModel strings are UNVERIFIED.

### 5.2 mDNS / DNS-SD

- **Service types** (Bonjour Printing Specification 1.2.1, https://developer.apple.com/bonjour/printing-specification/bonjourprinting-1.2.1.pdf):
  - `_printer._tcp` (515), `_ipp._tcp` (631), `_ipps._tcp`, `_pdl-datastream._tcp` (9100)
- **TXT keys** (same spec):
  - `ty` = make and model string
  - `product` = PostScript product string in parentheses
  - `usb_MFG`, `usb_MDL`, `usb_CMD` = the 1284 MANUFACTURER, MODEL and COMMAND SET values
  - `rp` = resource path; `adminurl`; `note`; `pdl`; `txtvers`
- **Querying one host only** (RFC 6762):
  - Â§5.5: a responder should answer a direct unicast query to port 5353 as if it were a "QU" question, and may drop it if the source is not on the local link.
  - Â§6.7: a query from a source port other than 5353 gets a unicast reply.
  - The multicast groups are 224.0.0.251 and FF02::FB, port 5353.
- **Kyocera/TA:**
  - Bonjour is supported on P-4030DN (German guide; UDP 5353). The Bonjour name is editable in Command Center RX.
  - P-5032DN supports AirPrint and Mopria (EN guide p.4-11).
  - The observed Kyocera DNS-SD instance name is `Kyocera ECOSYS M2040dn`.
  - TA TXT values (`ty`, `usb_MFG`, `usb_MDL`) and the Bonjour factory default are **UNVERIFIED**. A P-4030DN guide screenshot shows Bonjour On, but the text does not state the default.

---

## 6. How the Windows Standard TCP/IP Port Monitor uses SNMP

- **Device type** (High):
  - PORT_DATA_2.DeviceType holds MIB-II sysDescr (MS-RPRN 2.2.2.14.4, https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/2d256a4b-819c-4b84-8e8f-b1edb1dcf0c2).
  - TCPMON matches sysDescr against the `[Ports]` patterns in `%windir%\System32\tcpmon.ini`.
  - On this Windows 11 machine that file is 60,124 bytes, SHA256 `A8E2A47614DFBCC514D8E51E4B9B0F7D6DA203E8CCED9E759F83D8539CCF84CB`, and its header comment says it is used to determine the port type.
  - It contains `"*KYOCERA*" = Kyocera_EcoLAN`, whose section is `[Kyocera_EcoLAN]` with NAME "Kyocera Network Printer", 1 port, RAW 9100, `SNMP1="?"`.
  - The fallback section `[<Generic Network Card>]` is also RAW 9100.
  - There are no UTAX or Triumph patterns, so TA devices will show as "Generic Network Card" (High inference).
  - A 2004 Microsoft-authored PWG proposal (Pavicevic and Fenelon, mail.pwg.org/â€¦/TCPMonMIBExtension.doc) confirms the sysDescr-to-TCPMON.INI lookup.
- **1284 Device ID** (High; MS-RPRN TCPMON command table, https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/4f6172c7-0831-4ca6-a374-72c841f27c7c):
  - XcvData `DeviceID` starts the query. `SetDeviceIDOid` overrides the OID; the default is `1.3.6.1.4.1.2699.1.2.1.2.1.1.3.<1-based port index>`, which is ppmPrinterIEEE1284DeviceId.
  - `GetPortList` returns PORT_DATA_LIST_1 (an array of PORT_DATA_2). That this list comes from ppmPortTable is a consistent inference but UNVERIFIED.
  - PORT_DATA_2.PortMonitorMibIndex is the index used for the 1284 query.
  - Appendix A notes <137> and <390>: these commands and PORT_DATA_2 exist only from Windows Vista onward.
- **Purpose:**
  - The 2004 Microsoft proposal says the port monitor uses the 1284 string to compute the PnP ID and install the driver automatically.
  - KB 814586 (https://learn.microsoft.com/en-us/troubleshoot/windows-server/printing/standard-port-monitor-for-tcpip) says the monitor configures the device from SNMP responses.
  - HCK requirement `Device.Imaging.Printer.Base.TCPMon` requires PPM MIB 1.0 plus the minimum Host Resources and Printer MIB objects for status (https://learn.microsoft.com/en-us/previous-versions/windows/hardware/hck/jj125101(v=vs.85)).
- **Not documented:** which hardware-ID form (prefix) Windows matches a TCP/IP-derived 1284 ID against. UNVERIFIED. The utility should install the driver explicitly by INF model name.

---

## 7. Open items to test on real devices

1. Real SNMP values on P-4030DN, P-4531DN and P-5032DN: sysDescr, PPM MIB support, model and serial OIDs.
2. The full 1284 strings, and whether IPP `printer-make-and-model` is the bare model on TA devices.
3. Factory defaults on P-4531DN and P-5032DN for SNMP, IPP, the IPP Security setting and Bonjour.
4. The IPP path on TA devices.
5. The PJL INFO ID string, the default of LP1 "Bi-directional Printing", and whether a PJL-only job has side effects.
6. WSD ThisModel strings and directed-discovery support.
7. mDNS TXT values.
8. Checksum edge cases with non-ASCII input.

## 8. Evidence files

Root folder: `C:\Users\abdel\AppData\Local\Temp\claude\C--Users-abdel-Desktop-Amwag-Printer\7737442f-5f1d-46cc-85b0-3b47c5e9c693\scratchpad\research\network-identity\`

- **Scripts:**
  - `...\network-identity\scripts\csharp\pnpcrc.cs`
  - `...\network-identity\scripts\validate_inf_ids.py`
  - `...\network-identity\scripts\truncated_ids.py`
  - `...\network-identity\scripts\crc_test.py`
  - `...\network-identity\scripts\id_chars.py`
- **Text extracts:** `...\network-identity\txt\` (operation guides, Command Center RX guides, PWG 5107.1/5107.2, HP PJL manual, WS-Discovery, Bonjour spec, WS-Print spec strings, LibreNMS/Printmaster/go-mfp data copies).
- **Downloads and SHA256:**
  - `dl-og-p4531dn` `fc5950aeâ€¦dad2`
  - `dl-og-p5032dn` `5ed11394â€¦3584`
  - `dl-og-p4030dn-de` `489ed18fâ€¦3c36a`
  - `dl-ccrx-086` `ef5a1feaâ€¦f462`
  - `dl-ccrx-054` `2df2c977â€¦8501`
  - `dl-ms-wsprint\Printer.zip` `764087d4â€¦fb67`
  - `dl-pwg5107.1` `04324040â€¦587e`
  - `dl-pwg5107.2` `b39873daâ€¦8cbf`
  - `dl-hp-pjl` `b6ee452câ€¦707e`
  - `dl-wsdiscovery` `24c110abâ€¦9996`
  - `dl-bonjourprinting` `5811aac3â€¦e74d`
- **Retrieval notes:** the PWG, HP, Apple and xmlsoap PDFs were fetched through WebFetch and only text-extracted. Third-party GitHub data was read as text through the GitHub API.