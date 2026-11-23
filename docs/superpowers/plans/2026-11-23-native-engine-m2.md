# Native engine M2: at the car — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `ediabasd` reads Ryan's E61 through the cable on the Mac and is shown to match the VM
in the same session: `IDENT` on `NVE_65` with the same telegrams, then fault memory, vehicle
status and the E60 whole-car reads. The MCP server gets an opt-in native backend
(`EDIABAS_BACKEND=native`) and `bridge_health`, and the echo timing that M0 could not measure
comes from the first real read.

**Architecture:**
- **Zig (`kline/`):** a latency choice at open (1 or 16 ms, for the echo measurement) and a
  cable-state check (absent, free or locked) that never opens the tty.
- **C# (`engine/ediabasd-net`):** a `latencyMs` config key, and `health` reporting the cable's
  state, the latency the last session applied and the policy's SHA-256.
- **TypeScript (`mac-mcp`):** an `EdiabasBackend` interface that `AgentClient` already fits, a
  `NativeClient` that runs `ediabasd serve` as a child over stdio, backend selection in config,
  and `bridge_health`. The `vm_*` tools exist only on the VM backend.
- **Dev tools (`dev/`):** a car-comparison runner (the same job list through the VM agent, then
  natively), and a telegram tool that compares EDIABAS's IFH trace with a `libkline` recording
  and measures echo and reply times.
- **Sessions:** a desk rehearsal with the cable on the Mac (no car), then Phase 6 (the VM's own
  first car session, as `docs/at-the-car.md` has it), then two M2 car sessions, written up in
  `docs/native/m2-results.md`.

**Tech Stack:** as M1: .NET 10 via `/opt/homebrew/opt/dotnet@10`, xunit; Zig 0.16.0;
Node/TypeScript with vitest (`mac-mcp`, `dev/`).

**Spec:** `docs/superpowers/specs/2026-11-22-native-engine-design.md` ("MCP server" and M2 under
"Milestones and acceptance", read with its "M1 amendments"). M1's results and notes for M2:
`docs/native/m1-results.md`, section 5. M0's echo-timing section: `docs/native/m0-results.md`,
section 6.

## Decisions this plan makes (read before reviewing)

1. **Echo timing comes from the first real read** (Ryan, 2026-11-23). No bench supply, and
   `kline loopback` stays locked to one. In session B, `NVE_65 IDENT` runs natively twice, at
   latency 1 ms and at 16 ms, with recording on; the telegram tool measures echo and reply times
   from the recordings' timestamps. That answers M0's open risk 3 (does 1 ms take effect on
   macOS?) with nothing sent but a read the car answers anyway.
2. **Phase 6 first, then M2** (Ryan). Trip A is Phase 6, VM only, exactly as
   `docs/at-the-car.md` has it, so the VM baseline is known good and the answering modules are
   known. Trips B and C are M2. In each, a read runs on the VM first, then the cable moves from
   UTM to the Mac and the same read runs natively. Ignition-on time stays under 10 minutes per
   trip; anything left over goes to another trip.
3. **The VM stays the default backend** (Ryan). The spec makes native the default when M2
   passes, but the strict gate refuses 24 reads natively (M1 section 2), including the
   `NVE_65 STATUS_BORDNETZSPANNUNG` voltage source. `EDIABAS_BACKEND` defaults to `vm`; native
   is opt-in until the gate is revisited. This plan ends by recording M2's result, not by
   switching.
4. **The Mac-side policy check stays on both backends in M2.** The spec drops it on native
   ("one policy check, in the engine"), but the TypeScript policy goes only with the VM backend
   (M4). Until then it costs nothing, keeps a refused job from reaching the child, and keeps
   one refusal path for both backends. The native preflight compares the engine's policy
   SHA-256 with the Mac's, as the VM preflight does (`policy_mismatch`).
5. **On native, vehicle status runs voltage sources as a separate request.** A gate refusal
   fails its whole request (M1), so a voltage source the gate refuses must not take the
   ignition and battery readings down with it. The fixed readings go first; the sources go in a
   second request, and a refused one shows as its own `[frame_refused]` line.
6. **`NativeClient` never resends.** One request at a time. If the child exits or stops
   answering during a `job` or `jobs` request, the call fails with `ediabas_request_lost` (it
   may have run), and the next call starts a fresh child. A request that cannot reach the car
   (`ecus`, `describe`, `health`) fails with `engine_unavailable` (new).
7. **The cable's state is read without opening it.** `kline_cable_state` checks the USB
   identity and try-locks the lock file in `/tmp` (`flock` with `LOCK_NB`, released at once). It
   never opens the tty, so `bridge_health` cannot toggle DTR or disturb a session.
8. **Telegrams are compared without checksums and echoes.** The IFH trace's `INPUT:` has no
   checksum; `libkline`'s `send` has one. The tool drops a trailing byte from either side when
   it is the 8-bit sum of the rest, and drops a native `recv` that repeats the preceding `send`
   (the K-line echo).
9. **Out of scope:** fast init and the Linux repeat (dropped in M0, section 9), relaxing the
   gate, the `.sim` exporter, committing recordings (they stay local: a `C_FA_LESEN` reply holds
   the VIN), switching the default, and removing the VM backend (M4).

## Global Constraints

- GPL-3.0-only. Our own engine; OpenDS2 and bimmerscope are references only. Read-only, always.
- Never commit a `.prg`/`.grp`/`.lib`. Run `dev/check-no-bmw-files.sh` before every commit.
  Ryan's SGBDs stay at `~/ediabas-ecu/Ecu`. Recordings, IFH traces and session outputs stay
  outside the repo, under `~/m2/`.
- **Nothing talks to the car without Ryan there and his go for that session** (tasks 7–9). Tasks
  1–5 are desk work: tests never open a real device (ptys, replays, fakes). Task 6 uses the real
  cable on the Mac with no car connected. `kline loopback` is never run on a car.
- At the car: a charger on for the whole session, the voltage noted, ignition-on time under 10
  minutes per trip, and `docs/at-the-car.md`'s rules (no retries after a refusal or a lost
  request; restart the VM if a job will not stop).
- The 24 gate-refused reads (M1 section 2) are left out of every native job list.
- VM shell: `ssh -o BatchMode=yes ediabas-vm`. Back up before changing anything, never print
  `token.txt`, and each `EDIABAS.INI` change needs Ryan's OK for that change (task 8 asks for
  one).
- No EDIABAS jobs, Tool32, INPA, NCS Expert or WinKFP over SSH; EDIABAS runs only through the
  agent or by Ryan.
- .NET: `DOTNET_ROOT=/opt/homebrew/opt/dotnet@10/libexec /opt/homebrew/opt/dotnet@10/bin/dotnet`
  (`$DOTNET` below). Zig `/opt/homebrew/bin/zig` 0.16.0; `zig build test --summary all` and
  `zig build check-linux` stay green.
- EdiabasLib stays unmodified at `5e9efc5`.
- Commit after each task, plain-sentence messages like the repo's history; note any small
  deviation from this plan in the message.
- Suites that must stay green: Zig; `engine/EdiabasLib.Native.Tests`; `engine/ediabasd-net.Tests`;
  `vm-agent/EdiabasAgent.Tests`; `dev/coverage.Tests`; `cd mac-mcp && npm test && npm run test:dev && npm run typecheck:dev`.

## Review Focus

1. **The `ediabasd` child dies or hangs mid-job** (exit 3 after a stuck job, a crash, no
   answer): the call is `ediabas_request_lost`, never resent, and the next call works with a
   fresh child. Test in task 3.
2. **The cable is held by the VM (passed through in UTM) while the native backend is in use**:
   the Mac sees no cable, every native read is `cable_not_found` with the "untick it in UTM"
   advice, and `bridge_health` says absent. Test in tasks 2 and 4.
3. **A voltage source the gate refuses** (`NVE_65 STATUS_BORDNETZSPANNUNG`) on native vehicle
   status: ignition and battery still show; the source shows `[frame_refused]`. Test in task 4.
4. **Two MCP calls at once on native**: the second waits for the first; the child never sees two
   requests interleaved. Test in task 3.
5. **An IFH trace and a recording that do not line up** (an extra INIT exchange on one side, a
   retry, a missing reply): the telegram tool reports the first point where they diverge, with
   both sides' bytes, rather than a bare "differ". Test in task 5.

---

### Task 1: Latency at open

**Files:**
- Modify: `kline/src/session.zig`, `kline/src/capi.zig`, `kline/include/kline.h`, `kline/README.md`
- Modify: `engine/EdiabasLib.Native/KlineNative.cs`, `KlineTransport.cs`
- Modify: `engine/ediabasd-net/NativeConfig.cs`, `EdiabasdHost.cs` (or wherever the live transport is built)
- Test: `kline/src/session.zig`, `kline/src/capi.zig`, `engine/ediabasd-net.Tests/NativeConfigTests.cs`, `engine/EdiabasLib.Native.Tests/KlineInterfaceTests.cs`

**Interfaces:**
- Produces:
  - Zig: `Session.openDeviceOpts` takes `latency_ms: u8 = 1` (1 or 16); the recording's `open`
    event says `"latency":"1ms"`, `"16ms"` or `"unsupported"`.
  - C: `int kline_open_ex(const char *path, const char *record_dir, unsigned latency_ms, kline_session **out);`
    — `latency_ms` must be 1 or 16, otherwise it returns `KLINE_UNSUPPORTED` (10) and opens
    nothing. `kline_open` stays and means latency 1.
    `const char *kline_latency(const kline_session *k);` — the label the open recorded
    (`"1ms"`, `"16ms"`, `"unsupported"`; `""` for a replay).
  - C#: `KlineTransport.Device(string? recordDir, int latencyMs = 1)`;
    `KlineTransport.LastOpenLatency` (static `string?`: the `open` event's latency label of the
    most recent device open in this process, null before any).
  - Config: `latencyMs` (1 or 16, default 1), for M2's measurement only.

- [ ] **Step 1: Write the failing tests.**
  - Zig `capi.zig`: `"kline_open_ex refuses a latency other than 1 or 16"` — 0, 2 and 255 return
    10 and leave `out` null, with a path that does not exist (so the check must come first).
  - Zig `session.zig`: `"open records the latency asked for, or unsupported"` — on a pty with a
    record dir, `latency_ms = 16`: the `open` event's latency is `"unsupported"` (a pty has
    none) and the session still opens.
  - C# `NativeConfigTests`: `LatencyMs_DefaultsTo1`, `LatencyMs_16_Parses`,
    `LatencyMs_Other_IsError` (5 → error naming `latencyMs: must be 1 or 16`).
  - C# `KlineInterfaceTests`: `LastOpenLatency_IsNullBeforeAnyOpen` (fresh process state via a
    reset hook marked internal for tests).
- [ ] **Step 2: Run** `cd kline && zig build test --summary all` and
  `$DOTNET test engine/ediabasd-net.Tests engine/EdiabasLib.Native.Tests`. Expected: compile
  failures.
- [ ] **Step 3: Implement.** `openDeviceOpts` calls `Port.setLatency(latency_ms)` in place of
  `setLowLatency` and keeps the label it records on the session. `KlineTransport` passes the
  config value to `kline_open_ex` and, after a successful device open, sets `LastOpenLatency`
  from `kline_latency`. Document `latencyMs` in `docs/api.md`'s ediabasd config.
- [ ] **Step 4: Run** the same commands plus `zig build check-linux`. Expected: all pass.
- [ ] **Step 5: Commit** — "kline and ediabasd: choose the cable's latency at open, 1 or 16 ms, for the echo measurement"

### Task 2: Cable state without opening it, in `ediabasd` health

**Files:**
- Modify: `kline/src/port.zig` (or a new `kline/src/cable_state.zig`), `kline/src/capi.zig`, `kline/include/kline.h`
- Modify: `engine/EdiabasLib.Native/KlineNative.cs`, `KlineLibrary.cs`
- Modify: `engine/ediabasd-net/Front.cs` (`CableHealth`, `NativeHealth`), `docs/api.md`
- Test: Zig beside the code; `engine/ediabasd-net.Tests/FrontTests.cs`

**Interfaces:**
- Consumes: the lock-file path rule in `port.zig` (`lockPath`, `/tmp` by default).
- Produces:
  - Zig: `pub const CableState = enum(c_int) { absent = 0, free = 1, locked = 2, not_ftdi = 3, io = 4 }`;
    `pub fn cableState(path: []const u8, lock_dir: ?[]const u8) CableState`.
  - C: `int kline_cable_state(const char *path);` (returns the enum value).
  - C#: `KlineLibrary.CableState(string path)` → `"absent" | "free" | "locked" | "unsupported" | "unknown"`.
  - Health: `cable: { state: "absent" | "free" | "locked" | "several" | "unsupported" | "unknown", port, latency }`
    (`latency` = `KlineTransport.LastOpenLatency`, null before any live session), and a new
    top-level `policySha256` (Core's `JobPolicy.Sha256`).

- [ ] **Step 1: Write the failing tests.**
  - Zig: `"cable state of a missing path is absent"`; `"cable state is free when nobody holds the lock"`
    and `"locked while another fd holds the lock"` (a pty path with `require_ftdi = false` via a
    test-only entry point and a temp lock dir; a second `flock(LOCK_EX)` on the same lock file
    held during the check); `"checking the state does not open the device"` (the check runs on a pty path chmod-ed to
    000, whose `open` would fail with EACCES, and still answers `free`).
  - C# `FrontTests`: `Health_ReportsCableStateLatencyAndPolicySha` — with `Port` set to a
    missing path: `cable.state == "absent"`, `cable.latency == null`, `policySha256` is the
    64-hex SHA of the embedded policy (equal to `JobPolicy.Embedded.Sha256`).
- [ ] **Step 2: Run** the Zig and `ediabasd` tests. Expected: failures (functions missing).
- [ ] **Step 3: Implement.** Identity check first (USB `0403:6001`, as `Port.open` does; a
  non-FTDI device is `not_ftdi`), then open the lock file and `flock(LOCK_EX | LOCK_NB)`:
  success → unlock, close, `free`; `EWOULDBLOCK` → `locked`. Never `open()` the tty. With no
  `port` configured and exactly one cable found, report that cable's state; with several, keep
  `"several"`.
- [ ] **Step 4: Run** all Zig, `EdiabasLib.Native.Tests` and `ediabasd-net.Tests`. Expected: pass.
- [ ] **Step 5: Commit** — "Cable state (absent, free or locked) in ediabasd health without opening the cable"

### Task 3: `NativeClient`: `ediabasd serve` as a child

**Files:**
- Create: `mac-mcp/src/backend.ts`, `mac-mcp/src/nativeClient.ts`
- Create: `mac-mcp/test/fakeEdiabasd.ts` (a scripted stand-in child), `mac-mcp/test/nativeClient.test.ts`
- Modify: `mac-mcp/src/errors.ts` (codes and advice), `docs/tools.md` ("Errors")

**Interfaces:**
- Consumes: `ediabasd serve` stdio (`docs/api.md`, "`ediabasd` stdio"); the zod schemas in
  `agentClient.ts` (`EcusSchema`, `DescribeListSchema`, `DescribeJobSchema`, `JobResultSchema`,
  `JobsResponseSchema`).
- Produces:
  - `backend.ts`: `export interface EdiabasBackend { readonly kind: "vm" | "native"; listEcus(filter?: string): Promise<EcuList>; describeJobs(q: { ecu: string; filter?: string; comments?: boolean }): Promise<DescribeList>; describeJob(q: { ecu: string; job: string }): Promise<DescribeJob>; runJob(body: { ecu: string; job: string; args?: string; results?: string; timeoutSeconds?: number }): Promise<JobResult>; runJobs(items: JobRequest[], timeoutSeconds?: number): Promise<JobsResponse>; }`.
    `AgentClient` gets `readonly kind = "vm"` and is unchanged otherwise.
  - `nativeClient.ts`: `export const NativeHealthSchema` (zod, the shape from task 2) and
    `type NativeHealth`; `export class NativeClient implements EdiabasBackend` with
    `constructor(opts: { binary: string; configPath?: string; env?: Record<string, string>; spawn?: SpawnFn; requestTimeoutMs?: number; log: (m: string) => void })`,
    `health(): Promise<NativeHealth>`, `close(): void`.
  - New error codes: `engine_unavailable` (local), and advice for the engine's own
    `cable_unsupported`, `cable_permission`, `frame_refused` and `needs_car`.

- [ ] **Step 1: Write `fakeEdiabasd.ts`**: a Node script that reads JSON lines on stdin and
  answers from a script passed in `FAKE_EDIABASD_SCRIPT` (a JSON array of
  `{ "answer": {...} } | { "exit": n } | { "hang": true } | { "delayMs": n, "answer": {...} }`),
  echoing each request to a log file named in `FAKE_EDIABASD_LOG`, so tests can assert what the
  child received.
- [ ] **Step 2: Write the failing tests** in `nativeClient.test.ts`:
  - `"sends one JSON line per request and parses the result"`: `describeJobs({ecu:"NVE_65"})`
    sends `{"id":1,"method":"describe","params":{"ecu":"NVE_65"}}` and returns the parsed list.
  - `"an engine error becomes a BridgeError with its code"`: `{ok:false,error:{code:"cable_not_found",message:"m"}}`
    → `BridgeError` with code `cable_not_found`.
  - `"two calls at once reach the child one after the other"`: start `runJob` and `listEcus`
    together; the child's log shows request 2 only after answer 1 was written (fake uses
    `delayMs`).
  - `"the child exiting during a job is ediabas_request_lost and is not resent"`: script
    `[{exit:3}, {answer:…}]` (the second line belongs to the restarted child); `runJob` rejects
    with `ediabas_request_lost`; the log shows the job request once; the next `listEcus` succeeds
    through a new child.
  - `"no answer within the timeout during a job is ediabas_request_lost"` (`hang`,
    `requestTimeoutMs: 200`); the child is killed.
  - `"the child exiting during describe is engine_unavailable"`.
  - `"a missing binary is engine_unavailable with the path in the message"`.
  - `"an answer that fails its schema is bad_response"`.
  - `"env is passed to the child"` (`DOTNET_ROOT` visible in the fake's log).
- [ ] **Step 3: Run** `cd mac-mcp && npx vitest run test/nativeClient.test.ts`. Expected: fail
  (module missing).
- [ ] **Step 4: Implement** `NativeClient`: spawn lazily on the first request
  (`[binary, "serve", ...(configPath ? ["--config", configPath] : [])]`, stdio pipes), a promise
  chain as the one-at-a-time queue, `readline` on stdout matched by `id`, stderr kept (last 2 KB)
  for messages. `requestTimeoutMs` defaults to 75 000 (the VM client's). Add the new codes to
  `LOCAL_ERROR_CODES` and their advice in `errors.ts`; list them in `docs/tools.md` (the
  `errorCodesDoc` test checks).
- [ ] **Step 5: Run** the file, then `npm test`. Expected: all pass.
- [ ] **Step 6: Commit** — "MCP server: NativeClient runs ediabasd serve as a child, one request at a time, never resending"

### Task 4: Backend selection, `bridge_health`, and the native tool set

**Files:**
- Modify: `mac-mcp/src/config.ts`, `server.ts`, `faults.ts`, `format.ts` (a `formatBridgeHealth`)
- Modify: `config.local.example.json`, `docs/tools.md`, `docs/development.md` or `README.md` (how to try native), `skills/bimmer-bridge/SKILL.md`
- Test: `mac-mcp/test/config.test.ts`, `mac-mcp/test/tools.test.ts` (or a new `nativeTools.test.ts`), `mac-mcp/test/format.test.ts`

**Interfaces:**
- Consumes: `EdiabasBackend`, `NativeClient`, `NativeHealth` (task 3).
- Produces:
  - Config: `backend: "vm" | "native"` (env `EDIABAS_BACKEND`, else `config.local.json`
    `backend`, default `"vm"`; anything else is a `config_error`); `ediabasdPath` (env
    `EDIABASD_PATH`, else key, default `<repo>/engine/ediabasd-net/bin/Release/net10.0/ediabasd`);
    `ediabasdConfig` (key, optional; absent means ediabasd's own XDG default); `dotnetRoot`
    (key, optional; passed to the child as `DOTNET_ROOT` when set).
  - `ServerParts` gains `ediabas: EdiabasBackend` and `nativeHealth?: () => Promise<NativeHealth>`;
    `client` (the VM `AgentClient`) becomes optional and is present only on `vm`.
  - Tool set: on `vm` exactly today's eleven tools. On `native`: `bridge_health` plus the six
    `ediabas_*` tools; no `vm_*` tool. `INSTRUCTIONS` has a native variant that says to call
    `bridge_health` first and drops the VM sentences.
  - `formatBridgeHealth(h: NativeHealth, macPolicyVersion: number | undefined, mac: {version, startedAt}, macPolicySha: string | undefined): string`.

- [ ] **Step 1: Write the failing tests.**
  - `config.test.ts`: `"backend defaults to vm"`, `"EDIABAS_BACKEND=native selects native"`,
    `"config.local.json backend is read, and the env wins"`, `"an unknown backend is a config error"`.
  - Native tool-set test (a server built with a stub `EdiabasBackend` and stub `nativeHealth`):
    `"native registers bridge_health and the ediabas tools, no vm tools"` (list names exactly);
    `"vm registers today's tools and no bridge_health"`.
  - `"bridge_health shows engine, version, ecuPath, cable state and latency, and policy version"`
    — text contains `cable: free (/dev/cu.usbserial-X, latency 1ms)` for a stub health.
  - `"bridge_health on a cable passed to the VM says absent and how to fix it"` — state
    `absent`: the text says the cable is not on the Mac and to untick it in UTM's USB menu if
    the VM has it (Review Focus 2).
  - `"a native preflight with a different policy SHA refuses with policy_mismatch"`.
  - `"native vehicle status sends voltage sources in a second request"` — stub records two
    `runJobs` calls: first `UTILITY STATUS_ZUENDUNG, UTILITY STATUS_UBATT, TMODE …KL30, TMODE …KL15`,
    then the configured sources; a stub answer of `frame_refused` for the second still shows the
    ignition and battery blocks, plus `[frame_refused]` for the source (Review Focus 3).
  - `"vm vehicle status is unchanged: one request"`.
- [ ] **Step 2: Run** `cd mac-mcp && npm test`. Expected: the new tests fail.
- [ ] **Step 3: Implement.** `createAppServer` builds either `AgentClient` or `NativeClient`
  from `config.backend`. `buildServer` registers `vm_*` only when `client` is present, and
  `bridge_health` only on native; the `ediabas_*` handlers call `parts.ediabas` in place of
  `client`; `preflight()` uses `client.health()` on vm and `nativeHealth()` on native (policy
  SHA check only; there is no approvals watch on native, as `ediabasd` reads `reviewedJobs` at
  start). Tool descriptions that say "needs the cable attached to the VM" become neutral ("needs
  the cable"). Vehicle status on native: `runBatch` once for the fixed four, once for the
  sources, results merged in the same order as today's single batch. Skill: on native, call
  `bridge_health` first in place of `vm_health`.
- [ ] **Step 4: Run** `npm test && npm run test:dev && npm run typecheck:dev`. Expected: pass.
- [ ] **Step 5: Commit** — "MCP server: opt-in native backend (EDIABAS_BACKEND=native) with bridge_health; vm_* tools only on the VM backend"

### Task 5: Car comparison tools

**Files:**
- Create: `dev/telegrams-lib.ts`, `dev/telegrams.ts` (CLI), `dev/car-compare-lib.ts`, `dev/car-compare.ts` (CLI)
- Create: `dev/test/telegrams.test.ts`, `dev/test/car-compare.test.ts`
- Create: `dev/m2/session-b.json`, `dev/m2/session-c.json` (job lists; no car data)
- Modify: `dev/README.md`

**Interfaces:**
- Consumes: `AgentClient` and `NativeClient` from `mac-mcp/dist` (as `describe-diff.ts` loads
  `AgentClient`); recordings (`kline/src/recording.zig`'s format).
- Produces:
  - `telegrams-lib.ts`:
    - `type Exchange = { request: number[]; response: number[] | null; error: string | null; tUs?: number; echoUs?: number; replyUs?: number }`
    - `parseIfhTrace(text: string): Exchange[]` — one exchange per `SENDECUTEL` block: request
      from `INPUT:` (continuation lines included), response from `OUTPUT:` (`--` is null), error
      from `ERROR:`. Other calls (`SETECUCOMM`, `I -> D`/`D -> I` driver lines) are skipped.
    - `parseRecording(jsonl: string): { exchanges: Exchange[]; latency: string | null; shortReads: number; refused: number }`
      — a `send` starts an exchange; a `recv` equal to that send is its echo (sets `echoUs`);
      the next `recv` bytes up to the next `send` are the response (`replyUs` = first one's
      time minus the echo's); a `recv` with a `reason` counts as a short read.
    - `stripChecksum(t: number[]): number[]` — drops the last byte when it equals the 8-bit sum
      of the others.
    - `compareExchanges(vm: Exchange[], native: Exchange[]): { matched: number; firstDivergence: null | { index: number; vm: Exchange | null; native: Exchange | null } }`
      — pairwise after `stripChecksum` on both sides of both requests and responses.
    - `timing(exchanges: Exchange[]): { echo: Stats; reply: Stats }` with
      `Stats = { n: number; minMs: number; medianMs: number; maxMs: number }`.
  - `telegrams.ts compare <ifh.trc> <recording.jsonl>` and `telegrams.ts timing <recording.jsonl>…`
    (one table row per recording, with its latency label).
  - `car-compare-lib.ts`: `compareJobResults(vm: unknown, native: unknown): { kind: "match" | "differ" | "vm_error" | "native_error" | "both_error"; differences: { path: string; vm: unknown; native: unknown }[] }`
    — results compared per set by name (type and value), system results excluded, `durationMs`
    ignored, and EdiabasLib's extra `GRUPPE`/`FAMILIE` ignored (M1 section 4).
  - `car-compare.ts run --side vm|native --jobs dev/m2/session-c.json --out FILE [--ediabasd PATH] [--ediabasd-config PATH]`
    (one line per job: `{ ecu, job, response }`, in the job list's order, each job sent alone so
    one failure never hides the rest; the VM side finds the agent as `describe-diff.ts` does)
    and `car-compare.ts compare VM.jsonl NATIVE.jsonl`.

- [ ] **Step 1: Write the failing tests** (synthetic trace text in the format of
  `docs/native/m0-results.md` section 4 and the VM sample: `_STD     [  16] SENDECUTEL`,
  `_STD     [  16]  INPUT: 82 12 F1 1A 80`, `… OUTPUT: --`, `… ERROR: IFH_0003`):
  - `"parses a SENDECUTEL block with no reply"` → `{request:[0x82,0x12,0xF1,0x1A,0x80], response:null, error:"IFH_0003"}`.
  - `"joins INPUT continuation lines"`.
  - `"recording: echo dropped, response kept, times measured"` — send at 1000 µs, recv echo at
    2100, recv response at 9100: `echoUs 1100`, `replyUs 7000`.
  - `"a recv with a reason counts as a short read"`.
  - `"checksum on one side only still matches"` (`82 12 F1 1A 80` vs `82 12 F1 1A 80 9F`).
  - `"first divergence names the index and both sides"` — VM has an extra exchange at 1
    (Review Focus 5).
  - `"timing stats: min, median, max"`.
  - `compareJobResults`: `"same results in a different order match"`,
    `"a differing value is reported with set and name"`, `"GRUPPE and FAMILIE are ignored"`,
    `"error on one side is vm_error / native_error"`.
- [ ] **Step 2: Run** `cd mac-mcp && npm run test:dev`. Expected: fail.
- [ ] **Step 3: Implement**, and write the job lists:
  - `session-b.json`: `[{"ecu":"NVE_65","job":"IDENT"}]`.
  - `session-c.json`: `FS_LESEN` on the `docs/at-the-car.md` modules that trip A showed
    answering (fill after task 7; until then `NVE_65` only), then `UTILITY STATUS_ZUENDUNG`,
    `UTILITY STATUS_UBATT`, `TMODE LESE_SPANNUNG_KL30`, `TMODE LESE_SPANNUNG_KL15`, then `E60`
    `IDENT_FUNKTIONAL` and `FS_LESEN_FUNKTIONAL` (each `timeoutSeconds: 60`), then `CAS
    C_FA_LESEN`. None of the 24 gate-refused reads.
- [ ] **Step 4: Run** `npm run test:dev && npm run typecheck:dev`. Expected: pass.
- [ ] **Step 5: Commit** — "Car comparison tools: telegrams against the IFH trace, echo timing, and a VM-then-native job runner"

### Task 6: Desk rehearsal with the cable on the Mac (no car)

**Files:**
- Create: `docs/native/m2-results.md` (all sections, procedures first; this task fills section 1)

**Ryan, at the desk, cable plugged into the Mac and not into a car or the VM.** Nothing here
reaches a car: with no 12 V on pin 16 there is no echo and no module.

- [ ] **Step 1: Write `docs/native/m2-results.md`** with sections: 1 desk rehearsal, 2 trip A
  (Phase 6), 3 trip B (`IDENT` and echo timing), 4 trip C (fault memory, vehicle status,
  whole-car), 5 results against the spec's M2 list, 6 for M3. Each car section has its
  before-the-trip checklist, the exact commands, and empty result tables.
- [ ] **Step 2: Build release binaries.** `$DOTNET build -c Release engine/ediabasd-net` and
  `cd mac-mcp && npm run build`. Expected: no warnings.
- [ ] **Step 3: Rehearse (Ryan's go).**
  - `ediabasd health`: `cable.state` is `free`, the port is the FTDI cable.
  - With `latencyMs` 1, then 16, and `recordDir` `~/m2/desk/`: `ediabasd job NVE_65 IDENT`.
    Expected: an error with no reply (no car), a recording whose `open` says `1ms` / `16ms`, one
    `send` of `IDENT`'s telegram, and no `refused` event.
  - `telegrams.ts timing ~/m2/desk/*.jsonl`: runs, reports zero echoes.
  - While a job runs, `ediabasd health` from a second shell says `locked`.
  - Tick the cable for the VM in UTM: `ediabasd health` says `absent`; a native job is
    `cable_not_found`. Untick it again.
  - With `EDIABAS_BACKEND=native` in a scratch MCP registration: `bridge_health` answers, and
    `ediabas_describe NVE_65` works (offline). Restore the production registration afterwards.
- [ ] **Step 4: Fill section 1** and commit — "M2 results: the desk rehearsal with the cable on the Mac"

### Task 7: Trip A — Phase 6 (VM only)

**Ryan at the car; Claude through the MCP tools.** Exactly `docs/at-the-car.md`, unchanged: this
is the VM bridge's own first car session, and it gives M2 its baseline.

- [ ] **Step 1:** Before the trip, the checklist's "Before going to the car" items.
- [ ] **Step 2:** The session as the checklist has it (charger on, ignition-on time noted).
- [ ] **Step 3:** Record in `m2-results.md` section 2: which modules `IDENT_FUNKTIONAL` showed
  answering, how long each functional job took, ignition-on time, and anything that failed.
  Fill `dev/m2/session-c.json`'s `FS_LESEN` list with the answering `docs/at-the-car.md`
  modules (leaving out any gate-refused read).
- [ ] **Step 4:** Commit — "M2 results: trip A (Phase 6) baseline; session C's module list"

### Task 8: Trip B — `IDENT` on `NVE_65`, telegrams and echo timing

**Ryan at the car.** Before the trip, in the VM, with Ryan's OK for this change: back up
`C:\EDIABAS\Bin\EDIABAS.INI` and set `IfhTrace = 3` (as M0 section 4). Restore it after the trip
and check its SHA-256 against the backup.

- [ ] **Step 1: VM side** (cable ticked for the VM, ignition on, note the time):
  `node dev/car-compare.ts run --side vm --jobs dev/m2/session-b.json --out ~/m2/b-vm.jsonl`.
  Copy `C:\EDIABAS\TRACE\ifh.trc` to `~/m2/b-ifh.trc` over the dev shell.
- [ ] **Step 2: Move the cable:** untick it in UTM. `ediabasd health`: `free`.
- [ ] **Step 3: Native side, twice:** two `ediabasd` configs written before the trip,
  `~/m2/ediabasd-1.json` and `~/m2/ediabasd-16.json` (`ecuPath`, `recordDir` `~/m2/b/`,
  `latencyMs` 1 and 16):
  `node dev/car-compare.ts run --side native --ediabasd-config ~/m2/ediabasd-1.json --jobs dev/m2/session-b.json --out ~/m2/b-native-1.jsonl`,
  then the same with `-16`. Ignition off. Note the total time.
- [ ] **Step 4: Compare:** `car-compare.ts compare` for each native file against `b-vm.jsonl`;
  `telegrams.ts compare ~/m2/b-ifh.trc <recording>` for the latency-1 recording;
  `telegrams.ts timing ~/m2/b/*.jsonl`.
- [ ] **Step 5: Fill section 3:** results match (or every difference explained), telegrams
  match (or the first divergence explained), the echo/reply table at 1 and 16 ms, short reads.
  If latency 1 is no faster than 16, note open risk 3 as real (the fallback is a longer
  `ObdAddRecTimeout`, for M3). Commit — "M2 results: trip B, NVE_65 IDENT matches the VM; echo timing at 1 and 16 ms"

### Task 9: Trip C — fault memory, vehicle status and the whole-car reads

**Ryan at the car.** No INI change. If the job list will not fit in 10 minutes ignition-on on
both sides, split it at a job boundary across two trips; each half is compared on its own.

- [ ] **Step 1: VM side:** `car-compare.ts run --side vm --jobs dev/m2/session-c.json --out ~/m2/c-vm.jsonl`.
- [ ] **Step 2: Move the cable** (untick in UTM; `ediabasd health` says `free`).
- [ ] **Step 3: Native side** (`~/m2/ediabasd-c.json`: `recordDir` `~/m2/c/`, latency 1):
  `car-compare.ts run --side native --ediabasd-config ~/m2/ediabasd-c.json --jobs dev/m2/session-c.json --out ~/m2/c-native.jsonl`.
  Ignition off.
- [ ] **Step 4: Compare** with `car-compare.ts compare`; count IFH-0003 errors on each side and
  short reads (`telegrams.ts timing` reports them per recording).
- [ ] **Step 5: Fill section 4:** per job, match or the explained difference (live values such
  as voltages may differ between the two reads; fault entries and identities may not). Commit —
  "M2 results: trip C, fault memory, vehicle status and whole-car reads against the VM"

### Task 10: M2 results and whole-branch review

- [ ] **Step 1: Section 5** of `m2-results.md`: the spec's M2 list item by item (1, 2; 3 and 4
  dropped by M0), each with its evidence or what is missing. Record Ryan's decision that the VM
  stays the default and why (the 24 gate-refused reads). Section 6: what M3 needs (the
  recordings as replay fixtures, the latency answer, any divergence found).
- [ ] **Step 2: Spec amendment** under "M1 amendments" (or a new "M2 amendments"): decisions 3,
  4 and 5 above.
- [ ] **Step 3: Whole-branch review** of the M2 range by a fresh reviewer, Review Focus included;
  fix Critical and Important findings test-first.
- [ ] **Step 4: Commit** — "M2 results: written up against the spec; the VM stays the default"
