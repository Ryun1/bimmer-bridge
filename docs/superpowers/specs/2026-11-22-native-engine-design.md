# Native engine: reading the car without the Windows VM

Date: 2026-11-22. Status: approved. Approach A, licence GPL-3.0.
Research behind it: `docs/research/native-engine/report.md` (notes in `notes/` beside it), and
the spike in `~/projects/ediabaslib-spike/NOTES.md` (outside the repo).

## Intent

Today every read goes Claude → `mac-mcp` → HTTP → `EdiabasAgent.exe` in a Windows VM → BMW's
`api32.dll` → K+DCAN cable. Ryan wants:

- **to stop running a Windows VM** to read the car;
- **other people to use the project on macOS and Linux**, so it is published;
- the engine **eventually in Zig**, reached by first **using and understanding EdiabasLib**, then
  porting it to Zig and improving it.

Two young GPL-3 projects overlap: OpenDS2 (a Mac app on EdiabasLib, with an MCP server) and
bimmerscope (a Rust port of EdiabasLib). Both clear faults and code modules, and neither has
run on a pre-2007 E60/E61. Ryan's decision: **build our own, using them as references only**.

## Goals

1. Read the car (faults, idents, `STATUS_` reads, describe) from macOS and Linux with no VM.
2. End state: one self-contained Zig binary, so users need no .NET runtime.
3. Anyone with a K+DCAN cable and their own SGBD files can use it, through Claude (MCP) or a
   plain CLI.
4. Read-only guarantees at least as strong as today's, enforced at the lowest layer.

## Non-goals

- Writing to the car (clearing faults, coding, flashing, activations, adaptations). That stays
  manual, in INPA/NCS Expert, outside this project.
- ELM327/OBDLink adapters: EdiabasLib's ELM path is CAN-only, so it can't reach a K-line car.
  Also out: Bluetooth/Wi-Fi adapters, ENET/F-series, ICOM. The engine recognises a non-FTDI
  device and says it is unsupported.
- 5-baud init (concepts 1–3; EdiabasLib refuses them on an echoing cable anyway), and generic
  OBD-II. Both are possible later, not now.
- Windows as a host. Shipping or downloading BMW SGBD files.

## Anti-goals (a failure even if it works)

- Any path by which a published build can send a write, clear, routine, security, session or
  transfer request to a car, or rewrite the cable's own settings.
- Silent divergence: a Zig engine that gives different results from EdiabasLib or real
  EDIABAS without a test catching it.
- Two engines that each need their own MCP server or tool set.
- Anything run against the car unattended, by Claude or by tests.

## Constraints

- **GPL-3.0** for the whole repo: stage 1 links EdiabasLib, stage 2 is a port of it. bimmerscope
  (GPL-3) may be borrowed from with attribution. ediabasx (PolyForm Noncommercial), klartext
  (AGPL) and MrHilgert/ediabas-prg (no licence) are read-only references.
- **SGBDs are user-supplied** (copy of `C:\EDIABAS\Ecu`). This repo never commits or ships a
  `.prg`/`.grp`/`.lib`.
  - That includes upstream's test SGBDs. They are all output of BMW's compiler, and
    `cmd_test1`/`cmd_test2` link `test.lib`, Softing's BEST2 runtime.
  - Tests use them **in place from the submodule**. The submodule is a pointer to upstream's
    own publication, not a copy, and no release artifact contains it.
  - Upstream's `dm338ds1_interbyte`/`d60m47a0_dcan` are never used.
- **The car**: a 2005 E61 diagnosed over the single K-wire (pin 7), cable switch on K-line.
  BMW-FAST at 115200 is expected to dominate. DS2 (9600 Even), KWP2000* (Even) and KWP2000 BMW
  (fast init) are possible. The concept census (M0) settles it. The KGM's after-run relay and
  undervoltage faults mean sessions are short and on a charger.
- **Serial platform facts** (research report, "macOS can lower the FTDI latency timer"):
  - macOS 26 drives the FT232R with Apple's DriverKit dext. Latency is set per open with
    `IOSSDATALAT` (in ms; it resets on replug) and 10400 baud with `IOSSIOSPEED`.
  - Linux uses `ftdi_sio`. An unprivileged user gets 1 ms latency with `ASYNC_LOW_LATENCY`;
    custom baud rates need termios2.
  - .NET's Unix `SerialPort.BreakState` is a 0.25–0.5 s one-shot, so .NET's serial port is not
    used at all.
  - Zig 0.16's standard library lacks the Darwin `TIOC*`/`IOSS*` constants and termios2; they
    are declared by hand.
- **Upstream pin**: EdiabasLib `5e9efc5322eb01ac9703cf1fcafd0b2c37571ca0`. Zig 0.16.
- **Safety rules 1–10 in `docs/safety.md` carry over** (mapped below). Rule 10 is unchanged:
  anything against the car is Ryan's, at the car.

## Approaches considered

- **A. Swap the engine behind one contract, port in testable layers. Chosen.** Stage 1 runs
  EdiabasLib natively behind the existing `/ediabas/*` contract, and stage 2 ports to Zig with
  stage 1 (and real EDIABAS) as reference.
- **B. Zig from day one, EdiabasLib only as reading.** Nothing works at the car until all of it
  does, there is no reference, and it skips "use and understand it first".
- **C. A Zig host around EdiabasLib compiled with NativeAOT.** A fiddly managed/native seam at
  the interpreter level, and more work than A for the same end.
- **Build on OpenDS2 or bimmerscope.** Rejected by Ryan. Both write to cars, neither is Zig,
  both are days old and untested on this car. Used as references.

## The shape

Four layers. The bottom one is written once, in Zig, and serves both stages. Stage 2 replaces
everything above it with one Zig binary.

```
 front      ediabasd stdio (JSON lines)  +  ediabasd CLI
 service    policy, description screen, batching, budgets, result shaping, audit
              stage 1: EdiabasAgent.Core (C#, reused)          stage 2: Zig port
 engine     SGBD parser, BEST/2 interpreter, protocol concepts (BMW-FAST, DS2, KWP…)
              stage 1: EdiabasLib (C#, unmodified)             stage 2: Zig port
 cable      libkline (Zig, C ABI): open/lock/latency/line settings/break/send/receive,
            echo, frame gate, recorder                          same in both stages
              │
              v  /dev/cu.usbserial-* | /dev/ttyUSB*  → K+DCAN → car
```

Claude reaches it through the existing MCP server, which spawns `ediabasd` as a child process.

**Why the cable layer comes first and in Zig.** The research concluded that the risk is
concentrated in a few hundred lines of serial timing that nobody has run on this car from a
Mac. Stage 1 is really "give EdiabasLib a correct Unix serial port", and that is also stage
2's first Zig deliverable. EdiabasLib already has a public hook for a host-provided transport:
the `InterfaceConnectFunc` … `InterfaceSendPulseFunc` setters on `EdInterfaceObd`
(EdInterfaceObd.cs:2346 onward).
- A delegate set from outside takes precedence over the built-in one, and survives connect.
- Ten delegates are required: Connect, Disconnect, SetConfig, SetDtr, SetRts, GetDsr, SetBreak,
  PurgeInBuffer, SendData and ReceiveData (:2859).
- No upstream code sets these public setters. The path behind them is used, through the
  internal `*FuncInt` slots that Bluetooth and Wi-Fi bind, so the setters themselves are
  untested upstream. M1 covers them.

So stage 1 plugs `libkline` in through P/Invoke with **no upstream patch**. Zig work starts on
day one, on the riskiest code, and the safety gate and recorder exist once, below whichever
interpreter is running.

## Layer by layer

### Cable: `libkline` (Zig, `kline/`)

A small static and shared library with a C ABI, plus a `kline` CLI for bring-up. Functions,
one per EdiabasLib delegate it backs:

- **connect / disconnect.** Open the device with `O_NONBLOCK`, take `TIOCEXCL` and an `flock`
  on a lock file, then set latency to 1 ms (`IOSSDATALAT` on macOS, `ASYNC_LOW_LATENCY` on
  Linux). Refuse anything that is not FTDI 0403:6001.
- **set config.** Baud rate (`IOSSIOSPEED` on macOS, termios2 `BOTHER` on Linux), data bits
  and parity. Drain before a parity change. Read the applied settings back.
- **set DTR / RTS, get DSR.** `TIOCMBIS`/`TIOCMBIC`/`TIOCMGET`.
- **set break.** `TIOCSBRK`/`TIOCCBRK`, held until cleared. The caller times the pulse.
- **set concept.** The engine declares the protocol concept and its parameter array (the
  SGBD's `xsetpar` values) before any send. In stage 1 a small subclass of `EdInterfaceObd`
  overrides the virtual `CommParameter` setter (EdInterfaceBase.cs:678), calls the base and
  forwards the values. This is needed because set-config carries `Protocol.Uart` for every
  K-line concept (EdInterfaceObd.cs:871), so the line settings alone can't tell DS2 from
  KWP2000\*.
- **set inter-byte time.** `libkline` owns inter-byte send timing: it paces the bytes with
  monotonic-clock waits. Without this delegate, EdInterfaceObd sends one byte per call when
  an SGBD sets an inter-byte time (EdInterfaceObd.cs:3249–3298), and the gate would never see
  a whole telegram. Replay parity compares bytes and order, not microsecond timing.
- **send.** Runs the **frame gate** (below) on the whole telegram, then writes. Reading back
  and checking the cable's echo stays with the engine, as EdiabasLib does it.
- **receive.** Takes a first-byte timeout and an inter-byte timeout; polls with `FIONREAD`
  and `poll(2)` on the monotonic clock. It returns early when transmit-cancel is set.
- **transmit cancel.** Sets a flag the receive and send loops check, so an abort interrupts a
  job blocked on the cable. EdiabasNet's own abort is checked only between opcodes
  (EdiabasNet.cs:5825).
- **last error.** A side channel giving the specific reason for the last failure, because
  EdiabasLib turns every connect failure into IFH-0018 and every send failure into IFH-0003.
  The reasons are `no_device`, `locked`, `not_ftdi`, `permission`, `gate_refused`, `io`.
- **purge, adapter-echo = true, send-pulse = unsupported.**
- **record.** When a record directory is set, every config change, DTR/break edge and byte
  sent or received goes to a JSON recording with timestamps. An exporter also writes
  EdiabasLib `.sim` files. Identifiers (VIN and others) are scrubbed before anything is
  committed, as `dev/scrub-fixtures.ts` does today.
- **replay.** A recording opened in place of a device. It answers sends from the recording and
  fails on any send that differs from it. This is how both engines are tested without the car.

`ediabasd` turns last-error into bridge errors:

| Reason | Error |
|---|---|
| `no_device` | `cable_not_found` |
| `locked` | `cable_in_use` |
| `not_ftdi` | `cable_unsupported` (new) |
| `permission` | `cable_permission` (new, with the udev hint on Linux) |
| `gate_refused` | `frame_refused` (new) |

A real echo mismatch (IFH-0003 with no other reason) reaches the user as "check the cable
switch is on K-line".

**The frame gate** sees each whole outgoing telegram and the declared concept. It **fails
closed**: a send before a concept is declared, or under a concept other than BMW-FAST (0x010F),
KWP2000\* (0x010D), KWP2000 BMW (0x010C), DS2 (0x0006) or D-CAN (0x0110), is refused. A refusal
**latches**: every further send fails until the port is closed. `ediabasd` checks last-error
after each job, and a `gate_refused` fails the whole request with the new error
`frame_refused`, whatever the job reported. This matters because an SGBD can mask the send
error with its trap mask and carry on as though it succeeded.
- **BMW-FAST and KWP framing:** decode the header and refuse service IDs 0x10 (other than the
  default session), 0x11, 0x14, 0x27, 0x28, 0x29, 0x2E, 0x2F, 0x30, 0x31–0x37, 0x3B, 0x3D and
  0x85, whether physically or functionally addressed.
- **Any `F1→F1` adapter frame** other than the read-only FA/FD/FE queries is refused, because
  those rewrite the cable's CAN mode, PLD mode or reset it.
- **DS2:** commands are manufacturer-specific, so DS2 relies on the job policy above. The
  gate refuses only the frames it can positively identify as writes.
- The SID list is from a secondary source (ISO 14230-3 is paywalled). It ships as an **enforced
  gate from day one**. M0 and M2 traces confirm no legitimate read is caught, and every refusal
  is logged with the frame so a false positive is easy to see.

### Engine, stage 1: EdiabasLib

- `third_party/ediabaslib` is a git submodule at the pinned commit. The build links the spike's
  7 core files plus `EdInterfaceObd.cs` and its helpers, with `BLUETOOTH` undefined, and never
  imports upstream's build files. There are no upstream patches; if one turns out to be
  unavoidable it goes in `third_party/patches/` with the reason.
- `EdInterfaceObd`'s transport delegates are bound to `libkline`; `ObdComPort` is a plain
  device path.
- **Describe never touches the cable.**
  - `ediabasd`'s `IEdiabasApi` holds two EdiabasNet instances and routes by job name:
    - every job whose name starts with `_` goes to an **offline instance**, with the spike's
      offline interface and `NoInitForVJobs = true`. That covers describe's own calls and the
      description screen's `_JOBCOMMENTS`/`_ARGUMENTS` at job time;
    - everything else goes to the live instance on `libkline`.

    `EdiabasService` itself needs no change for this.
  - **`.grp` names** are resolved for the offline instance through a **variant cache**
    (`$XDG_STATE_HOME/ediabas-bridge/variants.json`), keyed by group name and the `.grp` file's
    hash.
    - A live request naming a `.grp` first resolves it on the live instance (EdiabasNet's
      `ResolveSgbdFile`, which runs the group's `IDENTIFIKATION` read, as real EDIABAS does) and
      writes the `VARIANTE` to the cache. Only then do the description screen and the job run,
      so the first live job on a group never hits `needs_car`. A module swap overwrites the
      entry.
    - A changed hash drops the entry.
    - With no entry, an offline `.grp` call fails with the new error `needs_car`, because
      resolving a group runs `IDENTIFIKATION` against the ECU.
    - Describe on a `.grp` never resolves live, even with the cable present.

    The only Core change is mapping that error through to `needs_car`.
- **A job that won't stop.** Break sets transmit-cancel in `libkline` as well as EdiabasNet's
  abort. If the job is still busy after Core's 2 s grace, Core's existing "stuck" contract
  applies. Natively, `ediabasd` then answers that request and exits, and the MCP server
  respawns it on the next request. The job is never resent.

#### M1 amendments (from M0's results and the M1 plan)

- **INIT is split by path, deliberately.** Real EDIABAS runs `INITIALISIERUNG` on the first job
  against a freshly loaded SGBD, even when that job is `_JOBS` (M0 results, section 4). The live
  instance does the same: it is created fresh for each request with `NoInitForVJobs = false`.
  The offline instance keeps `NoInitForVJobs = true`, so describe never runs INIT and never
  touches the cable. For an SGBD whose INIT sends a telegram, describe therefore succeeds
  natively where real EDIABAS would need the car. This difference is intended.
- **One process, one job at a time.** Both instances live in `ediabasd`. Every job runs on one
  worker, one at a time, and `EdiabasFactory.Activate` binds EdiabasNet's static register table
  to the instance about to run (by reflection; upstream unmodified).
- **The port opens only when the SGBD asks.** `Init` creates nothing that touches the cable; the
  live instance is created at the request's first live job, and EdiabasLib opens the port on
  the SGBD's `xconnect`.
- **libkline's reasons reach Core as reserved codes** 9001–9007 (`NativeErrorCodes`), which
  `EdiabasErrorMap` maps to `cable_not_found`, `cable_in_use`, `cable_unsupported`,
  `cable_permission`, `frame_refused`, `needs_car` and an `ediabas_error` that says to check the
  cable switch.
- **Group files resolve at the car only in a live request** (`job`, `jobs`, `vehicle`); a
  describe with no cached variant gives `needs_car`.
- `EdiabasAgent.Core` stays in `vm-agent/` until M4.

### Service, stage 1: reuse `EdiabasAgent.Core`

`vm-agent/EdiabasAgent.Core` is already platform-neutral and reaches EDIABAS only through
`IEdiabasApi`. It moves to `engine/Core`, referenced by both the VM agent and `ediabasd`.
`ediabasd` implements `IEdiabasApi` over EdiabasNet:
- `Job` runs `ExecuteJob` on a worker, and `State` reports busy, ready or error. Upstream's
  `ApiInternal/ApiInternal.cs` already emulates `apiJob`/`apiState`/`apiResult*` over
  EdiabasNet and is the reference for this mapping.
- `Break` sets EdiabasNet's `AbortJobFunc` and `libkline`'s transmit-cancel.
- The result accessors read EdiabasNet's result sets.
- `Init` creates a fresh live instance and opens the port through `libkline`; `End` closes the
  port and disposes the instance. Each request therefore reruns the SGBD's `INITIALISIERUNG`,
  as `apiInit`/`apiEnd` do with real EDIABAS. `TryLoad`/`Unload` only check that `libkline`
  loads.

The policy, description and argument screens, budgets, batching, `job_not_found` and result
shaping are therefore reused unchanged. The process-based conflict check stays in the VM agent;
natively, the cable lock does that job.

### Front: `ediabasd`

- **stdio**: one JSON object per line. Request `{"id", "method", "params"}`, with method `ecus`,
  `describe`, `job`, `jobs`, `vehicle` or `health`. Response `{"id", "ok", "result" | "error"}`.
  Params, results and errors are exactly the bodies `docs/api.md` defines for the matching
  endpoint; this becomes a new section of `docs/api.md`.
- **CLI**: `ediabasd describe NVE_65`, `ediabasd job NVE_65 FS_LESEN` and so on, through the
  same service and policy.
- **Config**: `$XDG_CONFIG_HOME/ediabas-bridge/config.json` (default `~/.config/...`), with
  these keys:
  - `ecuPath`
  - `port`: optional when exactly one FTDI cable is present
  - `jobTimeoutSeconds`, `maxJobTimeoutSeconds` (≤ 60)
  - `voltageSources`, `reviewedJobs`
  - `traceDir`, `recordDir`

  Audit goes to `$XDG_STATE_HOME/ediabas-bridge/audit.log`, in the `AuditLine` format.

### MCP server

- `EDIABAS_BACKEND=native|vm`. The default is `vm` until M2 passes, then `native`.
- The native backend spawns `ediabasd` and speaks stdio, one request at a time, never resending
  a job.
- The seven `ediabas_*` tools are unchanged. `vm_*` tools exist only with the VM backend. A new
  `bridge_health` tool on native reports the engine and version, `ecuPath`, the cable (absent,
  free or locked; latency applied) and the policy version. The `ediabas-bridge` skill calls it
  first on native.
- **One policy check, in the engine.** On native, the MCP server does not re-check the policy.
  Its check existed so a refused job never left the Mac; with a local child process there is
  nothing to cross. This removes the TypeScript policy implementation along with the VM
  backend, leaving the policy in C# (stage 1) and Zig (stage 2). Both must pass
  `policy/policy-cases.json` and `policy/policy-parse-cases.json`.
- The server already runs on Linux under Node; Keychain and VM discovery are VM-backend only.

## Safety, re-mapped (`docs/safety.md`)

| # | Rule | Native enforcement |
|---|---|---|
| 1 | No input into the VM | Moot. |
| 2 | No writes to the car | No method for them; policy refuses by name; **frame gate** refuses by service ID, and refuses cable-config frames, below the interpreter. |
| 3 | Default-deny policy | Same `policy.json` and case files, in the engine. |
| 4 | One user of the cable | `TIOCEXCL` + `flock`. No override. A cable passed through to a VM is absent on the host, giving `cable_not_found`. |
| 5 | Release when idle | Port opened per request, closed at its end. |
| 6 | Network exposure | Moot: no listener. |
| 7 | Read-only files | Reads only `ecuPath`; writes only audit, trace, record dirs. |
| 8 | No EDIABAS config changes | No setter in the protocol or CLI. |
| 9 | Audit | Engine audit log + the MCP server's log. |
| 10 | Not unattended | Unchanged working rule, in skills and README. |

## Milestones and acceptance

**M0: Desk census and cable bring-up (no car).**
1. *Concept census*: for every SGBD in Ryan's Ecu folder, run `INITIALISIERUNG` against an
   offline interface that records the `xsetpar` parameters, then tabulate concept, baud and
   timeouts. If no module uses 0x010C, fast init drops out of M2.
2. *Opcode census*: disassemble every SGBD and confirm none of EdiabasLib's 16 unimplemented
   opcodes appear.
3. *INIT question*: in the VM, run real EDIABAS `_JOBS` with `IfhTrace=3` and the cable
   unplugged, to see whether INIT runs. Record the answer.
4. `libkline` + `kline` CLI, with the cable plugged into the Mac but not the car:
   - it enumerates as 0403:6001;
   - opens with the lock;
   - 9600E, 115200N and 10400 are set and read back;
   - latency is applied;
   - loopback echo timing is measured at latency 1 versus 16.

   Repeat on Linux. If the census found 0x010C, also measure the break-pulse width on a scope.

**M1: `ediabasd` (.NET) complete at the desk.**
- Both policy case files pass.
- `describe` matches the VM agent's for every `.prg` in the Ecu folder (scripted, both
  backends). `.grp` names give `needs_car` until the variant cache has an entry.
- The gate fails closed (no concept declared, unknown concept, refused SID, `F1→F1` writes),
  latches, and surfaces as `frame_refused` even when the SGBD masks the error.
- Upstream's `coverage.bat` job list on upstream's test SGBDs (used in place from the
  submodule) gives the same result sets through real EDIABAS (VM) and `ediabasd`.
- The frame gate's unit tests pass.

**M2: At the car (Ryan present, charger on, voltage logged).**
1. `IDENT` on one BMW-FAST module (`NVE_65`). Results match the VM's, and the telegrams in the
   native recording match those in the VM's IFH trace (compared as telegrams; the trace
   formats differ).
2. `FS_LESEN` on the `docs/at-the-car.md` modules, then `vehicle`, then the E60 whole-car
   reads, all matching the VM in the same session. Count IFH-0003 and short reads.
3. One fast-init module, if M0 found any.
4. A shorter repeat on Linux with the same cable.

These recordings become the replay fixtures (kept local unless scrubbed). Pass: native becomes
the default and the VM backend is deprecated.

**M3: Zig engine, layer by layer, each landing only when its tests pass.**
1. **Service and policy**: both case files.
2. **SGBD parser and virtual jobs**: `describe` and `_TABLES` match `ediabasd` for every `.prg`
   in the Ecu folder. BESTDIS (MIT) is a second reference for the container.
3. **Interpreter**: `coverage.bat` three ways (real EDIABAS, `ediabasd`, Zig) on the test
   SGBDs; every M2 recording replayed through both engines with identical telegrams and result
   sets; EdiabasLib's per-instruction log is the tool for finding where they diverge. Port
   1:1 first, including EdiabasLib's quirks (the compatibility mode and emulated version 7.7.3
   are pinned); improve only after parity. bimmerscope's porting method is the model.
4. **Protocol concepts**: the ones M0 finds in Ryan's SGBDs (BMW-FAST first), plus **DS2**
   for the E46 M3 whether or not the census needs it. KWP2000 BMW fast init only if the census
   finds it. Tested by replay, then at Ryan's car as in M2; DS2 as under "Publish scope".

Pass: the Zig `ediabasd` replaces the .NET one as the shipped engine. The .NET one stays as the
test reference until Ryan retires it.

**Publish scope (Ryan's decision).** Only the Zig engine is published; the .NET `ediabasd`
stays a private tool and the test reference, so there is no .NET packaging or notarisation.
At publish, two kinds of car must work:
- **Ryan's E61**: every concept his SGBDs use, according to the M0 census.
- **The E46 M3 (S54, 2001–06)**: DS2 at 9600 Even on K-line, cable switch on K-line (an E46
  needs pins 7+8 bridged, which is what that switch position does).

Ryan has no E46, so DS2 is proven by replay first:
- EdiabasLib's own DS2 handling, through `.sim` files built from DS2 framing;
- recordings from a volunteer E46 owner if one turns up (OpenDS2's author tests on a 2003 M3).

Until a car run happens it is labelled "replay-tested". Every other chassis is listed as
untested.

**M4: Publish** (after M3, including DS2). Remove the VM backend, `vm_*` tools, `vm-agent/` and `vm-setup/`; add
`LICENSE` (GPL-3.0); write setup docs for macOS and Linux (copy your Ecu folder, the switch
position, and a udev `uaccess` rule on Linux); ship release binaries. Packaging (Homebrew tap,
tarballs) is decided then.

## Repo layout (target)

```
kline/                Zig: libkline (C ABI) + kline CLI
ediabasd/             Zig engine + service + front (M3)
engine/Core/          C# service (moved from vm-agent/EdiabasAgent.Core)
engine/ediabasd-net/  C# stage 1 host
third_party/ediabaslib  (submodule, pinned)
mac-mcp/              MCP server
policy/               unchanged; both engines read it
dev/                  fixtures, recordings, describe diff, coverage three-way
vm-agent/, vm-setup/  until M4
```

## Open risks, ranked (from the research)

1. **BMW-FAST over K-line from a Mac is unproven by anyone.** M0 step 4 and M2 step 1 retire it.
2. **The concept mix in Ryan's SGBDs.** Fast init, and break timing on the dext, matter only if
   the census finds 0x010C.
3. **`IOSSDATALAT` on the macOS 26 dext** is extrapolated from older macOS. M0 measures it.
   Fallback: longer receive timeouts (`ObdAddRecTimeout`). Direct USB is a last resort, because
   it needs root or an Apple entitlement.
4. **`EdInterfaceObd`'s external-transport path** (`UseExtInterfaceFunc`) differs in places
   from its serial path, and its public setters have no upstream user. M1 replay tests and M2
   must cover it; if a difference matters, the transport compensates or one patch is carried.
5. **The gate may refuse a legitimate read.** Some BMW status reads may use 0x30 with
   "report current state", or switch session with 0x10 first. The gate then fails the read,
   which is safe but blocks it. M2 shows this: every refusal is logged with its frame. Any
   change to the SID list is Ryan's decision, made with its own test case, like a policy
   change.
6. **Clone FT232R chips and original cable firmware** are known only through the replacement
   firmware. Copy EdiabasLib's behaviour; don't reason from first principles.

## Left to the builder

- C# project split and names, test frameworks, CLI flags and output format.
- The exact C ABI of `libkline` and the recording JSON, within the lists above.
- Thread priority for the receive loop (`THREAD_TIME_CONSTRAINT_POLICY` / `SCHED_FIFO`) if M0
  timing shows a need.
- Zig module layout inside `ediabasd/`. Whether to rename `mac-mcp`. Whether to borrow
  ZigEmbeddedGroup/serial (MIT) code inside `libkline` or write it fresh. jcollie/zig-serial
  is used only once its licence is confirmed.
