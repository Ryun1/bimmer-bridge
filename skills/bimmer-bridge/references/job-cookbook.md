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

**Policy versions.** The job lists were first recorded under policy version 1 (2026-11-21/22).
Every verdict and count in this file was recorded again under **policy version 4** (agent and
Mac server 0.5.0) at the desk on 2026-11-22, cable in the VM and not in the car, with the five
pinned approvals listed below in place. The descriptions quoted did not change. If the policy
version in `vm_health` is newer than 4, go by what `ediabas_describe` returns, not by these
tables.

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

`ediabas_describe` on 2026-11-21: `variant` `NVE_65`, 59 jobs, about 15 s with comments
(`durationMs` 14745). Cable in the VM, not in the car. Under policy version 4 (2026-11-22,
`durationMs` 16545): 22 allowed, 21 runnable; the one change is `STATUS_PAIRING`, below.

**Allowed by the policy** (all runnable under version 4 except `STATUS_PAIRING`, which now needs review):

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
| `STATUS_PAIRING` | `allow.pattern` (**needs review** under version 4: `schaltet`, in `freigeschaltet`, matches the screen; refused with `job_needs_review` unless the owner approves it) | `Gibt den Pairing-Status aus zwischen ECU und CAM` (more lines below) | gives the pairing status between the ECU and the camera |
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

`ediabas_describe` on 2026-11-21: `variant` `TMODE`, 25 jobs (`durationMs` 1521); the same five
runnable under policy version 4 (2026-11-22, `durationMs` 891). `TMODE`
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

### `UTILITY` — a utility SGBD, not a car module

`ediabas_describe` on 2026-11-22 (policy version 4, cable in the VM, not in the car): 10 jobs,
3 runnable. `ediabas_vehicle_status` sends the two `STATUS_` jobs. Run so far only without the
car (below).

| Job | Verdict | Description as returned | Results declared | *(gloss)* |
|---|---|---|---|---|
| `INFO` | runnable | `Information SGBD` | not described on its own | information about the SGBD file |
| `STATUS_UBATT` | runnable | `Auslesen der Spannungsversorgung` | `JOB_STATUS` (string, `OKAY, wenn fehlerfrei`), `STAT_UBATT` (int, no unit declared) | read the supply voltage |
| `STATUS_ZUENDUNG` | runnable | `Auslesen der Klemme 15` | `JOB_STATUS` (string, `"OKAY","ERROR_UBATT"`), `STAT_ZUENDUNG` (int) | read terminal 15 (ignition) |

Refused: `INITIALISIERUNG`, `ABS_RELAIS`, `INTERFACE`, `TIME_AND_DATE`, `OGW_CAN_TEST`
(`ecu_restricted`), `IF_RESET` (`deny.substring:RESET`), `PARALLELE_CODIERUNG_EIN`
(`deny.substring:CODIER`). What `STAT_UBATT` and `STAT_ZUENDUNG` mean as numbers (units,
values for on and off) is not declared; it comes from Phase 6.

**Run with no car connected** (rehearsal 2026-11-22, cable in the VM, not in the car; fixture
`dev/fixtures/no-car`): `STATUS_ZUENDUNG` gave `STAT_ZUENDUNG=0`, `STATUS_UBATT` gave
`STAT_UBATT=1`, both `JOB_STATUS` `OKAY`. `TMODE` `LESE_SPANNUNG_KL30` gave `SPANNUNG`
`E02E0000` (12000 read little-endian) and `LESE_SPANNUNG_KL15` `00000000`, with no job status.
So `STAT_UBATT` and the KL30 reading come from the cable, not the car: they say nothing about
whether the car is awake or the battery's charge. Only a module's reading (a voltage source,
e.g. `NVE_65` `STATUS_BORDNETZSPANNUNG`) shows the car answered. In the same batch,
`NVE_65` `STATUS_BORDNETZSPANNUNG` gave `ediabas_error` 13 `IFH-0003`, as did every other job on a
real SGBD or group file (`E60`, `CAS`, `D_MOTOR`, `NVE_65` `FS_LESEN`).

### Whole-car and vehicle-order jobs (described 2026-11-22, not yet run)

Each described on its own with `ediabas_describe` and `job`, policy version 4, cable in the VM
and not in the car. All runnable. The result names are as the SGBD declares them.

| ECU | Job | Description (first line) | Arguments | Results declared |
|---|---|---|---|---|
| `E60` | `IDENT_FUNKTIONAL` | `Identdaten` — `KWP2000: $1A ReadECUIdentification` | `FUNKTIONALE_ADRESSE` (string, default `ALL`); `GRUPPENDATEI` (string, optional, not with `FUNKTIONALE_ADRESSE`) | per module: `ECU_ADR`, `ECU_GROBNAME`, `ID_SG_ADR`, `ECU_NAME`, `ECU_SGBD`, `ECU_GRUPPE`, `ID_BMW_NR`, `ID_HW_NR`, `ID_COD_INDEX`, `ID_DIAG_INDEX`, `ID_VAR_INDEX`, `ID_DATUM*`, `ID_LIEF_NR`, `ID_LIEF_TEXT`, `ID_SW_NR_MCV/FSV/OSV/RES`, `SERIENNUMMER`; plus `JOB_STATUS` |
| `E60` | `FS_LESEN_FUNKTIONAL` | `Fehlerspeicher lesen (alle Fehler / Ort und Art)` — `KWP2000: $18 ReadDiagnosticTroubleCodesByStatus` | `FUNKTIONALE_ADRESSE` (default `ALL`); `FEHLER_GRUPPE` (default `AG`, all fault groups) | per module: `ECU_ADR`, `ECU_GROBNAME`, `ID_SG_ADR`, `F_ANZ`, then `F_ORTi_NR` (fault code) and `F_ARTi_NR` (fault type) for i = 1..`F_ANZ`; plus `JOB_STATUS` |
| `CAS` | `C_FA_LESEN` | `Dieser Job liest den Fahrzeugauftrag` — `$22 ReadDataByCommonIdentifier $3F00 - $3F13` | none | `FAHRZEUGAUFTRAG` (string, up to 320 bytes), `SPEICHER_STATUS` (`BELEGT` / `UNBELEGT`), `JOB_STATUS` |
| `LM_60` | `C_FA_LESEN` | `Fahrzeugauftrag lesen` — `$22 ReadDataByCommonIdentifier $3F00 - $3F7F` | none | `FAHRZEUGAUFTRAG`, `SPEICHER_STATUS`, `JOB_STATUS` |
| `NVE_65` | `FS_LESEN` | `Fehlerspeicher lesen (alle Fehler / Ort und Art)` | none | `F_VERSION`, `F_HEX_CODE`, `F_ORT_NR`, `F_ORT_TEXT`, `F_SYMPTOM_NR/TEXT`, `F_READY_NR/TEXT`, `F_VORHANDEN_NR/TEXT`, `F_WARNUNG_NR/TEXT`, `JOB_STATUS` |

`FS_LESEN_FUNKTIONAL` declares fault codes and types as numbers only, with no text result:
the texts come from each module's own `FS_LESEN` (through `ediabas_read_faults`). `IDENT_FUNKTIONAL`'s
`SERIENNUMMER` and `C_FA_LESEN`'s `FAHRZEUGAUFTRAG` identify the car; the fixture scrubber
replaces `SERIENNUMMER`, but check `FAHRZEUGAUFTRAG` by hand when reviewing fixtures.

`D_MOTOR` (the engine group) could not be described at the desk: `ediabas_describe` with
`job` `IDENT` gave `ediabas_error` 13 `IFH-0003: DATATRANSMISSION TO INTERFACE DISTURBED`
(2026-11-22). A group file picks its variant by asking the module, so it needs the car.

## Battery-drain related jobs (desk, 2026-11-21/22, not yet run)

The job lists below were read with the cable in the VM and **not plugged into the car**; the
files are not known to be fitted to this car (see `ecu-map.md`). The single jobs were then
described one at a time with `ediabas_describe` and `job` on 2026-11-22. None has been run.

Only the jobs described one at a time are listed. For any other job on these files use
`ediabas_describe` with a `filter` (a job-name substring such as `STATUS_` or `KL30`).

Full job lists, **policy version 4**, 2026-11-22 (agent 0.5.0, with the five pinned approvals
below). "Runnable" counts jobs that passed the screen and approved ones; "not checked" means the
60 s describe limit ran out before their descriptions were read, so their verdict is unknown
until they are described one at a time. The version 1 counts of 2026-11-21 are in this file's
git history.

| File | Jobs | Allowed | Runnable | Needs review | Not checked | Full describe (`durationMs`) |
|---|---|---|---|---|---|---|
| `E60` | 26 | 3 (`INFO`, `IDENT_FUNKTIONAL`, `FS_LESEN_FUNKTIONAL`) | 3 | 0 | 0 | 3074 |
| `CAS` | 150 | 63 | 39 (2 of them approved) | 4 | 20 | 60226 (the limit) |
| `MPM_60` | 57 | 21 | 19 (1 approved) | 2 | 0 | 14980 |
| `KOMB60` | 128 | 37 | 37 | 0 | 0 | 58268 |
| `KBM_60` | 79 | 24 | 22 | 2 | 0 | 25499 |
| `LM_60` | 134 | 38 | 36 | 2 | 0 | 60036 |
| `sgm_60_2` (variant `SGM_60_2`) | 80 | 24 | 24 | 0 | 0 | 35216 |
| `IHKA60` | 119 | 33 | 29 | 4 | 0 | 55740 |
| `CCC_60` | 87 | 30 | 29 | 1 | 0 | 31766 |
| `MASK60` | 91 | 25 | 24 | 1 | 0 | 33885 |
| `DSC_60` | 95 | 21 | 21 | 0 | 0 | 39944 |
| `EKPM60_2` | 62 | 22 | 22 | 0 | 0 | 18032 |

`CAS` with filter `STATUS` (54 jobs, `durationMs` 60082, the limit): 47 allowed, 30 runnable
(2 approved), 6 needs review, 11 not checked, 7 refused by name. Its full list reached the limit
with 20 jobs not checked, among them `STATUS_WAKEUP_HISTORY`, `STATUS_WUP` and `STATUS_PA_WUP`;
described one at a time (below) all three are runnable.

**Needs review under policy version 4**, with the screen term each description matched (found by
checking the recorded description against `policy/policy.json` the way the agent does: the
ignored words `beschreibung` and `umsetzer` replaced by a space, then each term looked for
anywhere, ignoring case; an empty or label-only description needs review too). The `CAS` rows
combine the full list (4) and the `STATUS` filter (6): `STATUS_RDL`, `STATUS_RDU` and
`STATUS_THS_HANDSENDER1` were not checked in the full list.

| File | Job | Matched |
|---|---|---|
| `NVE_65` | `STATUS_PAIRING` | `schaltet` (in `freigeschaltet`) |
| `CAS` | `STATUS_CC_DATEN_ARG`, `STATUS_FNDCOM_FUNKTION`, `STATUS_RDL`, `STATUS_RDU` | `start` |
| `CAS` | `STATUS_ELV_HISTORY_AUS_BUFFER` | `write` |
| `CAS` | `STATUS_MECHANISCHER_SCHLUESSELCODE`, `STATUS_THS_HANDSENDER1` | no description |
| `MPM_60` | `STATUS_CAN_INPUT_LESEN`, `STATUS_ECUHWVERSION_LESEN` | no description |
| `KBM_60` | `STATUS_CFL_150N`, `STATUS_DENORM_REASON` | `start` (`StartRoutineByLID`) |
| `LM_60` | `STATUS_XENON_AL_EINSCHALTVERSUCHE` | `einschalt` |
| `LM_60` | `STATUS_LAMPEN_KURZSCHLUSS_COUNTER_MAX` | no description |
| `IHKA60` | `STATUS_BEDIENTEIL` | `einstell` |
| `IHKA60` | `STATUS_IO` | `ansteuer` |
| `IHKA60` | `STATUS_CAR_KEY_EINSTELLUNGEN` | `program` |
| `IHKA60` | `STATUS_VERDAMPF_MIN_ANHEBUNG` | `schreib` |
| `CCC_60` | `STATUS_FREISCHALTUNG` | `start` |
| `MASK60` | `STATUS_FLOTTENMODUS` | `aktivier` |

These are what the screen flags, not a judgement that the jobs write anything: the owner decides,
and the safe default is to skip them.

Below, "Description as returned" quotes the first line and the service line (`KWP2000: ...`),
if any, after " — "; tabs in the SGBD's text are shown as one space. "In the job list" is what
the full list showed for the same job. Every job here takes no arguments (`arguments` was
empty for all of them). Result names are listed as declared, in the SGBD's order.

**How the screen got here.** Version 2 (agent 0.2.2) made the screen ignore the words
"Beschreibung" and "Umsetzer" before matching, so the label "Jobbeschreibung:" no longer flags a
job by itself; version 3 added more terms, and version 4 added switching and triggering terms
(`schaltet`, `einschalt`, `ausschalt`, `umschalt`, `ausloes`, `auslös`, `trigger`, `oeffnen`,
`öffnen`). That is why some jobs flagged under version 1 pass now, and why `NVE_65`
`STATUS_PAIRING` and `LM_60` `STATUS_XENON_AL_EINSCHALTVERSUCHE` are flagged under version 4.

**Approved by the owner on 2026-11-22, pinned the same day for 0.5.0.** Five jobs below (`CAS`
`STATUS_WAKEUP_HISTORY`, `STATUS_KLEMMENVERHINDERER`, `STATUS_KL15_ABSCHALTUNG`, `STATUS_WUP`;
`MPM_60` `STATUS_AD_WERTE_LESEN`), flagged when they were first described, are listed in
`reviewedJobs` in the VM's `C:\ProgramData\EdiabasBridge\config.json` as pinned
`ECU:JOB@<hash>` lines, and `ediabas_describe` of each returns review `reviewed` under policy
version 4: the bridge on that VM runs them. A pin holds only while the job's description stays
the same. The approval lives only in that VM config, not in the repo; after a fresh install the
list is empty and they are refused again until the owner adds them back. Under policy version 4
the screen still flags three of them; `STATUS_WUP` and `STATUS_AD_WERTE_LESEN` match no term
now and would run without the approval.

**Flagged by the screen, approved by the owner.** Without the pinned approval the bridge would
refuse these (`job_needs_review`). The trigger is a screen term from `policy/policy.json`
`descriptionScreen.terms` found in the description (case-insensitive, anywhere in a word).

### `CAS`

Single-job describes under policy version 4 took 5.0–7.4 s each (`durationMs` 4961–7379).

| Job | Rule (review) | Description as returned | In the full job list (version 4) | Screen terms matched (version 4) |
|---|---|---|---|---|
| `STATUS_PA_WUP` | `allow.pattern` (review passed) | `Lesen des PA Wakeup Pattern` | no description, `not_checked` | none (runnable) |
| `STATUS_WAKEUP_HISTORY` | `allow.pattern` (**reviewed**: approved) | `Jobbeschreibung: This Job reads the history trace of the LPM application` — `KWP2000: $31 Start Routine By Local Identifier` | no description, `not_checked` | `start` (in `Start Routine`) |
| `STATUS_KLEMMENVERHINDERER` | `allow.pattern` (**reviewed**: approved) | `Jobbeschreibung: Der Job dient zum Auslesen der internen Verhinderungsgründe` — `KWP2000: $21 ReadDataByLocalIdentifier` | same description, reviewed | `einschalt`, `ausschalt` (in `Einschalten`, `Ausschalten`; version 4 terms) |
| `STATUS_KL15_ABSCHALTUNG` | `allow.pattern` (**reviewed**: approved) | `Jobbeschreibung: Status verschiedener Abschaltverhinderer` — `KWP2000: $31 StartRoutineByLocalIdentifier` | same description, reviewed | `start` (in `StartRoutineByLocalIdentifier`) |
| `STATUS_WUP` | `allow.pattern` (**reviewed**: approved) | `Jobbeschreibung: Dieser Job liest den aktuellen Status "Wake-Up-Pattern"` — `KWP 2000:$21 ReadDataByLocalIdentifier` | no description, `not_checked` | none under version 4 (version 1 flagged `schreib` in `Jobbeschreibung`, which the screen has ignored since version 2): it would pass without the approval |

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

Single-job describes took about 2 s each (`durationMs` 1921–2084; 2253–2343 under policy version 4, verdicts unchanged).

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

Single-job describes took 1.6 s and 2.1 s (`durationMs` 1584, 2149); under policy version 4 both still review passed.

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
(`durationMs` 2705); under policy version 4 still review passed.

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

Single-job describes took about 1.3 s each (`durationMs` 1292, 1380), and 1.6 s under policy version 4.

| Job | Rule (review) | Description as returned | In the full job list (version 4) | Screen terms matched (version 4) |
|---|---|---|---|---|
| `STATUS_ECU_LESEN` | `allow.pattern` (review passed) | `es wird der aktuelle Status der ECU ausgelesen` (no service line) | same, review passed | none (runnable) |
| `STATUS_AD_WERTE_LESEN` | `allow.pattern` (**reviewed**: approved) | `es werden die Messwerte des AD-Umsetzers ausgelesen` (no service line) | same, reviewed | none under version 4 (version 1 flagged `setz` in `AD-Umsetzers`; the screen has ignored `Umsetzer` since version 2): it would pass without the approval |

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

