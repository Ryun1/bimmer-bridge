# Native engine M0: desk session results

Plan: [M0 plan](../superpowers/plans/2026-11-22-native-engine-m0.md). Design:
[native engine](../superpowers/specs/2026-11-22-native-engine-design.md), "Milestones and
acceptance", M0.

M0 answers, without the car, the questions that decide M1 and M3: which protocol concepts the
SGBDs use, whether any unimplemented opcode occurs, whether real EDIABAS runs INIT for `_JOBS`,
and whether the K+DCAN cable can be opened, locked and configured from macOS and Linux.

**Nothing in this session touches the car.** The cable is unplugged from the car throughout.
`kline loopback` needs a bench 12 V supply, never a car. `EDIABAS.INI` is edited by Ryan by
hand only (safety rule 8).

Who does what: Ryan does sections 1 and 4–8. Claude runs sections 2–3 once `~/ediabas-ecu`
exists and fills in their tables (counts and names, never SGBD contents). Section 9 is decided
together. The census JSON stays outside the repo; whether to commit a copy is Ryan's call.

Status: sections 2–3 done (2026-11-22); **waiting for the rest of the desk session.**

---

## 1. SGBD copy (Ryan)

Copy `C:\EDIABAS\Ecu` from the VM to `~/ediabas-ecu` on the Mac, for example through a UTM
shared folder. Then compare counts and hashes on both sides.

Mac:
```sh
ls ~/ediabas-ecu | wc -l
shasum -a 256 ~/ediabas-ecu/E60.prg ~/ediabas-ecu/NVE_65.prg ~/ediabas-ecu/TMODE.prg
```
(The Mac lookup is case-insensitive in the tools, but `shasum` needs the names as they are on
disk; use `ls ~/ediabas-ecu | grep -i -E '^(e60|nve_65|tmode)\.prg$'` if they differ.)

VM (PowerShell):
```powershell
(Get-ChildItem C:\EDIABAS\Ecu -File).Count
Get-FileHash C:\EDIABAS\Ecu\E60.prg,C:\EDIABAS\Ecu\NVE_65.prg,C:\EDIABAS\Ecu\TMODE.prg
```

| | Mac | VM | Match |
|---|---|---|---|
| File count | | | |
| `E60.prg` SHA-256 | | | |
| `NVE_65.prg` SHA-256 | | | |
| `TMODE.prg` SHA-256 | | | |

## 2. Concept census (Claude, after section 1)

Runs each `.prg`'s `INITIALISIERUNG` against an in-memory capture interface that records the
`xsetpar` parameters and stops at the first call that would send anything. Nothing opens a
port.

```sh
dotnet run --project dev/census -- concepts ~/ediabas-ecu --json ~/ediabas-ecu-census.json
```

Run 2026-11-22 on Ryan's folder (`~/ediabas-ecu/Ecu`, 1,251 `.prg`), JSON at
`~/ediabas-ecu-census.json` (outside the repo).

Tally (concept → files, bauds):

| Concept | Name | Files | Bauds |
|---|---|---|---|
| 0x010F | BMW-FAST | 501 | 115200 |
| 0x0006 | DS2 | 367 | 9600 |
| 0x0110 | D-CAN | 243 | 500000 |
| 0x0001 | concept 1 (5-baud init; out of scope) | 47 | 9600 |
| 0x0002 | concept 2, ISO 9141 / KWP1281 (5-baud init; out of scope) | 32 | 0, 4800 |
| 0x010D | KWP2000\* | 28 | 9600 |
| 0x0005 | DS1 | 10 | 0, 9600 |
| 0x0003 | concept 3 (5-baud init; out of scope): `IHKA`, `IHKA31` | 2 | 0 |
| 0x010C | KWP2000 BMW (fast init): `mrrbt` only | 1 | 10400 |
| 0x000E | not implemented in EdiabasLib (IFH-0014): `ZUHEIZ_B` | 1 | 9600 |
| 0x000F | not implemented in EdiabasLib (IFH-0014): `asc_l22` | 1 | 10400 |
| `no_xsetpar` | INIT set no parameters: tool and utility SGBDs (`TMODE`, `UTILITY`, `ADS`, `EDIC`, `FA`, `GRPLISTE`, `IFR`, …) | 17 | |
| `job_error` | an EDIABAS error ended INIT before it set any: concept unknown, look at each | 0 | |
| `error` | could not be resolved or parsed: `00MDS410.prg`, a 2-byte file (also 2 bytes in the zip made from the VM's folder, dated 2014) | 1 | |
| **total** | | **1,251** | |

The folder is the whole EDIABAS SGBD set, not only this car's modules, so the tally covers every
BMW of the era. The modules in [at-the-car.md](../at-the-car.md):

| SGBD | Status | Concept | Baud | Parameters |
|---|---|---|---|---|
| `NVE_65` | ok | 0x010F BMW-FAST | 115200 | 271, 115200, 800, 20, 10, 2, 5000 |
| `E60` | ok | 0x010F BMW-FAST | 115200 | 271, 115200, 1200, 20, 10, 2, 5000 |
| `CAS` | ok | 0x010F BMW-FAST | 115200 | 271, 115200, 1200, 20, 10, 2, 5000 |
| `LM_60` | ok | 0x010F BMW-FAST | 115200 | 271, 115200, 1400, 20, 10, 2, 5000 |
| `N62_TUE2` | ok | 0x010F BMW-FAST | 115200 | 271, 115200, 1200, 20, 10, 2, 5000 |
| `ME9N62_2` | ok | 0x010F BMW-FAST | 115200 | 271, 115200, 1200, 20, 10, 2, 5000 |
| `GS19D` | ok | 0x010F BMW-FAST | 115200 | 271, 115200, 1200, 20, 10, 2, 5000 |
| `SZL_60` | ok | 0x010F BMW-FAST | 115200 | 271, 115200, 800, 20, 10, 2, 5000 |
| `D_MOTOR` (group) | not run: a group resolves to its variant only at the car | | | |

Concept numbers, for reading the tally: 0x0006 DS2, 0x010C KWP2000 BMW (fast init), 0x010D
KWP2000\*, 0x010F BMW-FAST, 0x0110 D-CAN.

**Decision: does any SGBD use 0x010C (KWP2000 BMW, fast init)?** One file only, `mrrbt.prg`,
which is not one of this car's modules. Every module listed for the E61 is BMW-FAST at
115200. Proposed: fast init drops out of M2, and section 8 (break width) is not needed.

The first run counted 1,169 `no_xsetpar`, because the capture interface stopped at `xstopf`,
which nearly every BMW INIT calls before `xsetpar`. On K+DCAN, `xstopf` only stops EdiabasLib's
own comm thread and sends nothing (`EdInterfaceObd.cs:2179-2222`), so the capture interface
now records it and carries on. The figures above are from the corrected run.

### Dry run on upstream test SGBDs (2026-11-22)

On a scratch copy of only `cmd_test1.prg`, `cmd_test2.prg`, `base1.prg`, `base2.prg` and
`cmd_ident.grp` (upstream's BMW-derived test files were not used). None of these files' INIT
jobs sets parameters, so every row is `no_xsetpar`, and the `.grp` is correctly skipped. Exit
code 0.

```
FILE                     STATUS     CONCEPT  BAUD    PARAMS
base1.prg                no_xsetpar -        -
base2.prg                no_xsetpar -        -
cmd_test1.prg            no_xsetpar -        -
cmd_test2.prg            no_xsetpar -        -

TALLY
no_xsetpar       4
total            4
```

## 3. Opcode census (Claude, after section 1)

Disassembles every `.prg` and `.grp` with upstream's BESTDIS (from a temporary copy, so nothing
is written into the Ecu folder) and flags EdiabasLib's 16 unimplemented opcodes: `jtsr`, `ret`,
`tosp`, `xdownl`, `xstoptr`, `xsendr`, `xrecv`, `xinfo`, `xparraw`, `pcall`, `xopen`, `xclose`,
`xcloseex`, `xswitch`, `xsendex`, `xrecvex`.

```sh
dotnet run --project dev/census -- opcodes ~/ediabas-ecu
```

Run 2026-11-22 on `~/ediabas-ecu/Ecu` (2 min 36 s); JSON at `~/ediabas-ecu-opcodes.json`.

| | Result |
|---|---|
| Files disassembled | 1,603 of 1,604 (1,251 `.prg`, 353 `.grp`); 146 distinct mnemonics, 90,289,714 instructions |
| Errors (file: reason) | `00MDS410.prg`: 2 bytes, shorter than the header |
| Unimplemented opcodes found (file: mnemonic) | none |

**Decision: none, or a list of opcodes for M3:** none. No SGBD in the folder uses any of
EdiabasLib's 16 unimplemented opcodes.

### Dry run on upstream test SGBDs (2026-11-22)

Same five files. 143 distinct mnemonics, 45,930 instructions; no unimplemented opcode, no
errors; exit code 0. The top of the table and the interface opcodes:

```
MNEMONIC     COUNT
clear        11677
move          8009
pushf         7544
popf          6232
jae           1001
ergs           750
push           727
pop            718
jt             698
jnz            665
…
xstate 4, xbatt 2, xignit 2, xkeyb 2, xreset 2, xsend 2, xtype 2, xvers 2,
xconnect 1, xhangup 1, xraw 1, xrequf 1, xsetpar 1

UNIMPLEMENTED IN EDIABASLIB: none
ERRORS: none
```

## 4. Does real EDIABAS run INIT for `_JOBS`? (Ryan, in the VM, by hand)

The cable stays **unplugged from the car**. This settles whether `NoInitForVJobs = true` (what
the native describe path uses) matches real EDIABAS.

1. Back up `C:\EDIABAS\Bin\EDIABAS.INI`, then set `IfhTrace = 3` in it, by hand.
2. In Tool32, load `NVE_65` and run `_JOBS`.
3. Read `C:\EDIABAS\TRACE\ifh.trc` (Claude can fetch it with `vm_logs_read`).
4. Restore the INI from the backup.

| | Result |
|---|---|
| `INITIALISIERUNG` appears in `ifh.trc` for `_JOBS` | yes / no |
| Trace excerpt (the lines around it) | |
| INI restored | |

## 5. Cable on the Mac (Ryan; not in the car)

1. In UTM's USB menu, untick the cable for the VM so macOS sees it.
2. Build and run:
   ```sh
   cd kline && zig build
   ./zig-out/bin/kline list
   ./zig-out/bin/kline probe /dev/cu.usbserial-…
   ```
3. Paste every line below. The ones that matter most are `line 10400` (does macOS 26's
   FTDI driver accept and read back a non-standard rate?), `latency 1ms`, and the `lock` line's
   `exclusive` (macOS ptys don't enforce `TIOCEXCL`, so only the real cable can show it).

```
(kline list)

(kline probe)

exit code:
```

| Check | Result |
|---|---|
| Enumerates as `0403:6001` | |
| Lock: lock file refuses a second open | |
| Lock: `TIOCEXCL` refuses a second open | |
| 9600 8E1 read back | |
| 115200 8N1 read back | |
| 10400 8N1 read back | |
| Latency 1 ms accepted | |
| DTR on/off | |
| DSR | |

## 6. Echo timing (Ryan; bench 12 V supply only)

Only with a bench supply: 12 V on OBD pin 16, ground on pins 4/5, nothing else connected. If
there's no bench supply, write "no bench supply" here; this moves to the start of M2.

```sh
./zig-out/bin/kline loopback /dev/cu.usbserial-… --bench --latency 1 --count 200
./zig-out/bin/kline loopback /dev/cu.usbserial-… --bench --latency 16 --count 200
```

| Latency | Echoes | Timeouts | Wrong | Min ms | Median ms | Max ms |
|---|---|---|---|---|---|---|
| 1 ms | | | | | | |
| 16 ms | | | | | | |

Each run sets the latency explicitly (1 or 16 ms), because a setting lasts until the cable is
replugged. If latency 1 shows no improvement over 16, `IOSSDATALAT` did not take effect on
the dext (open risk 3 in the design); the fallback is longer receive timeouts
(`ObdAddRecTimeout`).

## 7. Linux repeat (Ryan)

On a Linux machine (a UTM Linux VM with the cable passed through is fine), with Zig 0.16.0:

1. Install the udev rule from [kline/README.md](../../kline/README.md#linux-permissions) and
   replug the cable.
2. Run:
   ```sh
   cd kline
   zig build test --summary all     # the Linux constants tests run here (skipped on macOS)
   zig build && ./zig-out/bin/kline list && ./zig-out/bin/kline probe /dev/ttyUSB0
   ```
3. With a bench supply, the two `loopback` runs from section 6 as well.

| | Result |
|---|---|
| `zig build test` (pass/skip counts) | |
| Linux constants tests ran and passed | |
| `kline list` | |
| `kline probe` (paste below) | |
| Loopback latency 1 / 16 (min/median/max) | |

```
(kline probe on Linux)
```

## 8. Break width (only if section 2 found 0x010C)

With a scope or logic analyser on the cable's K-line (pin 7), run `kline probe` and measure the
low pulse from its `break 25ms` step, on macOS and on Linux. `probe` prints how long the host
held the break by its own clock; on a pty at the desk that was 29.4 ms for 25 asked (macOS
`nanosleep` overshoot), so a fast-init implementation may need a spin-wait for the last
milliseconds.

| | Host-reported hold | Measured on the line |
|---|---|---|
| macOS | | |
| Linux | | |

## 9. Decisions for M1–M3 (together)

| Question | Decision |
|---|---|
| Concepts M3 must port (always including DS2, for the E46 M3) | |
| Fast init (0x010C) in M2? | |
| Opcode work for M3 | |
| Does `NoInitForVJobs = true` match real EDIABAS? | |
| Did latency 1 ms take effect (macOS / Linux)? | |

### Found while building M0, for the M1 plan

- **One EdiabasNet at a time per process.** EdiabasNet's constructor rebinds a static register
  table to the newest instance (`EdiabasNet.cs:3224-3227`), and fills a static dictionary
  without a lock (`:3178`). Two instances interpreting at once in one process corrupt each
  other (seen as a spinning test host). The design's `ediabasd` holds an offline and a live
  instance; M1 must never run them concurrently, and must construct (or reconstruct) each
  before use, or run them in separate processes. M0's census runs one instance at a time,
  and its tests run serially.
- BESTDIS ignores short reads, so the opcode census checks each file's header and job table
  bounds itself and reports a truncated file as an error. That check was validated only on
  upstream's test files, so give any `error` row in section 3 a second look before believing
  it.
