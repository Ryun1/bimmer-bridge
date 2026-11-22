# Job cookbook — jobs seen on a 2005 E61 550i

*Recorded on a 2005 E61 550i: other cars' ECUs and jobs differ, so check yours with
`ediabas_list_ecus` and `ediabas_describe`.*

**Status: NO job has been run against the car yet.** The lists below are what
`ediabas_describe` returned at the desk on 2026-11-21, with the cable in the VM and not in the
car: job names, their descriptions from the SGBD, and the bridge's policy verdict. They say
which jobs exist and which the bridge will run. Where a job was also described on its own
(`ediabas_describe` with `job`, 2026-11-22), its arguments and result names are listed as the
SGBD declares them; none of these jobs has been run, so no value or meaning of a result is
recorded. Those come from Phase 6. Do not fill them in from the name or the description.

**Policy versions.** Every verdict and count in this file was recorded under policy version 1
(at the desk, 2026-11-21/22), except the one recount under version 2 noted below. Agent 0.3.0 brings
policy version 3: more screen terms, and the `E60` whole-car jobs. The verdicts have to be
recorded again under version 3 at the desk; until then go by what `ediabas_describe` returns,
not by these tables.

This file holds only what a real session returned: jobs listed by `ediabas_describe`, and jobs
that a real session ran successfully on this car, with the arguments used and the result fields
that came back. Nothing from memory, forums or the web goes in: SGBDs differ between variants,
and a job name or result field remembered from another car can be wrong here. Use only jobs
`ediabas_describe` shows as runnable, and check one job's arguments and results with
`ediabas_describe` and `job` before running it.

Entries are added in the same commit as the recorded session (fixtures) they came from.

## Entry format (for a job run on the car)

### `<ECU>` `<JOB>`
- **Purpose:** what it reads, in plain English.
- **Arguments:** exactly as passed (or none).
- **Result fields seen:** name, unit, example value, and what it meant.
- **Policy verdict:** the rule `ediabas_describe` showed (and review status).
- **Typical duration:** `durationMs` seen.
- **Confirmed:** date, session, fixture name.

## Entries (jobs run on the car)

None yet.

## Seen in `ediabas_describe` (not yet run)

Descriptions are quoted as returned. Where the SGBD's description runs to several lines, the
first line is quoted and the KWP2000 service line, if any, follows after " — ". Text marked
*(gloss)* is an English translation added here, not part of what the tool returned.

### `NVE_65` — night vision

`ediabas_describe` on 2026-11-21 (policy version 1): `variant` `NVE_65`, 59 jobs, about 15 s
with comments (`durationMs` 14745). Cable in the VM, not in the car.

**Runnable (allowed by the policy):**

| Job | Rule (review) | Description as returned | *(gloss)* |
|---|---|---|---|
| `INFO` | `allow.global` | `Information SGBD` | information about the SGBD file |
| `IDENT` | `allow.global` | `Identdaten` — `KWP2000: $1A ReadECUIdentification` | identification data |
| `FS_LESEN` | `allow.global` | `Fehlerspeicher lesen (alle Fehler / Ort und Art)` — `KWP2000: $18 ReadDiagnosticTroubleCodesByStatus` | read fault memory (all faults / location and type) |
| `FS_LESEN_DETAIL` | `allow.global` | `Fehlerspeicher lesen (ein Fehler / alle Details)` — `KWP2000: $17 ReadStatusOfDiagnosticTroubleCodes` | read fault memory (one fault / all details) |
| `IS_LESEN` | `allow.global` | `Infospeicher lesen (alle Info-Meldungen / Ort und Art)` — `KWP2000: $22 ReadDataByCommonIdentifier` | read info memory (all info messages / location and type) |
| `IS_LESEN_DETAIL` | `allow.global` | `Infospeicher lesen (alle Info-Meldungen / Ort und Art)` — `KWP2000: $22 ReadDataByCommonIdentifier` | same first line as `IS_LESEN`; the rest of its description names `$2001 - $20FF dtcShadowMemoryEntry` |
| `PRUEFSTEMPEL_LESEN` | `allow.global` | `Auslesen des Pruefstempels` — `KWP2000: $22 ReadDataByCommonIdentifier` | read the test stamp |
| `C_FG_LESEN` | `allow.global` | `Fahrgestellnummer lesen` — `KWP2000: $1A ReadECUIdentification` | read the VIN. Its result is the car's VIN: never write it into a file here or quote it beyond what the user asks for |
| `SERIENNUMMER_LESEN` | `allow.global` | `Hersteller Seriennummer lesen` — `KWP2000: $1A ReadECUIdentification` | read the manufacturer's serial number |
| `ZIF_LESEN` | `allow.global` | `Auslesen des Zulieferinfofeldes` — `KWP2000: $22   ReadDataByCommonIdentifier` | read the supplier info field |
| `ZIF_BACKUP_LESEN` | `allow.global` | `Auslesen des Backups des Zulieferinfofeldes` | read the backup of the supplier info field |
| `PHYSIKALISCHE_HW_NR_LESEN` | `allow.global` | `Auslesen der physikalischen Hardwarenummer` — `KWP2000: $1A ReadECUIdentification` | read the physical hardware number |
| `HARDWARE_REFERENZ_LESEN` | `allow.global` | `Auslesen der Hardware Referenz` — `KWP2000: $22   ReadDataByCommonIdentifier` | read the hardware reference |
| `DATEN_REFERENZ_LESEN` | `allow.global` | `Auslesen der Daten Referenz` — `KWP2000: $22   ReadDataByCommonIdentifier` | read the data reference |
| `AIF_LESEN` | `allow.global` | `Auslesen des Anwender Informations Feldes` — `KWP 2000: $23 ReadMemoryByAddress` | read the user information field |
| `STATUS_TASTER` | `allow.pattern` (review passed) | `Die Spannung (mV) und wird dezimal ausgegeben` (more lines below) | button status; the voltage is given in mV, in decimal |
| `STATUS_KLEMMEN` | `allow.pattern` (review passed) | `read clamp status` | (already English) |
| `STATUS_BORDNETZSPANNUNG` | `allow.pattern` (review passed) | `Bordnetzspannung in Volt` | on-board supply voltage in volts |
| `STATUS_ENERGIESPARMODUS` | `allow.pattern` (review passed) | `read out status of energy saving mode` | (already English) |
| `STATUS_PAIRING` | `allow.pattern` (review passed) | `Gibt den Pairing-Status aus zwischen ECU und CAM` (more lines below) | gives the pairing status between the ECU and the camera |
| `STATUS_NIVI` | `allow.pattern` (review passed) | `Liest den Status von NIVI` | reads the night-vision status |
| `STATUS_FSC_LESEN` | `allow.pattern` (review passed) | `gibt den detaillierten Status des Freischaltcodes aus` | gives the detailed status of the enabling code (FSC) |

The SGBD's own value tables for two of these, as returned (the SGBD's description, not a
reading from the car):

`STATUS_TASTER`:
```
Die Spannung (mV) und wird dezimal ausgegeben
Taster-Status
0 : 2,53V - 0,20V  =>  Taster gedrueckt
1 : 4,20V - 2,96V  =>  Taster nicht gedrueckt
2 : > 4,20V Taster nicht angeschlossen
3 : 4,96V - 2,53V  =>  nicht definiert
4 : <0,20V  =>  Kurzschluss
```
*(gloss)* 0 button pressed; 1 button not pressed; 2 button not connected; 3 not defined;
4 short circuit.

`STATUS_PAIRING`:
```
Gibt den Pairing-Status aus zwischen ECU und CAM
00  (0x00) -> Pairing erfolgreich (NIVI Kamera ist freigeschaltet für dieses Fahrzeug)
254 (0xFE) -> Pairing nicht erfolgt (NIVI Kamera ist NICHT freigeschaltet für dieses Fahrzeug)
```
*(gloss)* 0 pairing succeeded (the night-vision camera is enabled for this car); 254 pairing
not done (the camera is NOT enabled for this car).

The description of `STATUS_FSC_LESEN` also says, as returned: `für die einfache Überprüfung
des FSC-Status` / `den Job 'Status Pairing'verwenden` — *(gloss)* for a simple check of the FSC
status, use the `STATUS_PAIRING` job.

**Notable refused jobs** (the bridge will not run these; do not look for another way):

| Job | Rule | Description as returned (first line) | *(gloss)* |
|---|---|---|---|
| `FS_LOESCHEN` | `deny.substring:LOESCH` | `Fehlerspeicher loeschen` | clear fault memory |
| `IS_LOESCHEN` | `deny.substring:LOESCH` | `Infospeicher loeschen` | clear info memory |
| `STEUERN_TASTER` | `deny.substring:STEUER` | `Simuliert den NIVI-Taster (Entspricht einem Tasterdruck fuer eine Sekunde)` | simulates the night-vision button. Its description also warns, as returned: `WICHTIG: Nach der Aktivierung ist der reale NIVI-Taster wirkungslos bis ein Steuergerät-Reset durchgeführt wird` *(gloss: after activation the real button does nothing until the ECU is reset)* |
| `STEUERGERAETE_RESET` | `deny.substring:STEUER` | `Steuergeraete reset ausloesen` | trigger an ECU reset |
| `SLEEP_MODE` | `deny.substring:SLEEP` | `SG in Sleep-Mode versetzen` | put the ECU into sleep mode |
| `ENERGIESPARMODE` | `default_deny` | `Einstellen des Energiesparmodes` | set the energy-saving mode (the read is `STATUS_ENERGIESPARMODUS`, above) |
| `DIAGNOSE_MODE` | `default_deny` | `SG in bestimmten Diagnosemode bringen` | put the ECU into a given diagnostic mode |
| `SPEICHER_LESEN` | `deny.token:SPEICHER` | `Auslesen des Steuergeraete-Speichers` | read the ECU's memory by address |
| `C_C_LESEN` | `default_deny` | `Codierdaten lesen` | read coding data (not on the allowed list) |
| `C_FG_SCHREIBEN`, `C_C_SCHREIBEN`, `PRUEFSTEMPEL_SCHREIBEN`, `SPEICHER_SCHREIBEN`, `AIF_SCHREIBEN` | `deny.substring:SCHREIB` | e.g. `Fahrgestellnummer schreiben`, `Codierdaten schreiben` | writes |
| `FLASH_*` (for example `FLASH_LOESCHEN`, `FLASH_SCHREIBEN`, `FLASH_ZEITEN_LESEN`) | `deny.substring:FLASH` (or `LOESCH` / `SCHREIB`) | e.g. `Flash loeschen` | flashing, refused even where the name says `LESEN` |

The other refused jobs on `NVE_65` are the remaining coding jobs (`C_*`; only `C_FG_LESEN` is
runnable), authentication (`AUTHENTISIERUNG_*`) and
session-control jobs; `ediabas_describe` shows each with its rule.

### `TMODE` — the diagnostic interface, not a car module

`ediabas_describe` on 2026-11-21 (policy version 1): `variant` `TMODE`, 25 jobs (`durationMs` 1521). `TMODE`
loads with no cable connected at all. It is a restricted ECU in the policy: only the jobs on
its own list run (`allow.ecu`); everything else is `ecu_restricted` or denied by name.

**Runnable (allowed by the policy):**

| Job | Rule (review) | Description as returned | *(gloss)* |
|---|---|---|---|
| `INFO` | `allow.ecu` | `Information SGBD` | information about the SGBD file |
| `LESE_INTERFACE_TYP` | `allow.ecu` | `Dieser Job stellt den Interfacetyp fest` | determines the interface type |
| `LESE_INTERFACE_VERSION` | `allow.ecu` | `Dieser Job liest die Versionsnummer des Interface` | reads the interface's version number |
| `LESE_SPANNUNG_KL30` | `allow.ecu` | `Dieser Job stellt die Batteriespannung fest` | determines the battery voltage |
| `LESE_SPANNUNG_KL15` | `allow.ecu` | `Dieser Job stellt die Spannung an der Zuendung fest` | determines the voltage at the ignition |

None of these has been run, with or without the car; what they return is not recorded. The
refused `TMODE` jobs include every `SETZE_*` job (`deny.substring:SETZE`), the telegram jobs
`SENDE_TELEGRAMM`, `SENDE_TELEGR_WIEDERHOLT` and `HOLE_ANTWORT_TELEGR`
(`deny.substring:TELEGR`), and `REICHE_AN_INTERFACE_DURCH`, `INITIALISIERUNG` and the
remaining jobs (`ecu_restricted`).

## Battery-drain related jobs (desk, 2026-11-21/22, not yet run)

The job lists below were read with the cable in the VM and **not plugged into the car**; the
files are not known to be fitted to this car (see `ecu-map.md`). The single jobs were then
described one at a time with `ediabas_describe` and `job` on 2026-11-22. None has been run.

Only the jobs described one at a time are listed. For any other job on these files use
`ediabas_describe` with a `filter` (a job-name substring such as `STATUS_` or `KL30`).

Full job lists, 2026-11-21, **policy version 1**:

| File | Jobs | Allowed | Runnable | Full describe (`durationMs`) |
|---|---|---|---|---|
| `E60` | 26 | 1 (`INFO`; version 3 adds `IDENT_FUNKTIONAL`, `FS_LESEN_FUNKTIONAL`) | 1 | 2872 |
| `CAS` | 150 | 62 | 16 (28 needs review, 18 not checked) | 60043 (the limit) |
| `MPM_60` | 57 | 21 | 18 (3 needs review) | 13284 |
| `KOMB60` | 128 | 37 | 36 (1 needs review) | 52730 |
| `KBM_60` | 79 | 24 | 22 (2 needs review) | 24111 |
| `LM_60` | 134 | 37 | 37 | 60051 (the limit) |
| `sgm_60_2` (variant `SGM_60_2`) | 80 | 24 | 24 | 29423 |
| `IHKA60` | 119 | 33 | 30 (3 needs review) | 50419 |
| `CCC_60` | 87 | 30 | 29 (1 needs review) | 28285 |
| `MASK60` | 91 | 25 | 24 (1 needs review) | 30179 |
| `DSC_60` | 95 | 21 | 21 | 37102 |
| `EKPM60_2` | 62 | 22 | 21 (1 needs review) | 14730 |

Below, "Description as returned" quotes the first line and the service line (`KWP2000: ...`),
if any, after " — "; tabs in the SGBD's text are shown as one space. "In the job list" is what
the full list showed for the same job. Every job here takes no arguments (`arguments` was
empty for all of them). Result names are listed as declared, in the SGBD's order.

**Screen changed on 2026-11-22 (agent 0.2.2, policy version 2).** The description screen now
ignores the words "Beschreibung" and "Umsetzer" before matching its terms, so the label
"Jobbeschreibung:" no longer fails a job by itself. The counts and verdicts in this file were
recorded under policy version 1 and are stale where they say "needs review": the same day,
`ediabas_describe` of `CAS` with filter `STATUS` under version 2 returned 54 jobs: 30 passed,
4 reviewed, 6 needs review (`STATUS_CC_DATEN_ARG`, `STATUS_ELV_HISTORY_AUS_BUFFER`,
`STATUS_FNDCOM_FUNKTION`, `STATUS_RDL`, `STATUS_RDU`, `STATUS_TRANSPONDER_PAGE`), 7 not checked
(60 s limit), 7 refused by name; and `MPM_60` showed 19 runnable of 57.

**Approved by the owner on 2026-11-22.** All five needs-review jobs below (`CAS`
`STATUS_WAKEUP_HISTORY`, `STATUS_KLEMMENVERHINDERER`, `STATUS_KL15_ABSCHALTUNG`, `STATUS_WUP`;
`MPM_60` `STATUS_AD_WERTE_LESEN`) are listed in `reviewedJobs` in the VM's
`C:\ProgramData\EdiabasBridge\config.json`, and `ediabas_describe` of each then returned
review `reviewed`: the bridge on that VM now runs them. The approval lives only in that VM config, not in
the repo; after a fresh install the list is empty and they are refused again until the owner adds
them back. The verdicts in the tables below are as first recorded (policy version 1), before
the approval.

**Needs review: the bridge will refuse to run these** (`job_needs_review`). The user decides;
the safe default is to skip them. The likely trigger is a screen term from
`policy/policy.json` `descriptionScreen.terms` found in the recorded description
(case-insensitive, anywhere in a word). The refusal message itself, which names the matched
terms, has not been recorded.

### `CAS`

Single-job describes took 4.8–6.5 s each (`durationMs` 4781–6545).

| Job | Rule (review) | Description as returned | In the job list | Likely trigger |
|---|---|---|---|---|
| `STATUS_PA_WUP` | `allow.pattern` (review passed) | `Lesen des PA Wakeup Pattern` | no description, `not_checked` | — (runnable) |
| `STATUS_WAKEUP_HISTORY` | `allow.pattern` (**needs review**) | `Jobbeschreibung: This Job reads the history trace of the LPM application` — `KWP2000: $31 Start Routine By Local Identifier` | no description, `not_checked` | `schreib` (in `Jobbeschreibung`) and `start` (in `Start Routine`) |
| `STATUS_KLEMMENVERHINDERER` | `allow.pattern` (**needs review**) | `Jobbeschreibung: Der Job dient zum Auslesen der internen Verhinderungsgründe` — `KWP2000: $21 ReadDataByLocalIdentifier` | same description, needs review | `schreib` (in `Jobbeschreibung`) |
| `STATUS_KL15_ABSCHALTUNG` | `allow.pattern` (**needs review**) | `Jobbeschreibung: Status verschiedener Abschaltverhinderer` — `KWP2000: $31 StartRoutineByLocalIdentifier` | same description, needs review | `schreib` (in `Jobbeschreibung`) and `start` (in `StartRoutineByLocalIdentifier`) |
| `STATUS_WUP` | `allow.pattern` (**needs review**) | `Jobbeschreibung: Dieser Job liest den aktuellen Status "Wake-Up-Pattern"` — `KWP 2000:$21 ReadDataByLocalIdentifier` | no description, `not_checked` | `schreib` (in `Jobbeschreibung`) |

The full descriptions, as returned (tabs shown as one space):

`STATUS_WAKEUP_HISTORY`:
```
Jobbeschreibung: This Job reads the history trace of the LPM application
Vorbedingungen: Keine
Diagnose-Service: KWP2000: $31 Start Routine By Local Identifier
$2A History trace of the LPM application
Modus: Default
```

`STATUS_KLEMMENVERHINDERER`:
```
Jobbeschreibung: Der Job dient zum Auslesen der internen Verhinderungsgründe
der Klemmensteuerungs-Statemachine für Klemmenwechsel
• Einschalten der KL15
• Ausschalten der KL15
• Einschalten der KL50
Vorbedingungen: keine
Diagnose-Service: KWP2000: $21 ReadDataByLocalIdentifier
LocalIdentifier 0xA0
Modus: Default
```

`STATUS_KL15_ABSCHALTUNG`:
```
Jobbeschreibung: Status verschiedener Abschaltverhinderer
Vorbedingungen: keine
Diagnose-Service: KWP2000: $31 StartRoutineByLocalIdentifier
LocalIdentifier $2C
Modus: Default
```

`STATUS_WUP`:
```
Jobbeschreibung: Dieser Job liest den aktuellen Status "Wake-Up-Pattern"
Vorbedingungen: keine
Diagnose-Service: KWP 2000:$21 ReadDataByLocalIdentifier
LocalIdentifier $5F
Modus: Default
```

Result names declared:
- `STATUS_PA_WUP`: `WAKEUP_PATTERN`, `JOB_STATUS`.
- `STATUS_WAKEUP_HISTORY`: for each of `ERR1`, `ERR2`, `ERR3`: `STAT_ERRn_CLASS_NR`,
  `STAT_ERRn_CLASS_TXT`, `STAT_ERRn_REASON_1` to `STAT_ERRn_REASON_4`,
  `STAT_ERRn_WAKEUP_REASON`, `STAT_ERRn_MILEAGE`, `STAT_ERRn_RELATIVE_TIME`; then
  `JOB_STATUS`, `_TEL_ANTWORT` (29 in all). Written out: `STAT_ERR1_CLASS_NR`,
  `STAT_ERR1_CLASS_TXT`, `STAT_ERR1_REASON_1`, `STAT_ERR1_REASON_2`, `STAT_ERR1_REASON_3`,
  `STAT_ERR1_REASON_4`, `STAT_ERR1_WAKEUP_REASON`, `STAT_ERR1_MILEAGE`,
  `STAT_ERR1_RELATIVE_TIME`, `STAT_ERR2_CLASS_NR`, `STAT_ERR2_CLASS_TXT`,
  `STAT_ERR2_REASON_1`, `STAT_ERR2_REASON_2`, `STAT_ERR2_REASON_3`, `STAT_ERR2_REASON_4`,
  `STAT_ERR2_WAKEUP_REASON`, `STAT_ERR2_MILEAGE`, `STAT_ERR2_RELATIVE_TIME`,
  `STAT_ERR3_CLASS_NR`, `STAT_ERR3_CLASS_TXT`, `STAT_ERR3_REASON_1`, `STAT_ERR3_REASON_2`,
  `STAT_ERR3_REASON_3`, `STAT_ERR3_REASON_4`, `STAT_ERR3_WAKEUP_REASON`,
  `STAT_ERR3_MILEAGE`, `STAT_ERR3_RELATIVE_TIME`.
- `STATUS_KLEMMENVERHINDERER`: `STAT_KL15_EIN_VERHINDERER`, `STAT_KL15_EIN_VERHINDERER_TEXT`,
  `STAT_KL15_AUS_VERHINDERER`, `STAT_KL15_AUS_VERHINDERER_TEXT`, `STAT_KL50_EIN_VERHINDERER`,
  `STAT_KL50_EIN_VERHINDERER_TEXT`, `JOB_STATUS`, `_TEL_ANTWORT`.
- `STATUS_KL15_ABSCHALTUNG`: `STAT_CODIERUNG_KL15_ABSCHALTUNG_AKTIV`,
  `STAT_ABSCHALTVERHINDERER_WIEDERHOLUNG_AKTIV`, `STAT_ABSCHALTVERHINDERER_BREMSE_AKTIV`,
  `STAT_ABSCHALTVERHINDERER_MOTORLAUF_AKTIV`, `STAT_ABSCHALTVERHINDERER_KUPPLUNG_AKTIV`,
  `STAT_ABSCHALTVERHINDERER_FEWEMODE_AKTIV`, `STAT_ABSCHALTVERHINDERER_GESCHWINDIGKEIT_AKTIV`,
  `STAT_ABSCHALTVERHINDERER_MSA_AKTIV`, `STAT_ABSCHALTVERHINDERER_ABBLENDLICHT_AKTIV`,
  `STAT_ABSCHALTVERHINDERER_WAEHLHEBEL_NICHT_IN_P_AKTIV`,
  `STAT_ABSCHALTVERHINDERER_DIAGNOSE_AKTIV`, `STAT_ABSCHALTVERHINDERER_AKTIV`,
  `STAT_AUTOMATISCHE_ABSCHALTUNG_DURCHGEFUEHRT`, `JOB_STATUS`, `_TEL_ANTWORT`.
- `STATUS_WUP`: `WUP`, `JOB_STATUS`, `_TEL_ANTWORT`.

### `KOMB60`

Single-job describes took about 2 s each (`durationMs` 1921–2084).

| Job | Rule (review) | Description as returned | In the job list |
|---|---|---|---|
| `STATUS_KLEMMENSPANNUNG` | `allow.pattern` (review passed) | `liefert Wert der Kombi-Versorgungsspannung` — `KWP2000: $21 ReadDataByLocalIdentifier` | same, review passed |
| `STATUS_KLEMMEN` | `allow.pattern` (review passed) | `Klemmenstati auslesen` — `KWP2000: $21, $0E` | same, review passed |
| `STATUS_KL30_H_ZAEHLER` | `allow.pattern` (review passed) | `Klemme 30 Stundenzaehler auslesen` — `KWP2000: $21 ReadDataByLocalIdentifier` | same, review passed |

Result names declared:
- `STATUS_KLEMMENSPANNUNG`: `JOB_STATUS`, `STAT_KLEMMENSPANNUNG_WERT`,
  `STAT_KLEMMENSPANNUNG_EINH`, `_TEL_ANTWORT`.
- `STATUS_KLEMMEN`: `JOB_STATUS`, `STAT_KLR_WERT`, `STAT_KL15_WERT`, `STAT_KL50_WERT`,
  `STAT_KLR_EINH`, `STAT_KL15_EINH`, `STAT_KL50_EINH`, `_TEL_ANTWORT`.
- `STATUS_KL30_H_ZAEHLER`: `JOB_STATUS`, `STAT_KL30_H_ZAEHLER_WERT`,
  `STAT_KL30_H_ZAEHLER_EINH`, `_TEL_ANTWORT`.

### `IHKA60`

Single-job describes took 1.6 s and 2.1 s (`durationMs` 1584, 2149).

| Job | Rule (review) | Description as returned | In the job list |
|---|---|---|---|
| `STATUS_ENERGIESPARMODE` | `allow.pattern` (review passed) | `Auslesen des aktuell eingestellten Energiesparmodes` — `KWP2000: $30 InputOutputControlByLocalIdentifier` (next line `$01 ReportCurrentState`) | same, review passed |
| `STATUS_VERBRAUCHERREDUZIERUNG` | `allow.pattern` (review passed) | `Auslesen der Status Stundenzaehler Verbraucherreduzierung` — `KWP2000: $30 InputOutputControlByLocalIdentifier` (next line `$01 ReportCurrentState`) | same, review passed |

Result names declared:
- `STATUS_ENERGIESPARMODE`: `JOB_STATUS`, `STAT_TRANSPORTMODE_WERT`,
  `STAT_WERKSTATTMODE_WERT`, `STAT_FERTIGUNGSMODE_WERT`, `_TEL_ANTWORT`.
- `STATUS_VERBRAUCHERREDUZIERUNG`: `JOB_STATUS`, `STAT_ZAEHLER_HHS_TAKTUNG_WERT`,
  `STAT_ZAEHLER_HHS_TAKTUNG_EINH`, `STAT_ZAEHLER_HHS_AUS_WERT`, `STAT_ZAEHLER_HHS_AUS_EINH`,
  `STAT_ZAEHLER_GEBL_75_PROZ_WERT`, `STAT_ZAEHLER_GEBL_75_PROZ_EINH`,
  `STAT_ZAEHLER_GEBL_50_PROZ_WERT`, `STAT_ZAEHLER_GEBL_50_PROZ_EINH`,
  `STAT_ZAEHLER_GEBL_AUS_WERT`, `STAT_ZAEHLER_GEBL_AUS_EINH`,
  `STAT_ZAEHLER_SH_AUS_BEI_KL15_WERT`, `STAT_ZAEHLER_SH_AUS_BEI_KL15_EINH`,
  `STAT_ZAEHLER_STANDVERBRAUCHER_WERT`, `STAT_ZAEHLER_STANDVERBRAUCHER_EINH`, `_TEL_ANTWORT`.
  Each `..._EINH` result's own description is `min`.

### `sgm_60_2` (variant `SGM_60_2`)

Passed as `sgm_60_2`; the tool reported variant `SGM_60_2`. Single-job describe took 2.7 s
(`durationMs` 2705).

| Job | Rule (review) | Description as returned | In the job list |
|---|---|---|---|
| `STATUS_STROMVERSORGUNG_LESEN` | `allow.pattern` (review passed) | `Status Stromversorgung lesen` — `KWP2000: $22 SG spezifische Daten lesen`. Its last line, as returned: `Wird fruehestens ab I4.40 Unterstuetzt` | same, review passed |

Result names declared: `JOB_STATUS`, `STAT_SPG_KLEMME_30_SGM_WERT`,
`STAT_SPG_KLEMME_30_SGM_EINH`, `STAT_SPG_U60_SGM_WERT`, `STAT_SPG_U60_SGM_EINH`,
`STAT_SPG_U_ASE_SGM_WERT`, `STAT_SPG_U_ASE_SGM_EINH`, `STAT_SPG_U_ASE_S_SGM_WERT`,
`STAT_SPG_U_ASE_S_SGM_EINH`, `STAT_SPG_U5_SW_SGM_WERT`, `STAT_SPG_U5_SW_SGM_EINH`,
`STAT_SPG_U5_TH_ER_SGM_WERT`, `STAT_SPG_U5_TH_ER_SGM_EINH`, `STAT_ENERGIE_RESERVE_LADEN_AKTIV`,
`STAT_HIGHSIDETRANSISTOR`, `STAT_ENERGIERESERVE_AKTIVIERT`, `STAT_ENERGIERESERVE_TEST`,
`STAT_COMPARATOR_ENERGIERESERVE`, `STAT_WAKE_UP_ENERGIERESERVE_AKTIV`, `STAT_U_ASE_S_EIN`,
`STAT_ER_SAFE`, `_TEL_ANTWORT`.

### `MPM_60`

Single-job describes took about 1.3 s each (`durationMs` 1292, 1380).

| Job | Rule (review) | Description as returned | In the job list | Likely trigger |
|---|---|---|---|---|
| `STATUS_ECU_LESEN` | `allow.pattern` (review passed) | `es wird der aktuelle Status der ECU ausgelesen` (no service line) | same, review passed | — (runnable) |
| `STATUS_AD_WERTE_LESEN` | `allow.pattern` (**needs review**) | `es werden die Messwerte des AD-Umsetzers ausgelesen` (no service line) | same, needs review | `setz` (in `AD-Umsetzers`) |

Result names declared:
- `STATUS_ECU_LESEN`: `JOB_STATUS`, `STAT_RELAIS_SOLL_WERT`, `STAT_RELAIS_SOLL_TEXT`,
  `STAT_RELAIS1_IST_WERT`, `STAT_RELAIS1_IST_TEXT`, `STAT_RELAIS2_IST_WERT`,
  `STAT_RELAIS2_IST_TEXT`, `STAT_MODUS_WERT`, `STAT_MODUS_TEXT`, `STAT_DIAGNOSE_WERT`,
  `STAT_DIAGNOSE_TEXT`, `STAT_SLACOUNTER_WERT`, `STAT_SLACOUNTER_TEXT`, `STAT_USCOUNTER_WERT`,
  `STAT_USCOUNTER_TEXT`, `STAT_ABSCOUNTER_WERT`, `STAT_ABSCOUNTER_TEXT`,
  `STAT_SLAKLRCOUNTER_WERT`, `STAT_SLAKLRCOUNTER_TEXT`, `STAT_TRANSPORTCOUNTER_WERT`,
  `STAT_TRANSPORTCOUNTER_TEXT`, `STAT_TIMEOUT_PWRMNG_WERT`, `STAT_TIMEOUT_PWRMNG_TEXT`,
  `STAT_TIMEOUT_KLAPPENST_WERT`, `STAT_TIMEOUT_KLAPPENST_TEXT`, `STAT_TIMEOUT_STANDVER_WERT`,
  `STAT_TIMEOUT_STANDVER_TEXT`, `STAT_TIMEOUT_KLEMMENST_WERT`, `STAT_TIMEOUT_KLEMMENST_TEXT`,
  `STAT_COUNTER_NACHLAUF_ECU_WERT`, `STAT_COUNTER_NACHLAUF_ECU_TEXT`.
- `STATUS_AD_WERTE_LESEN`: `JOB_STATUS`, `STAT_SPANNUNG_D_KL30_WERT`,
  `STAT_SPANNUNG_A_KL30_WERT`, `STAT_SPANNUNG_D_KL15_WERT`, `STAT_SPANNUNG_A_KL15_WERT`,
  `STAT_SPANNUNG_D_R1IN_WERT`, `STAT_SPANNUNG_A_R1IN_WERT`, `STAT_SPANNUNG_D_R1OUT_WERT`,
  `STAT_SPANNUNG_A_R1OUT_WERT`, `STAT_SPANNUNG_D_R1OFF_WERT`, `STAT_SPANNUNG_A_R1OFF_WERT`,
  `STAT_SPANNUNG_D_R2IN_WERT`, `STAT_SPANNUNG_A_R2IN_WERT`, `STAT_SPANNUNG_D_R2OUT_WERT`,
  `STAT_SPANNUNG_A_R2OUT_WERT`, `STAT_SPANNUNG_D_R2OFF_WERT`, `STAT_SPANNUNG_A_R2OFF_WERT`,
  `STAT_SPANNUNG_D_EINH`, `STAT_SPANNUNG_A_EINH`. The SGBD's own result descriptions do not
  all match the names (for example `STAT_SPANNUNG_A_KL30_WERT` is described as `Spannung Kl 15
  in V`); quote them as returned and do not decide which is right.

