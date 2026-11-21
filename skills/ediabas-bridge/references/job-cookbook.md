# Job cookbook — jobs confirmed on Ryan's E61

**Status: NO job has been run against the car yet.** The lists below are what
`ediabas_describe` returned at the desk on 2026-11-21, with the cable in the VM and not in the
car: job names, their descriptions from the SGBD, and the bridge's policy verdict. They say
which jobs exist and which the bridge will run. They do not record arguments or result fields,
because none of these jobs has been run; those come from `ediabas_describe` with `job` and from
Phase 6. Do not fill them in from the name or the description.

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
(`durationMs` 14745). Cable in the VM, not in the car.

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
| `C_FG_LESEN` | `allow.global` | `Fahrgestellnummer lesen` — `KWP2000: $1A ReadECUIdentification` | read the VIN. Its result is the car's VIN: never write it into a file here or quote it beyond what Ryan asks for |
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

`ediabas_describe` on 2026-11-21: `variant` `TMODE`, 25 jobs (`durationMs` 1521). `TMODE`
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
