# ECU map — a 2005 E61 550i

*Recorded on a 2005 E61 550i: other cars' ECUs and jobs differ, so check yours with
`ediabas_list_ecus` and `ediabas_describe`.*

**Status: the modules below were confirmed from the car on 2026-11-23** by `E60`
`IDENT_FUNKTIONAL` (recording `0029`, fixture `dev/fixtures/battery`): 43 modules answered
with a name, SGBD, group and address. The older desk entries (2026-11-21/22, cable in the VM,
not in the car) show only that a file exists in the VM and what `ediabas_describe` read from it.

This file holds only what a real session returned: an SGBD or group name that answered on this
car, and what it covers. Nothing from memory, forums or the web goes in, however likely it
looks, because a wrong SGBD name costs ignition-on time and can mislead a diagnosis. Until an
entry is confirmed by a read, find names with `ediabas_list_ecus` and check them with
`ediabas_describe` before relying on them.

Entries are added in the same commit as the recorded session (fixtures) they came from.

## Entry format

| Module | Name (`prg` / `grp`) | Resolved variant | Covers | Confirmed (date, how) |
|---|---|---|---|---|
| e.g. the module as the owner names it | the name passed as `ecu` | the `variant` the tool reported | what its fault memory / jobs cover | date, tool and result that confirmed it |

## Entries

| Module | Name (`prg` / `grp`) | Resolved variant | Covers | Confirmed (date, how) |
|---|---|---|---|---|
| Night vision | `NVE_65` (`prg`) | `NVE_65` | Night vision: its job list includes fault memory, info memory, ident and `STATUS_NIVI`, `STATUS_PAIRING` (see `job-cookbook.md`) | 2026-11-21: listed by `ediabas_list_ecus` with filter `nve`; `ediabas_describe` returned 59 jobs (cable in the VM, not in the car); verdicts recorded again under policy version 4 on 2026-11-22. `FS_LESEN` and `IS_LESEN` read from the car on 2026-11-23 (both empty); `IDENT_FUNKTIONAL` lists it at address 0x57. |
| Night vision (group) | `D_NVE` (`grp`) | **not checked** | not checked | 2026-11-21: listed by `ediabas_list_ecus` with filter `nve`, nothing more. `IDENT_FUNKTIONAL` on 2026-11-23 lists `NVE_65` with group `D_NVE`. |
| Diagnostic interface (the cable), not a car module | `TMODE` | `TMODE` | The interface itself: its runnable jobs read the interface type and version and the KL30 / KL15 voltages it sees (see `job-cookbook.md`) | 2026-11-21: `ediabas_describe` returned 25 jobs; the same five runnable under policy version 4 (2026-11-22). Loads with no cable connected at all (seen at the desk the same day). |
| Utility SGBD, not a car module | `UTILITY` | not recorded | Supply voltage (`STATUS_UBATT`) and terminal 15 (`STATUS_ZUENDUNG`), the two reads `ediabas_vehicle_status` sends (see `job-cookbook.md`) | 2026-11-21: loaded with no cable connected at all (seen at the desk). 2026-11-22: `ediabas_describe` returned 10 jobs, 3 runnable (cable in the VM, not in the car). |

Modules from `E60` `IDENT_FUNKTIONAL` on 2026-11-23 (the name column is the SGBD to pass as `ecu`; the group is `ECU_GRUPPE`):

| Module | Name (`prg` / `grp`) | Resolved variant | Covers | Confirmed (date, how) |
|---|---|---|---|---|
| Aktive Lehnenbreitenverstellung Fahrer | `ALBV60F` (`prg`) | `ALBV60F` | group `D_ALBVF`; address 0x59 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Karosserie Gatewaymodul | `KGM_60` (`prg`) | `KGM_60` | group `D_ZGM`; address 0x00 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Zentrale Bedieneinheit High | `ZBEH60` (`prg`) | `ZBEH60` | group `D_EC`; address 0x67 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Aktive Lehnenbreitenverstellung Beifahrer | `ALBV60B` (`prg`) | `ALBV60B` | group `D_ALBVB`; address 0x5A | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Car Access System 2 / 3 | `CAS` (`prg`) | `CAS` | group `D_CAS`; address 0x40 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Schaltzentrum Lenksäule | `SZL_60` (`prg`) | `SZL_60` | group `D_SZL`; address 0x02 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Passive Go System | `PGS_65_2` (`prg`) | `PGS_65_2` | group `D_PGS`; address 0x27 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Airbag-Elektronik | `ACSM60` (`prg`) | `ACSM60` | group `D_SIM`; address 0x01 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Motorelektronik ME 9  8 Zylinder N62 TUE | `N62_TUE` (`prg`) | `N62_TUE` | group `D_MOTOR`; address 0x12 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Luftfeder | `EHC_E65` (`prg`) | `EHC_E65` | group `D_EHC`; address 0x38 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Multi Drive Schiebehebedach | `SD_KWP` (`prg`) | `SD_KWP` | group `D_SHD`; address 0x44 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Regen- Lichtsensor | `RLS_E65` (`prg`) | `RLS_E65` | group `D_RLS`; address 0x45 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Getriebesteuergerät GS19 B | `GS19B` (`prg`) | `GS19B` | group `D_EGS`; address 0x18 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Night Vision Steuergerät | `NVE_65` (`prg`) | `NVE_65` | group `D_NVE`; address 0x57 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Fernlicht Assistent | `FLA_65` (`prg`) | `FLA_65` | group `D_FLA`; address 0x5F | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Park Distance Control | `PDC_65_2` (`prg`) | `PDC_65_2` | group `D_PDC`; address 0x64 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Adaptive Cruise Control 2.Generation | `ACC2` (`prg`) | `ACC2` | group `D_ACC`; address 0x21 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Schaltzentrum Mittelkonsole | `SZM_60` (`prg`) | `SZM_60` | group `D_BZM`; address 0x65 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Heckklappenlift | `HKL_65_2` (`prg`) | `HKL_65_2` | group `D_HKL`; address 0x6B | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Dynamische Stabilitätskontrolle Plus mit AFS | `DXC8_P` (`prg`) | `DXC8_P` | group `D_DSC`; address 0x29 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Sitzmodul Fahrer | `FAS_PLX` (`prg`) | `FAS_PLX` | group `D_FAS`; address 0x6D | 2026-11-23, E60 IDENT_FUNKTIONAL |
| MOST/CAN-Gateway (im CCC) | `CCCG60` (`prg`) | `CCCG60` | group `D_MOSTGW`; address 0x62 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Sitzmodul Beifahrer | `BFS_PLX` (`prg`) | `BFS_PLX` | group `D_BFS`; address 0x6E | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Lichtmodul mit Adaptiver Lichtsteuerung | `LM_AHL` (`prg`) | `LM_AHL` | group `D_LM`; address 0x70 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Anhängermodul | `AHM_E65` (`prg`) | `AHM_E65` | group `D_AHM`; address 0x71 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Karosserie Basis Modul | `KBM_60` (`prg`) | `KBM_60` | group `D_KBM`; address 0x72 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Diebstahlwarnanlage | `DWA_E65` (`prg`) | `DWA_E65` | group `D_DWA`; address 0x41 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Zentrales Info Display | `CID_60` (`prg`) | `CID_60` | group `D_CID`; address 0x73 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Klimabedienteil | `IHKA60` (`prg`) | `IHKA60` | group `D_KLIMA`; address 0x78 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Intrumentenkombi | `KOMB60` (`prg`) | `KOMB60` | group `D_KOMBI`; address 0x60 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Aktiv Front Steering | `AFS_90` (`prg`) | `AFS_90` | group `D_AFS`; address 0x16 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Head-Up-Display | `HUD_60` (`prg`) | `HUD_60` | group `D_HUD`; address 0x3D | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Audio CD-Changer | `CDC_E65` (`prg`) | `CDC_E65` | group `D_CDC`; address 0x3C | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Telematic Control Unit | `TELE60_2` (`prg`) | `TELE60_2` | group `D_TEL`; address 0x36 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Videomodul Hybrid FBAS | `VID_65_2` (`prg`) | `VID_65_2` | group `D_VIDEO`; address 0x4B | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Antennentuner im CCC | `ANT_60` (`prg`) | `ANT_60` | group `D_ANTTU`; address 0x47 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Night Vision Kamera | `NVC_65` (`prg`) | `NVC_65` | group `D_NVC`; address 0x8B | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Top HIFI Verstärker | `AMP_60` (`prg`) | `AMP_60` | group `D_AMP`; address 0x37 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Audio System Kontroller (im CCC) | `ASK_60` (`prg`) | `ASK_60` | group `D_ASK`; address 0x3F | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Variabler Ventiltrieb Bank 1 8 Z. N62, 12 Zyl. N73 | `VVT_B1_0` (`prg`) | `VVT_B1_0` | group `D_VVT`; address 0x1B | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Car Comunication Computer | `CCC_60` (`prg`) | `CCC_60` | group `D_MMI`; address 0x63 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| CCC Applikation | `CCCA60` (`prg`) | `CCCA60` | group `D_CCC`; address 0xA0 | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Variabler Ventiltrieb Bank 2 8 Z. N62, 12 Zyl. N73 | `VVT_B2_0` (`prg`) | `VVT_B2_0` | group `D_VVT2`; address 0x1E | 2026-11-23, E60 IDENT_FUNKTIONAL |
| Engine (group; `ECU_NAME` in `IDENT_FUNKTIONAL`: see the `N62_TUE` row) | `D_MOTOR` (`grp`) | `N62_TUE` | `IDENT` and `FS_LESEN` answered (resolved by `IDENT`) | 2026-11-23, `ediabas_job` `D_MOTOR` `IDENT` (recording `0024`, 253 ms) |
| `PGS_65_2` group | `D_PGS` (`grp`) | `PGS_65_2` | `FS_LESEN` answered; same variant as in `IDENT_FUNKTIONAL` | 2026-11-23, `ediabas_read_faults` (recording `0018`) |
| `KBM_60` group | `D_KBM` (`grp`) | `KBM_60` | `FS_LESEN` answered | 2026-11-23, `ediabas_read_faults` (recording `0018`) |

`TMODE` and `UTILITY` are the only two names seen to load without the cable.

**Notes from the car, 2026-11-23:**

- `d_0012` gave `IFH-0009: NO RESPONSE FROM CONTROLUNIT` (recording `0018`): that name is not
  this car's engine group; use `D_MOTOR` (`N62_TUE`).
- `MPM_60` gave `IFH-0009` (two items, recording `0016`) and is not in the `IDENT_FUNKTIONAL`
  result (`0029`): it did not answer on this car.
- SINE (address 0x50) answers `FS_LESEN_FUNKTIONAL` (one code) but is not in `IDENT_FUNKTIONAL`. The VM has group `D_SINE` and SGBD `SINE_65` (desk, 2026-11-23); which variant answers is not recorded.
- `N62_TUE` (the engine file) does not load at the desk without the module answering (`SYS-0010`, 2026-11-23): describe its jobs with the ignition on.
- `VVT` (0x1B) and `VVT2` (0x1E) answer `FS_LESEN_FUNKTIONAL` with
  `ERROR_ECU_SERVICE_NOT_SUPPORTED` (recording `0031`).
- One `IDENT_FUNKTIONAL` set has no module name (44 sets, 43 named modules).

## Other job lists read at the desk, 2026-11-21

`ediabas_describe` also returned the full job lists of `E60`, `CAS`, `MPM_60`, `KOMB60`,
`KBM_60`, `LM_60`, `sgm_60_2` (variant `SGM_60_2`), `IHKA60`, `CCC_60`, `MASK60`, `DSC_60` and
`EKPM60_2`, with the cable in the VM and **not in the car**. That shows only that the files are
in the VM, not that any of them is fitted to this car. Their job counts and verdicts, recorded
again under policy version 4 on 2026-11-22, are in `job-cookbook.md`. `E60` allows the whole-car
jobs `IDENT_FUNKTIONAL` and `FS_LESEN_FUNKTIONAL` (since version 3); none of this has been read
from the car.

## Could not be described at the desk

`ediabas_describe` on these failed on 2026-11-21, with the cable in the VM and not in the car,
with `ediabas_error` 100 `SYS-0010: INITIALIZATION ERROR`, while the files above loaded. These
files appear to need the control unit to answer even to list their jobs, so they can only be
described at the car:

- `N62_TUE2`
- `ME9N62_2`
- `GS19D`
- `SZL_60`
- `D_MOTOR` (the engine group; 2026-11-22, `ediabas_describe` with `job` `IDENT`): a
  different error, `ediabas_error` 13 `IFH-0003: DATATRANSMISSION TO INTERFACE DISTURBED`. A
  group file picks its variant by asking the module.

The engine file is `N62_TUE` (`D_MOTOR` resolved to it on 2026-11-23). `N62_TUE2` and `ME9N62_2` are not this car's.

