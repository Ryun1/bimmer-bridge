# Spike: making sure the bridge never writes (2026-11-24)

**Question:** what would *ensure* that nothing the bridge sends can write to, actuate or
reprogram a module, rather than making it unlikely?

**Answer in one line:** a gate on the bytes, at the one place every telegram passes, that allows
read services by service ID and anything else only as an exact, reviewed telegram. Names and
descriptions can't give that guarantee; the VM backend can get it from a small EDIABAS
interface-handler shim that only the bridge uses.

## 1. What protects us today, and why it is not a guarantee

| Layer | Decides on | Gap |
|---|---|---|
| Job policy (name rules) | the job name | a `STATUS_` name says nothing about what is sent |
| Description screen | the SGBD's own description | `ACSM60 STATUS_ROC` says "$22 Read" and sends `$2E` twice |
| `deny.ecuJobs` (policy v5) | a list from `dev/sgbd-sweep.ts` | a deny-list: only what the sweep could see; about 10,700 allowed jobs (none on the E61's modules) build requests the static replay cannot follow |
| Native engine frame gate (`kline/src/gate.zig`) | each outgoing telegram | a **deny-list** of service IDs, so `$A0`–`$AF` (BMW-specific), OBD mode `$04` (clear emission faults), UDS `$38`, `$86`, `$87` and all DS2 pass; and it exists only on the native backend |

Every layer above the bytes trusts the SGBD, and every layer at the bytes is a deny-list.

## 2. What the bytes look like on the car's modules

The rebuilt sweep replayed every policy-allowed job on the E61's 43 modules (1,301 jobs).
A strict read-only allow-list ($12, $13, $17, $18, $19, $1A, $21, $22, $23, $3E, and $10 only
with the default session 81) would block 316 of them:

| Service | Jobs blocked | What they are |
|---|---|---|
| `$30` input/output control | 264 | mostly "report current state" reads (option 01), but **not all**, see below |
| `$31` start routine | 39 | many reads (CAS history, PGS TAGE data, NVE pairing), but also `ALBV60` "Standard Codierjob" routines, `PGS_65_2 STATUS_ECU_SELBSTTEST`, `STATUS_PGS_ANTENNEN_TEST` |
| `$A6` (BMW-specific) | 6 | `LM_AHL` adaptive headlight motor (SMC) status |
| `$33` routine results | 6 | `VID_65_2` test picture / self-test, `AFS_90`, `CCC_60` |
| `$10` other session | 1 | `GS19B STATUS_RAUS` |

`$30` cannot be judged by its option byte either: BMW SGBDs disagree on the layout (`VID_65_2`
and `ANT_60` put `01` ReportCurrentState *before* the identifier, ISO puts it after), and some
policy-allowed `STATUS_` jobs on the car's own modules use options that actuate:

- `IHKA60 STATUS_SELBSTTEST_SCHRITTMOTORE`: `30 09 07` ("Prüfen Selbsttest der Schrittmotoren"; 07 = short-term adjustment)
- `AFS_90 STATUS_EEPROM_*`: `30 0x 08` (08 = long-term adjustment under ISO; described as EEPROM reads)
- `AFS_90 STATUS_FSP_*_CONTROLFELD`, `AMP_60 STATUS_GERAETE_TEMPERATUR`: `30 xx 07 …`

These pass the policy and the description screen today. None was run on 2026-11-23.

**Conclusion:** read services can be allowed by service ID; `$30`, `$31`, `$33`, manufacturer
services and DS2 can only be allowed as **exact telegrams someone reviewed** (the target address
byte aside), e.g. `30 91 01 01` for `NVE_65 STATUS_BORDNETZSPANNUNG`.

## 3. Where a byte gate can sit on the VM backend

The IFH trace (`IfhTrace = 3` in the VM's `EDIABAS.INI`) shows EDIABAS's layers:

```
api32.dll → ebas32.exe → interface handler (IFH)  "STD:OBD" = XStd32.dll
            dllCallIFH(MESSAGE in, MESSAGE out), function numbers below
                         → device driver OBD32.dll (INITIALIZE, WRITEDATA, READDATA, …) → cable
SENDECUTEL  INPUT: B8 12 F1 02 1A 80        ← the whole telegram, as the SGBD built it
            I -> D: 06 09 00 B8 12 F1 02 1A 80
```

Every BEST instruction that reaches the cable goes through `dllCallIFH` with a function number
(EdiabasLib `IfhLog/IfhLog.cpp`): 29 `ifhSendTelegram` (xsend), 30/31 frequent telegrams,
34 repeat, 41 `ifhDownload`, 44/45/50 raw mode and raw sends, 38 programming voltage,
42 SI relay, 36 set port, 27 `ifhSetParameter` (the concept). So a shim IFH sees every byte the
bridge's EDIABAS could send, whatever SGBD, job or group name produced it.

Options considered:

| Option | Strength | Cost / risk |
|---|---|---|
| **A. Shim IFH `XGate32.dll`** forwarding to `XStd32.dll`; the agent opens it with `apiInitExt("GATE:OBD", …)` (exported by the VM's api32.dll as `__apiInitExt`, 5 args) | prevents; in-path for everything the bridge sends; INPA, NCS Expert and Tool32 keep `STD:OBD` untouched | a Win32 x86 DLL (Zig can cross-compile it, as `kline/` already uses Zig); the `GATE:` → `XGate32.dll` naming and the `MESSAGE` layout must be confirmed in the VM first |
| B. Rename `XStd32.dll` → `XStd32Org.dll` and shim it (IfhLog's own method) | prevents; verified method | global: would also gate INPA and NCS Expert, so manual coding stops working while it is in place |
| C. Serial proxy (com0com) between `OBD32.dll` and the FTDI port | prevents, below EDIABAS | must reproduce K+DCAN timing, echo and modem-line mode switching; fragile |
| D. Replace BMW EDIABAS in the agent with EdiabasLib and gate in C# | prevents; same code as the native engine | the native engine's comparison work (M1/M2) is exactly the open question of EdiabasLib parity |
| E. **Verify after**: the agent reads the new IFH trace lines after each request and checks every `SENDECUTEL` | proves what was sent; latches the bridge shut on the first bad telegram | does not stop the first one; needs IfhTrace on (the 2026-11-23 session was **not** traced) |

Licence: EdiabasLib (and `IfhLog`) is GPL-3.0; `main` is MIT. Option A can be written from the
ABI alone (function numbers and signatures are facts), or the shim can live with the GPL
native-engine code.

## 4. Recommendation

1. **Now, small (agent only): option E.** After each request the agent parses the IFH trace
   segment it caused, classifies every sent telegram with the rule from section 2, records a
   per-session telegram ledger in the audit log, and on anything not allowed latches every
   `ediabas_*` tool shut (`[telegram_not_allowed]`) until the user looks. This also answers
   "did we write?" from evidence after every session. It needs `IfhTrace = 3` while the bridge
   runs (the owner's setting in `EDIABAS.INI`, or per session through `apiSetConfig` if
   EDIABAS honours it at run time: to check).
2. **The guarantee: option A**, with the same rule, failing closed:
   - telegram functions (29, 30, 31, 34): read services by ID; anything else only as an exact
     telegram on a reviewed list (address byte ignored); `$10` only with 81;
   - concept and status functions (1–5, 8, 11–14, 20–24, 27, 28, 32, 33, 35, 37, 40, 43, 47–49,
     51, 54): allowed, recorded;
   - everything else (25, 26, 36, 38, 39, 41, 42, 44, 45, 50, unknown numbers): refused;
   - a refusal latches until `ifhEnd`, and the agent turns it into an error for the whole
     request, as `ediabasd` does with `frame_refused`.
   First step: a log-only build in the VM (IfhLog-style) to confirm the IFH name mapping and
   `MESSAGE` layout and record which function numbers a real read session uses.
3. **Native engine:** change `kline/src/gate.zig` from its deny-list to the same allow-list, so
   both backends enforce one rule (`$A6`, OBD `$04`, DS2 currently pass).
4. **One allow-list file**, shared like `policy/policy.json`: the read services, plus exact
   telegrams for reviewed `$30`/`$31` reads (the sweep generates candidates per module; the
   owner approves, as with `reviewedJobs`). The gate is then the guarantee and the name policy
   is only a convenience that refuses early with a clear message.
5. **Policy follow-up regardless:** the actuating `STATUS_` jobs in section 2 (`IHKA60`
   self-test, `AFS_90` EEPROM, `PGS_65_2` self-test and antenna test, `ALBV60` coding routines)
   are allowed today. Decide whether to deny them now or wait for the gate.

## 5. Not done in this spike

- No DLL was built or installed: that touches the VM's BMW install and is the owner's call.
- `apiSetConfig("IfhTrace")` at run time, the `GATE:` naming, and the `MESSAGE` struct layout
  for this EDIABAS version (7.3.0) are unverified.
- DS2: not checked which of the E61's modules (if any) use DS2; the sweep does not classify
  DS2 frames. The gate must refuse DS2 frames unless they are on the exact list.
