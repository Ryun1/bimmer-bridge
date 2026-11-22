# Native engine M1: `ediabasd` (.NET) complete at the desk — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A .NET `ediabasd` that runs EdiabasLib on macOS behind the existing service contract,
over a Zig `libkline` with an enforced frame gate, a recorder and a replay device, and that is
shown at the desk (no car) to match the VM: describe for every `.prg`, and upstream's
`coverage.bat` job list on upstream's test SGBDs.

**Architecture:**
- **Zig (`kline/`):** a frame gate, a `Session` (a real `Port` or a replay device, plus concept,
  gate latch, inter-byte pacing, cancel, last error, recorder), and a C ABI over it, built as a
  shared library.
- **C# engine (`engine/EdiabasLib.Native`):** links `EdInterfaceObd` and its helpers unmodified
  (`USE_SERIAL_PORT` and `BLUETOOTH` undefined, so .NET's serial port is never compiled in), and
  binds its ten transport delegates to `libkline` through P/Invoke.
- **C# host (`engine/ediabasd-net`):** implements `IEdiabasApi` over two EdiabasNet instances
  (offline for `_` jobs, live for the rest), and wires `EdiabasAgent.Core`'s `EdiabasService`
  unchanged behind a JSON-lines stdio front and a CLI.
- **Dev tools:** the describe comparison (TypeScript, against the VM agent) and the coverage
  comparison (C#, a Mac runner and a Windows runner), plus `docs/native/m1-results.md` for the
  two desk sessions.

**Tech Stack:** .NET 10 SDK 10.0.100 via `/opt/homebrew/opt/dotnet@10` (see Global
Constraints), xunit 2.9.3. Zig 0.16.0. Node/vitest for `dev/`. EdiabasLib at
`5e9efc5322eb01ac9703cf1fcafd0b2c37571ca0`.

**Spec:** `docs/superpowers/specs/2026-11-22-native-engine-design.md` (M1 under "Milestones and
acceptance"). M0's results and decisions: `docs/native/m0-results.md`, section 9 and "Found while
building M0, for the M1 plan".

## Decisions this plan makes (read before reviewing)

1. **INIT split by path (M0 section 9).** The offline instance keeps `NoInitForVJobs = true`; the
   live instance has `NoInitForVJobs = false` and is created fresh per request, so EdiabasNet runs
   the SGBD's `INITIALISIERUNG` before the first live job, as real EDIABAS does. Task 6 writes
   this into the spec as a deliberate difference.
2. **One EdiabasNet at a time, one process.** Both instances live in `ediabasd`. Every
   `ExecuteJob` and `ResolveSgbdFile` runs on one worker, one job at a time (the service already
   waits for `State() != Busy` before the next job). Before each one, `EdiabasFactory.Activate`
   rebinds EdiabasNet's static register table to the instance about to run, by reflection on the
   private `RegisterList` and `Register.SetEdiabas` (EdiabasNet.cs:1586-1599, 1865, 3224-3227).
   Upstream stays unmodified. `Activate` fails at startup if the members are missing, so an
   upstream bump cannot silently break this. The alternative, an offline worker process, needs a
   second IPC protocol that carries result sets, which is more code for no gain while jobs are
   serial anyway.
3. **The port opens only when the SGBD asks.** EdiabasNet connects the interface only on the
   `xconnect` opcode (EdOperations.cs:2856). `Init()` creates the live instance but opens nothing,
   so a describe request never touches the cable, even though the service calls `Init()` for it.
   The spec's "`Init` … opens the port" is read this way.
4. **libkline reasons reach the service as reserved error codes.** The native `IEdiabasApi`
   reports codes 9001–9007 (below) in place of EDIABAS's own when `libkline` gave a reason, and
   Core's `EdiabasErrorMap` maps them. No interface change in Core.
5. **`.grp` resolution needs a live request.** The front tells the API whether the request may
   reach the car (`job`, `jobs`, `vehicle`) or not (`describe`, `ecus`). An offline `_` job on a
   group with no cache entry resolves the group on the live instance only in the first case;
   otherwise it fails with `needs_car`.
6. **Core stays in `vm-agent/EdiabasAgent.Core`** and `ediabasd` references it there. The spec's
   move to `engine/Core` waits for M4, when `vm-agent/` goes, so this branch doesn't rename files
   that the main checkout is changing.
7. **Config and state directory name: `bimmer-bridge`** (the repo was renamed; the spec still
   says `ediabas-bridge`). One constant, `NativeConfig.AppDir`.
8. **The coverage comparison's VM side is a dev tool that Ryan runs at the VM's desktop.** It
   needs `apiJobExt` (standard arguments) and `apiSetConfig("EcuPath")` (to point at a scratch
   copy of the test SGBDs). The agent deliberately never declares these, so they live only in
   `dev/coverage-vm`, never in `vm-agent/`. It is run by Ryan, not over SSH, with the cable in the
   VM but not in the car. **Ryan's OK is needed for this before Task 12's desk session.**
9. **Not in M1:** the MCP server's native backend and `bridge_health` (M2 can use the
   `ediabasd` CLI; the backend comes in the M2 plan), the `.sim` exporter and fixture scrubbing
   (needed when recordings are committed, M2/M3), the echo timing test (start of M2), Linux
   hardware, fast init, and any opcode work.

## Global Constraints

- GPL-3.0. Build our own engine; OpenDS2 and bimmerscope are references only. Read-only, always.
- Never commit a `.prg`/`.grp`/`.lib`. Run `dev/check-no-bmw-files.sh` before every commit.
- Upstream test SGBDs are used in place from `third_party/ediabaslib/EdiabasLib/Test/Ecu`:
  `cmd_test1`, `cmd_test2`, `base1`, `base2`, `cmd_ident` only. Never `dm338ds1_interbyte`,
  `d60m47a0_dcan` or `adapter_prg`. Ryan's SGBDs stay at `~/ediabas-ecu/Ecu`, outside the repo.
- Nothing in M1 talks to the car. No `ediabas_*` tool use, no `kline loopback`, no EDIABAS jobs,
  Tool32, INPA, NCS Expert or WinKFP over SSH. Tests never open a real device: they use ptys,
  replay recordings, or no device at all.
- VM shell: `ssh -o BatchMode=yes ediabas-vm` (admin PowerShell 5.1). Back up before changing
  anything, never print `token.txt`, and no `EDIABAS.INI` edit without Ryan's OK for that change.
- EdiabasLib files are linked unmodified; upstream's build files, scripts and binaries are never
  used.
- .NET: `DOTNET_ROOT=/opt/homebrew/opt/dotnet@10/libexec /opt/homebrew/opt/dotnet@10/bin/dotnet`
  (written `$DOTNET` below). The default `dotnet` is 11 and does not match `global.json`.
- Zig: `/opt/homebrew/bin/zig` 0.16.0. `cd kline && zig build test --summary all` and
  `zig build check-linux` must stay green.
- Test SGBD jobs run one at a time per process: keep
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]` in every test project that
  runs EdiabasNet.
- The gate's concept and SID lists are exactly the spec's:
  - Concepts allowed: BMW-FAST `0x010F`, KWP2000\* `0x010D`, KWP2000 BMW `0x010C`, DS2
    `0x0006`, D-CAN `0x0110`. Any other concept, or no concept, refuses every send.
  - Refused service IDs (header concepts): `0x10` unless its next byte is `0x81` (default
    session), `0x11`, `0x14`, `0x27`, `0x28`, `0x29`, `0x2E`, `0x2F`, `0x30`, `0x31`–`0x37`,
    `0x3B`, `0x3D`, `0x85`, physically or functionally addressed.
  - `F1→F1` adapter frames: allowed only when the command byte is `0xFA`, `0xFD` or `0xFE`.
  - DS2: no write is positively identified yet, so the list of refused DS2 commands is empty.
- Error codes (reserved native codes, all above EDIABAS's own range):

  | Native code | `libkline` reason | Bridge error |
  |---|---|---|
  | 9001 | `no_device` | `cable_not_found` |
  | 9002 | `locked` | `cable_in_use` |
  | 9003 | `not_ftdi` | `cable_unsupported` (new) |
  | 9004 | `permission` | `cable_permission` (new) |
  | 9005 | `gate_refused` | `frame_refused` (new) |
  | 9006 | (group not in the variant cache) | `needs_car` (new) |
  | 9007 | (IFH-0003 with no `libkline` reason) | `ediabas_error`, message "check the cable switch is on K-line" |

- Commit after each task, plain-sentence messages like the repo's history. Note any small
  deviation from this plan in the commit message.

## Review Focus

1. **A job that masks the gate's refusal** (the SGBD's trap mask swallows IFH-0003 and the job
   ends ready with results): the request still fails with `frame_refused`. Test in Task 6.
2. **A describe of a `.grp` with the cable plugged in**: never resolves live, gives `needs_car`
   with no cache entry, and nothing calls `kline_open`. Test in Task 7.
3. **Offline and live instances interleaved in one request** (screen `_JOBCOMMENTS` offline, then
   the job live, then the next item's screen): each runs on its own registers. Test in Task 5.
4. **Break while the job is blocked in `kline_receive`**: `Break()` returns the job within the
   cancel latency, not after the receive timeout. Test in Task 2 (Zig) and Task 6 (C#).
5. **A replay recording that runs out or differs**: the send fails with `replay_mismatch`, never
   a hang, and nothing falls back to a real device. Test in Task 2.

---

### Task 1: Frame gate

**Files:**
- Create: `kline/src/gate.zig`
- Modify: `kline/src/root.zig` (export `gate`, add to the test block)

**Interfaces:**
- Produces:
  - `pub const Concept = enum(u32) { bmw_fast = 0x010F, kwp2000_star = 0x010D, kwp2000_bmw = 0x010C, ds2 = 0x0006, dcan = 0x0110, _ }`
  - `pub const Refusal = enum { no_concept, unknown_concept, refused_sid, adapter_write, malformed }`
  - `pub fn check(concept: ?u32, frame: []const u8) ?Refusal` — null means allowed.

- [ ] **Step 1: Write the failing tests** in `gate.zig`:

```zig
test "no concept refuses" { try expectEqual(Refusal.no_concept, check(null, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }).?); }
test "unknown concept refuses" { try expectEqual(Refusal.unknown_concept, check(0x0001, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }).?); }
test "bmw-fast read ident allowed" { try expectEqual(@as(?Refusal, null), check(0x010F, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80, 0x9F })); }
test "bmw-fast clear dtc refused physical and functional" {
    try expectEqual(Refusal.refused_sid, check(0x010F, &.{ 0x83, 0x12, 0xF1, 0x14, 0xFF, 0xFF }).?);
    try expectEqual(Refusal.refused_sid, check(0x010F, &.{ 0xC3, 0xEF, 0xF1, 0x14, 0xFF, 0xFF }).?);
}
test "default session allowed, other sessions refused" {
    try expectEqual(@as(?Refusal, null), check(0x010F, &.{ 0x82, 0x12, 0xF1, 0x10, 0x81 }));
    try expectEqual(Refusal.refused_sid, check(0x010F, &.{ 0x82, 0x12, 0xF1, 0x10, 0x85 }).?);
}
test "every listed sid refused" { /* loop 0x11,0x14,0x27,0x28,0x29,0x2E,0x2F,0x30,0x31..0x37,0x3B,0x3D,0x85 */ }
test "length byte form (fmt len 0) decodes sid at offset 4" { try expectEqual(Refusal.refused_sid, check(0x010F, &.{ 0x80, 0x12, 0xF1, 0x02, 0x2E, 0x01 }).?); }
test "16-bit length form decodes sid at offset 6" { try expectEqual(Refusal.refused_sid, check(0x010F, &.{ 0x80, 0x12, 0xF1, 0x00, 0x00, 0x02, 0x31, 0x01 }).?); }
test "adapter reads allowed, adapter writes refused" {
    for ([_]u8{ 0xFA, 0xFD, 0xFE }) |c| try expectEqual(@as(?Refusal, null), check(0x010F, &.{ 0x82, 0xF1, 0xF1, c, c }));
    for ([_]u8{ 0xFB, 0xFC, 0x06 }) |c| try expectEqual(Refusal.adapter_write, check(0x010F, &.{ 0x82, 0xF1, 0xF1, c, c }).?);
}
test "truncated header refuses as malformed" { try expectEqual(Refusal.malformed, check(0x010F, &.{ 0x82, 0x12 }).?); }
test "ds2 frames allowed" { try expectEqual(@as(?Refusal, null), check(0x0006, &.{ 0x12, 0x04, 0x00, 0x16 })); }
test "same rules for kwp2000*, kwp2000 bmw and d-can" { /* 0x14 refused, 0x1A allowed under each of 0x010D, 0x010C, 0x0110 */ }
```

- [ ] **Step 2: Run** `cd kline && zig build test --summary all`. Expected: compile failure (`check` undefined).
- [ ] **Step 3: Implement `check`.** Header concepts: data length `fmt & 0x3F`; if 0, a length byte at `[3]` and data at `[4]`; if that is 0 too, a big-endian u16 at `[4..6]` and data at `[6]`. Target `[1]`, source `[2]`. A frame too short for its header or SID is `malformed`. DS2: allowed when a concept is declared (empty refusal list, `ds2_refused_commands: []const u8 = &.{}`, with a comment saying why).
- [ ] **Step 4: Run** the tests and `zig build check-linux`. Expected: all pass.
- [ ] **Step 5: Commit** — `git add kline/src/gate.zig kline/src/root.zig && git commit -m "kline: frame gate for the declared concept, failing closed"`

### Task 2: Session, recorder and replay

**Files:**
- Create: `kline/src/session.zig`, `kline/src/recording.zig`
- Modify: `kline/src/root.zig`

**Interfaces:**
- Consumes: `Port` (`port.zig`: `open`, `setLine`, `setDtr`, `setRts`, `getDsr`, `setBreak`, `setLowLatency`, `write`, `receive`, `purge`, `close`), `gate.check`.
- Produces:
  - `pub const Reason = enum(c_int) { ok = 0, no_device = 1, locked = 2, not_ftdi = 3, permission = 4, gate_refused = 5, io = 6, timeout = 7, cancelled = 8, replay_mismatch = 9, unsupported = 10 }`
  - `pub const Session` with `openDevice(alloc, path: []const u8, record_dir: ?[]const u8, lock_dir: ?[]const u8) !*Session`, `openReplay(alloc, recording_path: []const u8) !*Session`, `close()`, `setLine(LineSettings) Reason`, `setDtr(bool) Reason`, `setRts(bool) Reason`, `getDsr(*bool) Reason`, `setBreak(bool) Reason`, `setConcept(params: []const u32) Reason`, `setInterByte(ms: u32) Reason`, `send(bytes: []const u8) Reason`, `receive(buf: []u8, timeout_ms: u32, tel_end_ms: u32) Reason`, `purge() Reason`, `cancel(on: bool) void` (atomic, any thread), `lastError() Reason`. Open failures are returned as `error.NoDevice` etc., mapped to `Reason` by `reasonOf(err)`.
  - Recording file: JSON lines, one event per line, `{"t_us":<monotonic µs since open>,"op":"<op>",…}`. Ops and fields: `open {path}`, `line {baud,bits,parity}`, `dtr {on}`, `rts {on}`, `break {on}`, `concept {params:[…]}`, `interbyte {ms}`, `send {hex}`, `recv {hex}`, `purge`, `refused {hex,reason}`, `close`. Named `kline-<yyyyMMddTHHmmssZ>-<pid>.jsonl` in `record_dir`.

- [ ] **Step 1: Write the failing tests** in `session.zig`, on a pty (`testing/pty.zig`, `require_ftdi = false`, temp lock dir) and on replay files written to a `TmpDir`:
  - `send refused before any concept, and the refusal latches`: `send` of `82 12 F1 1A 80` returns `.gate_refused`; after `setConcept(&.{0x010F, …})` the same send still returns `.gate_refused`; `lastError()` is `.gate_refused`; nothing reached the pty's other end.
  - `allowed send reaches the line whole`: concept BMW-FAST, send `82 12 F1 1A 80 9F`, the pty peer reads those 6 bytes.
  - `inter-byte pacing`: `setInterByte(5)`, send 4 bytes; the peer's read timestamps show ≥ 3 × 5 ms between first and last byte (allow 1 ms under).
  - `cancel interrupts a blocked receive`: a thread calls `cancel(true)` after 50 ms while `receive(buf, 5000, 100)` waits; it returns `.cancelled` within 200 ms.
  - `recorder writes every event`: with a record dir, open, line, concept, send, a peer reply, receive, close; the file's ops are exactly `open,line,concept,send,recv,close` and the `send`/`recv` hex match.
  - `replay answers from the recording`: replay a file with `send 821…`, `recv <echo+response>`; `send` of the same bytes is `.ok` and `receive` returns the recorded bytes in order, split across two `receive` calls of 3 and the rest.
  - `replay refuses a different send`: `.replay_mismatch`, and every later send too.
  - `replay runs out`: `receive` beyond the recorded bytes returns `.timeout` at once; a send after the last recorded send is `.replay_mismatch`.
  - `replay of an empty file`: opens; any send is `.replay_mismatch` (the coverage runner relies on this).
  - `gate applies in replay too`: replay a recording whose send is `83 12 F1 14 FF FF` under BMW-FAST: `.gate_refused`, before the recording is consulted.
- [ ] **Step 2: Run** `zig build test --summary all`. Expected: compile failure.
- [ ] **Step 3: Implement** `Session` (device is a tagged union of `Port` and `Replay`) and `recording.zig` (writer and reader, `std.json` for parse). Order inside `send`: latched? → gate → (replay compare | paced write). A refusal records a `refused` event. `openDevice` calls `setLowLatency` and ignores `Unsupported` (records it in the `open` event as `"latency":"unsupported"`). Receive polls in slices of at most 10 ms so `cancel` is seen promptly.
- [ ] **Step 4: Run** tests and `zig build check-linux`. Expected: pass (the pty tests that are macOS-only keep their skip).
- [ ] **Step 5: Commit** — `"kline: session with gate latch, inter-byte pacing, cancel, recorder and replay"`

### Task 3: C ABI and shared library

**Files:**
- Create: `kline/src/capi.zig`, `kline/include/kline.h`
- Modify: `kline/build.zig` (add a `.dynamic` library `kline` from a module rooted at `capi.zig`, installed to `zig-out/lib`, plus its tests in the `test` step), `kline/README.md` (C ABI section)

**Interfaces:**
- Consumes: `Session`, `Reason`, `identity` (for listing FTDI devices, as `kline list` does).
- Produces (exported, C calling convention; `kline_t` is opaque; every `int` return is a `Reason`):

```c
int  kline_open(const char *path, const char *record_dir /* nullable */, kline_t **out);
int  kline_open_replay(const char *recording_path, kline_t **out);
void kline_close(kline_t *k);
int  kline_set_line(kline_t *k, uint32_t baud, uint8_t data_bits, uint8_t parity /* 0 none, 1 even, 2 odd */);
int  kline_set_dtr(kline_t *k, int on);
int  kline_set_rts(kline_t *k, int on);
int  kline_get_dsr(kline_t *k, int *dsr);
int  kline_set_break(kline_t *k, int on);
int  kline_set_concept(kline_t *k, const uint32_t *params, size_t count);
int  kline_set_interbyte(kline_t *k, uint32_t ms);
int  kline_send(kline_t *k, const uint8_t *data, size_t len);
int  kline_receive(kline_t *k, uint8_t *buf, size_t len, uint32_t timeout_ms, uint32_t tel_end_ms);
int  kline_purge(kline_t *k);
void kline_cancel(kline_t *k, int on);              /* thread-safe */
int  kline_last_error(kline_t *k);                   /* sticky gate_refused once latched */
int  kline_find_cable(char *path, size_t path_len);  /* count of FTDI 0403:6001 devices; path filled when exactly 1 */
const char *kline_version(void);
```

- [ ] **Step 1: Write the failing tests** in `capi.zig`, calling the exported functions as C would: open replay of a temp recording, set concept, send, receive, close; `kline_open("/dev/does-not-exist", null, &k)` returns 1 (`no_device`) and leaves `k` null; null handle to any function returns `io` (6) without crashing; `kline_version()` is `"0.1.0"`.
- [ ] **Step 2: Run** `zig build test --summary all`. Expected: compile failure.
- [ ] **Step 3: Implement** the exports over `Session` with `std.heap.c_allocator`, and `kline.h` matching them. `zig build` must produce `zig-out/lib/libkline.dylib` on macOS (`.so` on Linux).
- [ ] **Step 4: Run** `zig build test --summary all && zig build check-linux && ls zig-out/lib`. Expected: pass; `libkline.dylib` present; `nm -gU zig-out/lib/libkline.dylib | grep kline_` lists all 17 symbols.
- [ ] **Step 5: Commit** — `"kline: C ABI and shared library"`

### Task 4: EdInterfaceObd on libkline

**Files:**
- Modify: `engine/EdiabasLib.Native/EdiabasLib.Native.csproj` (link `EdInterfaceObd.cs`, `EdBluetoothInterface.cs`, `EdElmInterface.cs`, `EdElmWifiInterface.cs`, `EdCustomWiFiInterface.cs`, `EdCustomAdapterCommon.cs`, `TcpClientWithTimeout.cs`, `EscapeStreamReader.cs`, `EscapeStreamWriter.cs`, `StreamExtension.cs`, `MemoryQueueBufferStream.cs` from upstream, unmodified; no `USE_SERIAL_PORT`, no `BLUETOOTH`; a `BuildKline` target before build running `$(ZigExe) build -Doptimize=ReleaseSafe` in `kline/` (`ZigExe` defaults to `zig`) and copying `libkline.dylib`/`.so` to the output)
- Create: `engine/EdiabasLib.Native/KlineNative.cs`, `engine/EdiabasLib.Native/KlineTransport.cs`, `engine/EdiabasLib.Native/KlineInterface.cs`
- Test: `engine/EdiabasLib.Native.Tests/KlineInterfaceTests.cs`, `engine/EdiabasLib.Native.Tests/Recordings.cs` (builds recording files in a temp dir)

**Interfaces:**
- Consumes: the C ABI (Task 3).
- Produces:
  - `enum KlineReason { Ok = 0, NoDevice = 1, Locked = 2, NotFtdi = 3, Permission = 4, GateRefused = 5, Io = 6, Timeout = 7, Cancelled = 8, ReplayMismatch = 9, Unsupported = 10 }`
  - `sealed class KlineTransport : IDisposable` — `static KlineTransport Device(string? recordDir)`, `static KlineTransport Replay(string recordingPath)`; `void Bind(EdInterfaceObd iface)` sets `InterfaceConnectFunc`, `InterfaceDisconnectFunc`, `InterfaceTransmitCancelFunc`, `InterfaceSetConfigFunc`, `InterfaceSetDtrFunc`, `InterfaceSetRtsFunc`, `InterfaceGetDsrFunc`, `InterfaceSetBreakFunc`, `InterfaceSetInterByteTimeFunc`, `InterfacePurgeInBufferFunc`, `InterfaceAdapterEchoFunc` (returns true), `InterfaceSendDataFunc`, `InterfaceReceiveDataFunc`; `InterfaceSendPulseFunc` and `InterfaceHasIgnitionStatusFunc` stay null. `KlineReason LastReason { get; }` (the first failure reason since connect; `GateRefused` once latched), `void Cancel()`, `void SetConcept(uint[] parameters)`, `bool IsOpen`.
  - `sealed class KlineInterface : EdInterfaceObd` — ctor `(KlineTransport transport)` binds it; overrides `CommParameter` set: base first, then `transport.SetConcept(value)` (also replayed at connect if set before the port opened). Exposes `Transport`.
  - Library loading: `NativeLibrary.SetDllImportResolver` for `"kline"`: `$KLINE_LIB` if set, else the app directory.

- [ ] **Step 1: Write the failing tests** (`KlineInterfaceTests`). Each attaches the interface to an EdiabasNet through `EdiabasFactory.Create` (EdInterfaceObd needs its owner for config and errors) and builds a replay recording with `Recordings.BmwFastExchange(request, response)` (echo of the request, then the response, checksums computed with `EdInterfaceBase.CalcChecksumBmwFast`), then:
  - `TransmitData_BmwFastIdent_ReturnsResponse`: `iface.CommParameter = EdInterfaceBase.CommParameterBmwFast`; connect; `TransmitData(82 12 F1 1A 80 9F)` is true and returns the recorded response bytes. (This is the first run of the public setter path, open risk 4.)
  - `TransmitData_ClearFaults_RefusedAndLatched`: request `83 12 F1 14 FF FF`: `TransmitData` false, `Transport.LastReason == GateRefused`; a following allowed request also fails and the reason stays `GateRefused`.
  - `TransmitData_NoConcept_Refused`: no `CommParameter`, any request: false, `GateRefused`.
  - `Connect_MissingDevice_ReasonNoDevice`: `KlineTransport.Device(null)` with `ObdComPort=/dev/does-not-exist`: `InterfaceConnect()` false, `LastReason == NoDevice`.
  - `Cancel_DuringReceive_ReturnsPromptly`: recording with the echo but no response; `TransmitData` on one thread, `Cancel()` after 100 ms; it returns within 1 s although the SGBD timeout (5000) is longer.
- [ ] **Step 2: Run** `$DOTNET test engine/EdiabasLib.Native.Tests`. Expected: build failure.
- [ ] **Step 3: Implement.** `SetConfig` maps `Protocol.Uart` + baud/bits/parity to `kline_set_line`, and returns `InterfaceErrorResult.ConfigError` for anything else (no bit-bang, no CAN). `SendData` with `setDtr == true` returns false with reason `Unsupported` (only concepts 1–3 set it on an echoing cable, EdInterfaceObd.cs:555/627/665). `ReceiveData` passes `timeout` and `timeoutTelEnd` straight through.
- [ ] **Step 4: Run** the tests, and the existing 6. Expected: all pass.
- [ ] **Step 5: Commit** — `"EdInterfaceObd on libkline through the public transport setters"`

### Task 5: Activate before every job

**Files:**
- Modify: `engine/EdiabasLib.Native/EdiabasFactory.cs`
- Test: `engine/EdiabasLib.Native.Tests/ActivateTests.cs`

**Interfaces:**
- Produces: `EdiabasFactory.Activate(EdiabasNet ed)` — under the factory's lock, calls `SetEdiabas(ed)` on every element of EdiabasNet's static `RegisterList`. `EdiabasFactory.CheckActivateSupported()` throws `InvalidOperationException` naming the missing member if reflection cannot find `RegisterList` or `SetEdiabas`; `Create` calls it once.

- [ ] **Step 1: Write the failing tests:**
  - `Interleaved_Instances_MatchSingleInstance`: run `TEST_MATH` on `cmd_test1` in a lone instance and keep its result sets. Then create A, create B, `Activate(A)` and run `TEST_MATH` on A, `Activate(B)` and run `_JOBS` on `cmd_test2` with B, `Activate(A)` and run `TEST_MATH` on A again. Both A runs equal the lone run (compare names, types and values of every set).
  - `Activate_BindsRegistersToInstance`: after `Activate(A)`, every register's private `_ediabas` (read by reflection in the test) is A.
- [ ] **Step 2: Run** the tests. Expected: build failure (`Activate` missing).
- [ ] **Step 3: Implement** with `BindingFlags.NonPublic | BindingFlags.Static` / `Instance`; cache the `FieldInfo`/`MethodInfo`.
- [ ] **Step 4: Run** all engine tests. Expected: pass.
- [ ] **Step 5: Commit** — `"Rebind EdiabasNet's register table before each job, so two instances can take turns"`

### Task 6: Native error codes in Core, and the native IEdiabasApi

**Files:**
- Modify: `vm-agent/EdiabasAgent.Core/ApiModels.cs` (codes `cable_unsupported`, `cable_permission`, `frame_refused`, `needs_car`; `Status`: `frame_refused` 403, `needs_car` 409, `cable_unsupported` 502, `cable_permission` 502), `vm-agent/EdiabasAgent.Core/EdiabasErrorMap.cs` (`public static class NativeErrorCodes { NoDevice = 9001 … EchoMismatch = 9007 }` and table entries per Global Constraints), `vm-agent/EdiabasAgent.Tests/` (map tests)
- Create: `engine/ediabasd-net/Ediabasd.csproj` (exe, `AssemblyName` `ediabasd`, references `EdiabasLib.Native` and `../../vm-agent/EdiabasAgent.Core`), `engine/ediabasd-net/NativeEdiabasApi.cs`, `engine/ediabasd-net/LiveCable.cs`
- Create: `engine/ediabasd-net.Tests/Ediabasd.Tests.csproj`, `engine/ediabasd-net.Tests/AssemblyInfo.cs`, `engine/ediabasd-net.Tests/NativeEdiabasApiTests.cs`
- Modify: `docs/superpowers/specs/2026-11-22-native-engine-design.md` — add under "Engine, stage 1" a short "M1 amendments" list: decisions 1–5 above, with INIT stated as the deliberate difference from real EDIABAS's `_JOBS` behaviour (M0 section 4).

**Interfaces:**
- Consumes: `IEdiabasApi`, `ApiState`, `ApiResultFormat` (Core); `EdiabasFactory.Create/Activate`, `OfflineInterface`, `KlineInterface`, `KlineTransport`.
- Produces:
  - `interface ILiveCable { EdInterfaceBase Interface { get; } KlineReason Reason { get; } void Cancel(); }` and `Func<ILiveCable>` factories: `LiveCable.Device(string port, string? recordDir)` for production; tests pass replay- or fake-backed ones.
  - `sealed class NativeEdiabasApi : IEdiabasApi, IDisposable` — ctor `(string ecuPath, Func<ILiveCable> liveCable, IVariantCache variants, string? traceDir)`; `RequestMode Mode { get; set; }` with `enum RequestMode { Offline, Live }` (default `Offline`).
  - Behaviour: `TryLoad` checks `libkline` loads (`kline_version`); `Unload` does nothing. `Init` disposes any previous live instance and creates a new one (`noInitForVJobs: false`) with `ObdComPort` set; returns true. `End` disposes it (closing the port). `Job` routes names starting with `_` to the offline instance (`noInitForVJobs: true`, created once), others to the live one, and runs `Activate` + `ResolveSgbdFile` + `ExecuteJob` on one dedicated worker thread; `State` is `Busy` until it finishes. After a live job, a `GateRefused` reason makes the state `Error` with code 9005 whatever the job returned; other reasons replace EDIABAS's code with 9001–9004; IFH-0003 with no reason becomes 9007. `Break` sets the abort flag read by `AbortJobFunc` (set once at construction, since it cannot change while a job runs, EdiabasNet.cs:2882) and calls `ILiveCable.Cancel()`. Result accessors read the finished job's `ResultSets` (set 0 is the system set and is not counted by `ResultSets`), with format and value conversion as upstream's `ApiInternal/ApiInternal.cs` does.

- [ ] **Step 1: Write the failing tests:**
  - Core: `EdiabasErrorMap.Failure(9005, "x")` has code `frame_refused`; 9001→`cable_not_found`, 9002→`cable_in_use`, 9003→`cable_unsupported`, 9004→`cable_permission`, 9006→`needs_car`, 9007→`ediabas_error` with "K-line" in the message; 28 still `cable_not_found`.
  - `OfflineJobs_ListsJobs`: `_JOBS` on `cmd_test2` → Ready, a set has `JOBNAME` `TEST_IFACE_FLAGS`, and the live cable factory was never called.
  - `LiveJob_RunsOnLiveInstance`: after `Init`, `TEST_MATH` on `cmd_test1` (cable factory: empty replay) → Ready, results equal a direct EdiabasNet run.
  - `LiveInstance_RunsInit_OfflineDoesNot`: the API exposes `internal bool LiveNoInitForVJobs` / `OfflineNoInitForVJobs` (`InternalsVisibleTo` the tests): false and true.
  - `GateRefusal_FailsJobEvenWhenMasked`: fake `ILiveCable` whose `Reason` is `GateRefused` and whose interface lets the job succeed → `State() == Error`, `ErrorCode() == 9005`.
  - `ConnectReason_ReplacesIfh0018`: fake reason `NotFtdi` and a job that fails with IFH-0018 → code 9003.
  - `Break_CancelsCable`: `Break()` during a job calls `ILiveCable.Cancel()` and the job ends with state `Break` or `Error` within 2 s.
  - `Describe_NeverCreatesLiveCable`: `Init`, `_JOBS`, `_JOBCOMMENTS#TEST_MATH`, `End` → factory call count 0.
- [ ] **Step 2: Run** `$DOTNET test engine/ediabasd-net.Tests` and `$DOTNET test vm-agent/EdiabasAgent.Tests`. Expected: build failure / failing map tests.
- [ ] **Step 3: Implement**, reading `third_party/ediabaslib/EdiabasLib/ApiInternal/ApiInternal.cs` for the `apiState`/`apiResult*` mapping (reference only; not linked).
- [ ] **Step 4: Run** both test projects and the engine tests. Expected: pass; the VM agent's existing tests unchanged.
- [ ] **Step 5: Commit** — `"Native IEdiabasApi over two EdiabasNet instances, and the native error codes in Core"`

### Task 7: Variant cache and `.grp` handling

**Files:**
- Create: `engine/ediabasd-net/VariantCache.cs`
- Modify: `engine/ediabasd-net/NativeEdiabasApi.cs`
- Test: `engine/ediabasd-net.Tests/VariantCacheTests.cs`, additions to `NativeEdiabasApiTests.cs`

**Interfaces:**
- Produces: `interface IVariantCache { string? Lookup(string group, string grpSha256); void Store(string group, string grpSha256, string variant); }`; `sealed class VariantCache : IVariantCache` — ctor `(string path)`, JSON `{"<GROUP>": {"sha256": "...", "variant": "..."}}`, group names upper-cased, a hash mismatch counts as a miss and is dropped on the next `Store`, written atomically (temp file + rename).
- Behaviour in `NativeEdiabasApi`: a name is a group when `<name>.grp` exists in `ecuPath` (case-insensitive) and no `<name>.prg` does. Offline `_` job on a group: cache hit → run on the variant; miss and `Mode == Offline` → `Error` with code 9006; miss and `Mode == Live` → resolve the group on the live instance (`ResolveSgbdFile`, which runs `IDENTIFIKATION`), store the variant, then run the `_` job offline on it. A live job on a group stores the variant it resolved. The variant name comes from EdiabasNet after `ResolveSgbdFile` (`SgbdFileName` without extension; check the property name in EdiabasNet.cs).

- [ ] **Step 1: Write the failing tests:**
  - `VariantCache`: store then lookup; lookup with another hash is null; reload from disk; a corrupt file reads as empty and is replaced on store.
  - `GroupDescribe_NoEntry_NeedsCar_NoCable`: `Mode = Offline`, `_JOBS` on `cmd_ident` → code 9006, live factory never called.
  - `GroupDescribe_WithEntry_UsesVariant`: cache holds `CMD_IDENT` → `cmd_test2` with the file's hash → `_JOBS` lists `TEST_IFACE_FLAGS`.
  - `GroupLive_ResolvesAndStores`: `Mode = Live`, `_JOBS` on `cmd_ident` (empty-replay cable) → the cache gains an entry whose variant is what EdiabasNet resolved. If `cmd_ident`'s identification needs an answer from the line, build the replay recording for it instead, and note it in the commit.
- [ ] **Step 2: Run** the tests. Expected: failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** all `ediabasd-net` tests. Expected: pass.
- [ ] **Step 5: Commit** — `"Variant cache: group names resolve offline only from a cached live resolution"`

### Task 8: Native config and service wiring

**Files:**
- Create: `engine/ediabasd-net/NativeConfig.cs`, `engine/ediabasd-net/EdiabasdHost.cs`, `engine/ediabasd-net/AuditLog.cs`
- Test: `engine/ediabasd-net.Tests/NativeConfigTests.cs`, `engine/ediabasd-net.Tests/PolicyCaseTests.cs`

**Interfaces:**
- Consumes: `EdiabasService` and its dependencies exactly as `vm-agent/EdiabasAgent/AgentContext.cs` builds them; `AuditLine.Format` as the VM agent uses it (`vm-agent/EdiabasAgent/Middleware.cs`).
- Produces:
  - `sealed class NativeConfig` — `const string AppDir = "bimmer-bridge"`; properties `EcuPath` (required, must exist), `Port` (optional), `JobTimeoutSeconds` (default 30), `MaxJobTimeoutSeconds` (default 60, ≤ 60), `VoltageSources`, `ReviewedJobs`, `TraceDir`, `RecordDir`; `static (NativeConfig? Config, IReadOnlyList<string> Errors) Load(string? path, IReadOnlyDictionary<string,string?> env)`; paths: `$XDG_CONFIG_HOME/bimmer-bridge/config.json` (default `~/.config/...`), state `$XDG_STATE_HOME/bimmer-bridge/` (default `~/.local/state/...`) holding `audit.log` and `variants.json`. Unknown keys are errors, like `AgentConfig`.
  - `sealed class EdiabasdHost : IDisposable` — ctor `(NativeConfig config, Func<ILiveCable>? liveCable = null)`; `EdiabasService Service`; `NativeEdiabasApi Api`; `bool Stuck` (from `DllLoadState`); `string ResolvePort()` — config `Port`, else the single cable from `kline_find_cable`, else `cable_not_found`/"more than one cable, set port". The conflict check returns no processes (the cable lock does that job natively).
- [ ] **Step 1: Write the failing tests:**
  - Config: defaults; `maxJobTimeoutSeconds: 61` is an error; missing `ecuPath` is an error; `XDG_CONFIG_HOME` honoured; unknown key is an error; `voltageSources` and `reviewedJobs` parse as in `AgentConfig`.
  - `PolicyCaseTests`: the `JobPolicy` instance `EdiabasdHost` uses passes every case in `policy/policy-cases.json` and `policy/policy-parse-cases.json` (same assertions as `vm-agent/EdiabasAgent.Tests/JobPolicyTests.cs`).
  - `Host_DescribeList_UpstreamTestSgbd`: host over the upstream test Ecu folder; `Service.DescribeList("cmd_test2", null, true, …)` is ok and lists `TEST_IFACE_FLAGS` with a verdict.
- [ ] **Step 2: Run** the tests. Expected: failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** all `ediabasd-net` tests. Expected: pass.
- [ ] **Step 5: Commit** — `"ediabasd config, host wiring for EdiabasService, and the policy case files on the native host"`

### Task 9: stdio front and CLI

**Files:**
- Create: `engine/ediabasd-net/Front.cs`, `engine/ediabasd-net/StdioServer.cs`, `engine/ediabasd-net/Cli.cs`, `engine/ediabasd-net/Program.cs`
- Modify: `docs/api.md` (new section "`ediabasd` stdio"), `README.md` or `engine/README.md` (how to build and run `ediabasd`)
- Test: `engine/ediabasd-net.Tests/FrontTests.cs`, `engine/ediabasd-net.Tests/StdioServerTests.cs`, `engine/ediabasd-net.Tests/CliTests.cs`

**Interfaces:**
- Consumes: `EdiabasdHost`, `RequestParsing`, `ApiJson`, `ApiErrors`.
- Produces:
  - `sealed class Front` — `string Handle(string method, JsonElement? params)` returns the response JSON for one request, `{"ok":true,"result":…}` or `{"ok":false,"error":{code,message,details}}`; sets `Api.Mode` to `Live` for `job`, `jobs`, `vehicle`, `Offline` otherwise; writes one audit line per request.
  - Methods and params: `ecus {filter?}`, `describe {ecu, job?, filter?, comments?: bool}`, `job` (body of `POST /ediabas/job`, parsed with `RequestParsing.ParseJob`), `jobs` (body of `POST /ediabas/jobs`), `vehicle {}`, `health {}` → `{engine:"ediabasd-net", version, ecuPath, cable:{state:"absent"|"free"|"locked", port?}}`. Unknown method → `bad_request`.
  - `StdioServer.Run(TextReader in, TextWriter out)`: one JSON object per line, `{"id","method","params"}` → `{"id","ok",…}`; malformed line → `bad_request` with `"id": null`; one request at a time; after a request leaves the host stuck, writes its response, flushes, and returns exit code 3.
  - CLI: `ediabasd serve` (stdio), `ediabasd ecus [FILTER]`, `ediabasd describe ECU [JOB] [--filter X] [--no-comments]`, `ediabasd job ECU JOB [--args A] [--results R] [--timeout N]`, `ediabasd vehicle`, `ediabasd health`, `--config PATH` on all. Prints the response JSON indented; exit 0 ok, 1 error response, 2 usage, 78 config error.
- [ ] **Step 1: Write the failing tests:**
  - `Front`: `describe {ecu:"cmd_test2"}` ok; `describe {}` → `bad_request` (parameter `ecu`); `job {ecu:"cmd_test2", job:"TEST_IFACE_FLAGS"}` → `job_not_allowed` (policy), and no live cable created; `ecus {filter:"cmd"}` lists `cmd_test1`; `describe {ecu:"cmd_ident"}` → `needs_car`; unknown method → `bad_request`.
  - `StdioServer`: two requests in, two responses out with matching ids, in order; a garbage line gives `{"id":null,"ok":false,…bad_request}` and the server carries on; end of input ends `Run` with 0.
  - `Cli`: `describe cmd_test2 --no-comments` exits 0 and prints JSON with `"ecu"`; `job` with no args exits 2; a config with `maxJobTimeoutSeconds: 99` exits 78.
- [ ] **Step 2: Run** the tests. Expected: failures.
- [ ] **Step 3: Implement**; document the stdio protocol in `docs/api.md` (request/response shape, methods, the four new error codes, exit code 3 after a stuck job).
- [ ] **Step 4: Run** all `ediabasd-net` tests; then by hand, with no cable plugged in: `$DOTNET run --project engine/ediabasd-net -- describe NVE_65 --config <a temp config with ecuPath ~/ediabas-ecu/Ecu>` prints NVE_65's jobs, and `health` says `"state":"absent"`. Expected: both as described.
- [ ] **Step 5: Commit** — `"ediabasd: JSON-lines stdio front and CLI over the shared service"`

### Task 10: Describe comparison tool

**Files:**
- Create: `dev/describe-diff.ts` (CLI), `dev/describe-diff-lib.ts` (pure), `dev/test/describe-diff.test.ts`
- Modify: `dev/README.md`

**Interfaces:**
- Consumes: the VM agent's `GET /ediabas/describe` through `mac-mcp/src/agentClient.ts` and `mac-mcp/src/token.ts` (as `dev/replay-agent.ts` reaches mac-mcp code); `ediabasd serve` spawned as a child.
- Produces: `diffDescribe(vm: Outcome, native: Outcome): DescribeDiff` with `kind: "match" | "differ" | "vm_error" | "native_error" | "both_error"` and a list of `{path, vm, native}` differences. Rules: ignore `durationMs`; compare `ecu`, `variant`, `count`, job names in order, each job's `comment` and `verdict`; when either side's `review` is `not_checked`, compare only name and `allowed`/`rule`.
- CLI: `npx tsx dev/describe-diff.ts --ecu-path ~/ediabas-ecu/Ecu --out <file.jsonl> [--only NAME…] [--limit N]`: every `.prg` (from `ediabasd ecus`), one JSONL line per ECU, skips ECUs already in `--out` (resumable), prints a summary table by `kind` at the end. Groups are not compared (they give `needs_car` natively by design).
- [ ] **Step 1: Write the failing tests** for `diffDescribe`: identical → match; durations differ → match; one comment differs → differ with path `jobs[3].comment`; VM `cable_not_found` and native ok → `vm_error`; `not_checked` on one side with different comments → match.
- [ ] **Step 2: Run** `cd dev && npx vitest run test/describe-diff.test.ts`. Expected: fail.
- [ ] **Step 3: Implement.** The CLI is not run in this task (it talks to EDIABAS in the VM; that is Task 12's desk session).
- [ ] **Step 4: Run** the dev test suite. Expected: pass.
- [ ] **Step 5: Commit** — `"Describe comparison tool: the VM agent against ediabasd, per SGBD"`

### Task 11: Coverage job list and runners

**Files:**
- Create: `dev/coverage/jobs.json`, `dev/coverage/CoverageModel.cs` (job-string parsing and the JSONL record), `dev/coverage/Coverage.csproj` (Mac runner + `compare`, references `EdiabasLib.Native`), `dev/coverage/Program.cs`
- Create: `dev/coverage-vm/CoverageVm.csproj` (`net10.0`, `win-x86`, self-contained single file, links `../coverage/CoverageModel.cs`), `dev/coverage-vm/Api32Coverage.cs`, `dev/coverage-vm/Program.cs`
- Create: `dev/coverage.Tests/` (tests for the parser and comparator)

**Interfaces:**
- `jobs.json`: an array of runs taken by hand from `third_party/ediabaslib/EdiabasLib/Test/Scripts/coverage.bat` lines 89–160 (the `ECU_TEST_PATH` runs only), `{ "run": "results10", "sgbd": "cmd_test1", "jobs": ["TEST_SHMID#ARGS##STDARGS", …], "formats": ["ERGB=W", …], "config": {"SystemResults": "0"} }`. The two `for` loops (lines 117, 160) are expanded. `TEST_TIME_FLAGS` (line 101) is left out: its argument is the current time. `--alltypes` is not carried (it prints API conversions, not EdiabasNet's values).
- Job strings follow `EdiabasTest`: `job#args#results#stdargs#sgbd`, an argument starting `|` is hex binary (`EdiabasTest/Program.cs:214-251`).
- JSONL record, one per job: `{"run","sgbd","job","ok","error":{"code","text"}|null,"sets":[[{"name","type","value","formatted":{"<fmt>":"…"}}]]}`; `value` is a number, string, or hex for binary.
- Mac runner: `dotnet run --project dev/coverage -- run --out mac.jsonl` — per run, a fresh EdiabasNet from `EdiabasFactory` with `EcuPath` = the upstream test folder, the run's config, and `KlineInterface` on an empty replay recording (an `xconnect` opens nothing; any send fails). Formats via `EdiabasNet.FormatResult`. Refuses any `sgbd` outside the five allowed names.
- VM runner: `CoverageVm.exe --ecu C:\bimmer-bridge-m1\Ecu --jobs jobs.json --out vm.jsonl` — `apiInit`, `apiSetConfig("EcuPath", …)` and the run's config, `apiJobExt` for standard arguments, `apiResultText(name, set, format)` for formats. Same refusal of other SGBD names. Declares only these api32 exports plus the result readers.
- `compare`: `dotnet run --project dev/coverage -- compare vm.jsonl mac.jsonl` prints per-job `match`/`differ` with the differing fields, and a count.
- [ ] **Step 1: Write the failing tests:** job-string parsing (`TEST_PARY#|12131415`, `TEST_SHMID###EXIT_EXCEPTION#cmd_test1.prg`, empty parts); refusal of `dm338ds1_interbyte`; comparator: equal records match, a value difference is reported with its set and name, a missing job is reported; `jobs.json` loads and every `sgbd` is allowed.
- [ ] **Step 2: Run** `$DOTNET test dev/coverage.Tests`. Expected: fail.
- [ ] **Step 3: Implement.** Then run the Mac side for real: `$DOTNET run --project dev/coverage -- run --out <scratch dir outside the repo>/mac.jsonl`. Expected: one line per job, no crash; note any job that threw outside EDIABAS's error handling.
- [ ] **Step 4: Run** the tests, `dev/check-no-bmw-files.sh`, and `$DOTNET publish dev/coverage-vm -r win-x86 -c Release` (build only). Expected: pass; one `CoverageVm.exe`.
- [ ] **Step 5: Commit** — `"Coverage job list from upstream's coverage.bat, with Mac and VM runners and a comparator"`

### Task 12: M1 results document and the desk sessions

**Files:**
- Create: `docs/native/m1-results.md`

Like `m0-results.md`: status line, who does what, and one section per acceptance item. **Claude does not run EDIABAS in the VM.**

1. **Policy case files** — test names and counts from Tasks 6/8 (Claude).
2. **Gate** — the Zig and C# gate tests by name, including latch and masking (Claude).
3. **Describe, every `.prg`** — desk session. Ryan unplugs the cable from the car (it may stay in the VM), confirms, and gives the go-ahead; then the describe diff runs (`dev/describe-diff.ts` against the VM agent and `ediabasd`). Table: counts by `kind`, and every `differ`/`*_error` row explained. Expected source of `vm_error` rows: real EDIABAS runs INIT before `_JOBS` (M0 section 4), so an SGBD whose INIT sends a telegram fails in the VM with the cable unpowered, while native describe does not run INIT. Groups: `needs_car` natively (list the count).
4. **Coverage three-way (two ways in M1)** — desk session, after Ryan's OK on decision 8. Claude copies `CoverageVm.exe`, `jobs.json` and the three test SGBDs (`cmd_test1.prg`, `cmd_test2.prg`, `cmd_ident.grp`) to `C:\bimmer-bridge-m1\` over the dev shell (nothing under `C:\EDIABAS` changes); Ryan runs `CoverageVm.exe` at the VM's desktop with the cable in the VM, not in the car; Claude copies `vm.jsonl` back and runs `compare`. Table: matches, differences, and each difference explained or fixed. Afterwards Claude removes `C:\bimmer-bridge-m1\` from the VM.
5. **Decisions carried to M2** — the open questions the sessions raise.

- [ ] **Step 1:** Write the document with the procedures and empty tables; fill sections 1–2.
- [ ] **Step 2:** Commit — `"M1 results document with the desk-session procedures"`
- [ ] **Step 3:** Ask Ryan for the two desk sessions (and decision 8's OK); fill sections 3–4 from them, committing each (`"M1 results: describe matches the VM for N of M SGBDs"` and so on). A difference that points to a bug in `ediabasd` is fixed under TDD in the task it belongs to, and re-run.

### Task 13: Whole-branch review

- [ ] **Step 1:** All suites green: `zig build test --summary all`, `zig build check-linux`, `$DOTNET test` for `engine/EdiabasLib.Native.Tests`, `engine/ediabasd-net.Tests`, `dev/census.Tests`, `dev/coverage.Tests`, `vm-agent/EdiabasAgent.Tests`; `cd dev && npx vitest run`; `cd mac-mcp && npm test` (untouched, must still pass); `dev/check-no-bmw-files.sh`.
- [ ] **Step 2:** One fresh reviewer (most capable model) reviews `git diff b31857d..HEAD` against the spec's M1 section, this plan, and the Review Focus list. Fix what it finds that holds up, one commit per fix.
- [ ] **Step 3:** Report to Ryan. Do not merge or push without asking.
