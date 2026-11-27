# Native engine M0: desk census and cable bring-up — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Answer, without the car, the questions that decide M1 and M3. These are which protocol
concepts Ryan's SGBDs use, whether any unimplemented opcodes occur, and whether real EDIABAS
runs INIT for `_JOBS`. Along the way, bring up the Zig cable layer far enough to open, lock and
configure the K+DCAN cable on macOS and Linux.

**Architecture:** Two independent tracks.
- **C#:** a library project links the spike's seven EdiabasLib core files from a pinned
  submodule. A `census` dev tool uses it with an in-memory capture interface to record what
  each SGBD's `INITIALISIERUNG` asks of the cable. It also disassembles every SGBD with
  upstream's MIT BESTDIS to count opcodes.
- **Zig:** `kline/` starts the cable layer with the `Port` type (open, lock, identity, line
  settings, modem lines, break, latency, timed receive) and a `kline` CLI. No C ABI, frame gate
  or recorder yet; those are M1.

M0 ends with a results document that Ryan completes from his desk session.

**Tech Stack:** .NET 10 SDK (10.0.404, Homebrew), xunit 2.9.3. Zig 0.16.0 (`/opt/homebrew/bin/zig`),
linking libc, plus IOKit and CoreFoundation on macOS. EdiabasLib at commit
`5e9efc5322eb01ac9703cf1fcafd0b2c37571ca0`.

**Spec:** `docs/superpowers/specs/2026-11-22-native-engine-design.md` (M0 under "Milestones and
acceptance"). Background: `docs/research/native-engine/report.md`.

**Later plans:** M1 (C ABI + gate + recorder + `ediabasd` .NET), M2 (at the car), M3 (Zig
engine) and M4 (publish) each get their own plan once M0's results are in, because those
results change their scope.

## Global Constraints

- Licence GPL-3.0 for the repo. (The spec adds `LICENSE` at M4; this plan adds it in Task 1,
  because Task 1 is when GPL code first enters the tree.)
- No `.prg`/`.grp`/`.lib` file is ever committed. Upstream test SGBDs are used in place from
  `third_party/ediabaslib/EdiabasLib/Test/Ecu`. `dm338ds1_interbyte` and `d60m47a0_dcan` are
  never used.
- Ryan's SGBDs live outside the repo (default `~/ediabas-ecu`); tools take the path as an
  argument.
- Nothing in M0 talks to a car. `kline loopback` is for a bench 12 V supply only.
- Pin EdiabasLib `5e9efc5322eb01ac9703cf1fcafd0b2c37571ca0`, Zig `0.16.0`, .NET `net10.0`.
- EdiabasLib files are linked unmodified; any patch would go in `third_party/patches/` with a
  reason (none expected in M0).
- The FT232R is identified as USB `0403:6001`.
- On macOS, `IOSSDATALAT` takes milliseconds (use 1, never 0), and the value resets on replug.
- macOS arm64 ioctl values:

  | ioctl | value |
  |---|---|
  | `TIOCEXCL` | 0x2000740d |
  | `TIOCSBRK` | 0x2000747b |
  | `TIOCCBRK` | 0x2000747a |
  | `TIOCMBIS` | 0x8004746c |
  | `TIOCMBIC` | 0x8004746b |
  | `TIOCMGET` | from SDK header |
  | `IOSSIOSPEED` | 0x80085402 |
  | `IOSSDATALAT` | 0x80085400 |
  | `FIONREAD` | 0x4004667f |

- Commit messages are plain sentences, matching the repo's history.

## Review Focus

1. **SGBD names in mixed case** (`NVE_65.PRG`, `e60.prg`): the census finds every `.prg`
   whatever its case, and reports each once.
2. **A corrupt or truncated SGBD in the folder**: the census records an `error` row for that
   file and carries on with the rest.
3. **The cable unplugged or missing**: `Port.open` returns `error.NoDevice`, never a crash or
   hang, and the CLI prints a one-line reason.
4. **A second process or tool already holding the cable**: the second `Port.open` returns
   `error.Locked`.
5. **A lock file left by a crashed run**: a stale lock file that no process holds does not
   block the next open.

---

### Task 1: Pinned EdiabasLib and the native core library

**Files:**
- Create: `.gitmodules`, `third_party/ediabaslib` (submodule at the pinned commit)
- Create: `LICENSE` (GPL-3.0 full text, from https://www.gnu.org/licenses/gpl-3.0.txt)
- Modify: `.gitignore` (add `*.lib` and `*.LIB` under the BMW-files block)
- Create: `engine/EdiabasLib.Native/EdiabasLib.Native.csproj`
- Create: `engine/EdiabasLib.Native/OfflineInterface.cs`
- Create: `engine/EdiabasLib.Native/EdiabasFactory.cs`
- Create: `engine/EdiabasLib.Native.Tests/EdiabasLib.Native.Tests.csproj`, `engine/EdiabasLib.Native.Tests/OfflineInterfaceTests.cs`, `engine/EdiabasLib.Native.Tests/TestPaths.cs`
- Create: `dev/check-no-bmw-files.sh`

**Interfaces:**
- Produces:
  - `EdiabasLib.Native.OfflineInterface : EdiabasLib.EdInterfaceBase`. This is the spike's
    `EdInterfaceOffline` (`~/projects/ediabaslib-spike/spike/stubs/EdInterfaceOffline.cs`),
    renamed, with the same refusal behaviour, throwing `OfflineInterfaceException`.
  - `EdiabasLib.Native.EdiabasFactory.Create(string ecuPath, EdInterfaceBase iface, bool noInitForVJobs) : EdiabasNet`.
    It registers `CodePagesEncodingProvider` once, sets `EcuPath`, sets `EdInterfaceClass`
    and sets `NoInitForVJobs`.
  - `TestPaths.UpstreamTestEcu : string`, the absolute path to
    `third_party/ediabaslib/EdiabasLib/Test/Ecu`, found by walking up to the repo root.

- [ ] **Step 1: Add the submodule at the pinned commit**

Run:
```bash
git submodule add https://github.com/uholeschak/ediabaslib third_party/ediabaslib
git -C third_party/ediabaslib checkout 5e9efc5322eb01ac9703cf1fcafd0b2c37571ca0
```
Expected: `git -C third_party/ediabaslib rev-parse HEAD` prints `5e9efc5322eb01ac9703cf1fcafd0b2c37571ca0`.

- [ ] **Step 2: Write `dev/check-no-bmw-files.sh` and run it**

The script exits 1 and lists any tracked path in the superproject (`git ls-files`, not
recursing into submodules) ending in `.prg`, `.grp` or `.lib`, case-insensitively. Otherwise
it exits 0.

Run: `dev/check-no-bmw-files.sh`. Expected: exit 0.

- [ ] **Step 3: Write the failing tests**

```csharp
public class OfflineInterfaceTests
{
    [Fact]
    public void JobsOnUpstreamTestSgbd_ListsTestIfaceFlags()
    {
        using var ed = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, new OfflineInterface(), noInitForVJobs: true);
        ed.ResolveSgbdFile("cmd_test2");
        ed.ExecuteJob("_JOBS");
        var names = ed.ResultSets.Skip(1).Select(s => (string)s["JOBNAME"].OpData).ToList();
        Assert.Contains("TEST_IFACE_FLAGS", names);
    }

    [Fact]
    public void InterfaceCall_IsRefusedOffline()
    {
        using var ed = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, new OfflineInterface(), noInitForVJobs: true);
        ed.ResolveSgbdFile("cmd_test2");
        var ex = Assert.ThrowsAny<Exception>(() => ed.ExecuteJob("TEST_IFACE_FLAGS"));
        Assert.Contains(Chain(ex), e => e is OfflineInterfaceException);
    }

    static IEnumerable<Exception> Chain(Exception e) { for (; e != null; e = e.InnerException) yield return e; }
}
```

(The result-set accessors are EdiabasNet's; the spike's `Program.cs` shows the exact calls.)

- [ ] **Step 4: Run them to see them fail**

Run: `dotnet test engine/EdiabasLib.Native.Tests`. Expected: build fails, because the project
and types don't exist yet.

- [ ] **Step 5: Create the library project and the two classes**

`EdiabasLib.Native.csproj` targets `net10.0`, as a library with `Nullable` disabled. It links,
as `Compile Include` with `Link`, exactly these files from `third_party/ediabaslib/EdiabasLib/EdiabasLib/`:
`EdiabasNet.cs`, `EdOperations.cs`, `EdInterfaceBase.cs`, `IniFile.cs`, `MemoryStreamReader.cs`,
`EdSimFile.cs` and `EdBase32.cs`. It sets `TreatWarningsAsErrors` true and never imports
upstream's `.csproj` or `Directory.Build.*`. The test project references it and uses xunit
2.9.3, `Microsoft.NET.Test.Sdk` 17.14.1 and `xunit.runner.visualstudio` 3.1.4.

- [ ] **Step 6: Run the tests**

Run: `dotnet test engine/EdiabasLib.Native.Tests`. Expected: 2 passed, 0 warnings.

- [ ] **Step 7: Add `LICENSE` and the `.gitignore` lines, re-run the check, and commit**

```bash
dev/check-no-bmw-files.sh
git add .gitmodules third_party/ediabaslib LICENSE .gitignore engine dev/check-no-bmw-files.sh
git commit -m "Pin EdiabasLib as a submodule and build its interpreter core natively (GPL-3.0)"
```

---

### Task 2: Concept census

**Files:**
- Create: `engine/EdiabasLib.Native/CaptureInterface.cs`
- Create: `dev/census/Census.csproj`, `dev/census/Program.cs`, `dev/census/ConceptCensus.cs`
- Create: `engine/EdiabasLib.Native.Tests/CaptureInterfaceTests.cs`, `dev/census.Tests/Census.Tests.csproj`, `dev/census.Tests/ConceptCensusTests.cs`

**Interfaces:**
- Consumes: `EdiabasFactory.Create`, `TestPaths.UpstreamTestEcu` (Task 1).
- Produces:
  - `EdiabasLib.Native.CaptureInterface : EdInterfaceBase`. It is entirely in memory and
    opens nothing.
    - `Connected` is true after `InterfaceConnect`.
    - Overriding the `CommParameter` setter records `uint[] LastCommParameter` (a copy) and
      `int ParameterSets`, the number of times it was set.
    - Battery and ignition voltage reads return 12000 (mV) and are recorded in
      `IReadOnlyList<string> Calls`.
    - Every transmit, reset, boot, port, prog-voltage or SI-relay call throws
      `CaptureStopException`, also recorded in `Calls`.
  - `ConceptCensus.Run(string ecuPath) : IReadOnlyList<CensusRow>`.
  - `record CensusRow(string File, string Status, uint? Concept, uint? Baud, uint[] Params, string[] Calls, string Error)`.
    `Status` is `ok`, `no_xsetpar` or `error`.
  - The CLI: `census concepts <ecuPath> [--json <out>]` prints a per-file table and then a
    tally of concept → count, with baud rates.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] // cmd_test2's TEST_IFACE_FLAGS sets BMW-FAST 115200 (cmd_test2.b2v:5331)
public void CapturesXsetparFromTestIfaceFlags()
{
    var cap = new CaptureInterface();
    using var ed = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, cap, noInitForVJobs: true);
    ed.ResolveSgbdFile("cmd_test2");
    try { ed.ExecuteJob("TEST_IFACE_FLAGS"); } catch (Exception) { /* CaptureStop or job error after xsetpar */ }
    Assert.Equal(new uint[] { 0x010F, 115200, 1200, 20, 10, 2, 5000 }, cap.LastCommParameter);
}

[Fact]
public void Census_RowsForEveryPrgOnce_NoGrp()
{
    var rows = ConceptCensus.Run(TestFiles.TempCopy("cmd_test1.prg", "cmd_test2.prg", "base1.prg", "base2.prg", "cmd_ident.grp"));
    Assert.Equal(4, rows.Count);
    Assert.DoesNotContain(rows, r => r.File.EndsWith(".grp", StringComparison.OrdinalIgnoreCase));
    Assert.Equal("no_xsetpar", rows.Single(r => r.File.Equals("cmd_test1.prg", StringComparison.OrdinalIgnoreCase)).Status);
}

[Fact] // Review Focus 1
public void Census_FindsUpperCaseExtension()
{
    var dir = TestFiles.TempCopyAs("cmd_test1.prg", "CMD_TEST1.PRG");
    Assert.Single(ConceptCensus.Run(dir));
}

[Fact] // Review Focus 2
public void Census_CorruptFile_IsErrorRow_AndOthersStillRun()
{
    var dir = TestFiles.TempCopy("cmd_test1.prg");
    File.WriteAllBytes(Path.Combine(dir, "junk.prg"), new byte[] { 1, 2, 3 });
    var rows = ConceptCensus.Run(dir);
    Assert.Equal("error", rows.Single(r => r.File == "junk.prg").Status);
    Assert.Equal("no_xsetpar", rows.Single(r => r.File.Equals("cmd_test1.prg", StringComparison.OrdinalIgnoreCase)).Status);
}
```

Put the helpers in `dev/census.Tests/TestFiles.cs`:
- `TestFiles.TempCopy(params string[] names) : string` copies the named files from
  `TestPaths.UpstreamTestEcu` into a fresh temp directory and returns it.
- `TestFiles.TempCopyAs(string name, string newName) : string` does the same for one file under
  a new name.

The temp directories are deleted when the test run ends. Tests only ever copy `cmd_test1`,
`cmd_test2`, `base1`, `base2` and `cmd_ident`, so the census never sees upstream's
BMW-derived files.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test engine/EdiabasLib.Native.Tests && dotnet test dev/census.Tests`.
Expected: compile errors, because the types don't exist yet.

- [ ] **Step 3: Implement `CaptureInterface` and `ConceptCensus.Run`**

For each `.prg` (case-insensitive extension), `Run` creates a fresh EdiabasNet with a fresh
`CaptureInterface` and `noInitForVJobs: true`, then calls `ResolveSgbdFile(name)` and
`ExecuteJob("INITIALISIERUNG")`. It catches every exception and keeps whatever was captured:
- the status is `ok` if `ParameterSets > 0`, else `no_xsetpar`;
- or `error` when the file could not be resolved or parsed, with the exception chain's
  messages in `Error`;
- `Concept = Params[0]`, `Baud = Params[1]` when present.

Files are processed in name order.

- [ ] **Step 4: Run the tests**

Run: `dotnet test engine/EdiabasLib.Native.Tests && dotnet test dev/census.Tests`. Expected: all pass.

- [ ] **Step 5: Add the CLI and smoke-run it on the upstream folder**

Copy `cmd_test1.prg`, `cmd_test2.prg`, `base1.prg` and `base2.prg` from the upstream test folder
into a scratch directory, then run `dotnet run --project dev/census -- concepts <that directory>`.
Expected: a table with four rows, including `cmd_test1.prg no_xsetpar`, and a tally line, with
exit code 0.

- [ ] **Step 6: Commit**

```bash
git add engine dev/census dev/census.Tests
git commit -m "Concept census: record what each SGBD's INITIALISIERUNG asks of the cable, offline"
```

---

### Task 3: Opcode census

**Files:**
- Modify: `dev/census/Census.csproj` (link `third_party/ediabaslib/Tools/BESTDIS/Program.cs`, MIT, unmodified)
- Create: `dev/census/OpcodeCensus.cs`
- Create: `dev/census.Tests/OpcodeCensusTests.cs`

**Interfaces:**
- Produces:
  - `OpcodeCensus.Run(string ecuPath) : OpcodeReport`, with
    `record OpcodeReport(IReadOnlyDictionary<string,int> MnemonicCounts, IReadOnlyList<(string File, string Mnemonic)> Unimplemented, IReadOnlyList<(string File, string Error)> Errors)`.
  - `OpcodeCensus.UnimplementedOpcodes : IReadOnlyDictionary<byte,string>`. These are the 16
    bytes 0x0C, 0x0D, 0x6F, 0x70, 0x78, 0x84, 0x85, 0x86, 0x8D, 0xA0, 0xAF, 0xB0, 0xB1, 0xB2,
    0xB3 and 0xB4, mapped to their mnemonics in BESTDIS's own opcode table (Program.cs:172 on).
  - The CLI: `census opcodes <ecuPath> [--json <out>]`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void UnimplementedTable_Has16Entries_FromBestdis()
    => Assert.Equal(16, OpcodeCensus.UnimplementedOpcodes.Count);

[Fact]
public void TestSgbds_DisassembleWithoutErrors_AndCountMove()
{
    var r = OpcodeCensus.Run(TestFiles.TempCopy("cmd_test1.prg", "cmd_test2.prg", "base1.prg", "base2.prg"));
    Assert.Empty(r.Errors);
    Assert.True(r.MnemonicCounts["move"] > 0);
}

[Fact]
public void CorruptFile_IsReportedAsError_NotThrown()
{
    var dir = TestFiles.TempCopy("cmd_test1.prg");
    File.WriteAllBytes(Path.Combine(dir, "junk.prg"), new byte[] { 1, 2, 3 });
    Assert.Single(OpcodeCensus.Run(dir).Errors);
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test dev/census.Tests`. Expected: compile errors.

- [ ] **Step 3: Implement `OpcodeCensus.Run`**

For each `.prg`/`.grp`, `Run` invokes BESTDIS's private static `BESTDIS.Program.Main(string[] { input, outputTempFile })`
by reflection (the census has its own entry point, so set `<StartupObject>` to it). It treats
a non-zero return or an exception as an error row. It counts mnemonics by parsing the
disassembly's instruction lines; check BESTDIS's `disassembleJob` output format and parse to
match it. It also builds `UnimplementedOpcodes` from BESTDIS's opcode table, by reflection
over its opcode list.

- [ ] **Step 4: Run the tests**

Run: `dotnet test dev/census.Tests`. Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add dev/census dev/census.Tests
git commit -m "Opcode census: disassemble every SGBD with BESTDIS and report EdiabasLib's unimplemented opcodes"
```

---

### Task 4: `kline` scaffold and platform constants

**Files:**
- Create: `kline/build.zig`, `kline/build.zig.zon` (minimum Zig `0.16.0`; no dependencies)
- Create: `kline/src/root.zig` (re-exports), `kline/src/sys/darwin.zig`, `kline/src/sys/linux.zig`
- Test: inline `test` blocks in the `sys/*.zig` files

**Interfaces:**
- Produces:
  - `sys.darwin`: `TIOCEXCL`, `TIOCSBRK`, `TIOCCBRK`, `TIOCMBIS`, `TIOCMBIC`, `TIOCMGET`,
    `IOSSIOSPEED`, `IOSSDATALAT`, `FIONREAD`, and `TIOCM_DTR`/`TIOCM_RTS`/`TIOCM_DSR`, all as
    `c_ulong` or `c_int` as the ioctl signature needs.
  - `sys.linux`: `struct_termios2`, `TCGETS2`, `TCSETS2`, `BOTHER`, `CBAUD`,
    `struct_serial_struct`, `TIOCGSERIAL`, `TIOCSSERIAL`, `ASYNC_LOW_LATENCY`, and the
    `TIOCM_*` bits.
  - The build has a static library `kline`, an executable `kline` (from Task 7) and a `test`
    step. It links libc, plus the `IOKit` and `CoreFoundation` frameworks on macOS.

- [ ] **Step 1: Write the failing tests**

```zig
// sys/darwin.zig
test "darwin ioctl constants match the SDK headers" {
    if (@import("builtin").os.tag != .macos) return error.SkipZigTest;
    const c = @cImport({ @cInclude("sys/ioctl.h"); @cInclude("IOKit/serial/ioss.h"); });
    try std.testing.expectEqual(@as(c_ulong, c.TIOCEXCL), TIOCEXCL);
    try std.testing.expectEqual(@as(c_ulong, 0x2000740d), TIOCEXCL);
    try std.testing.expectEqual(@as(c_ulong, 0x80085402), IOSSIOSPEED);
    try std.testing.expectEqual(@as(c_ulong, 0x80085400), IOSSDATALAT);
    // …one expectEqual against the header for every constant listed under Produces
}
```

There is a matching `test "linux constants match the kernel headers"` in `sys/linux.zig`. It
skips on non-Linux and checks against `<asm/termbits.h>`, `<asm/ioctls.h>` and
`<linux/serial.h>`, including `@sizeOf(struct_termios2)`.

- [ ] **Step 2: Run them to see them fail**

Run: `cd kline && zig build test`. Expected: compile errors, because the constants are
undefined.

- [ ] **Step 3: Declare the constants**

Hand-declare each value; the `@cImport` is used only in tests. The values for macOS arm64 are
listed under Global Constraints.

- [ ] **Step 4: Run the tests**

Run: `cd kline && zig build test`. Expected: pass on macOS (the Linux test skipped). Ryan
re-runs on Linux in Task 8.

- [ ] **Step 5: Commit**

```bash
git add kline
git commit -m "kline: Zig scaffold with hand-declared Darwin and Linux serial ioctls, checked against system headers"
```

---

### Task 5: `Port`: open, lock, identity, line settings

**Files:**
- Create: `kline/src/port.zig`, `kline/src/identity.zig`, `kline/src/testing/pty.zig`
- Test: inline `test` blocks in `port.zig` and `identity.zig`

**Interfaces:**
- Consumes: `sys.darwin`, `sys.linux` (Task 4).
- Produces (in `port.zig` unless noted):
  ```zig
  pub const Parity = enum { none, even, odd };
  pub const LineSettings = struct { baud: u32, data_bits: u4, parity: Parity };
  pub const OpenOptions = struct { require_ftdi: bool = true, lock_dir: ?[]const u8 = null };
  pub const OpenError = error{ NoDevice, Locked, NotFtdi, Permission, Io };
  pub const LineError = error{ Unsupported, Io };
  pub const Port = struct {
      pub fn open(path: []const u8, opts: OpenOptions) OpenError!Port;
      pub fn close(self: *Port) void;
      pub fn setLine(self: *Port, s: LineSettings) LineError!void;
      pub fn getLine(self: *Port) LineError!LineSettings;
  };
  // identity.zig
  pub const UsbIds = struct { vid: u16, pid: u16 };
  pub const IdentityError = error{ NotUsb, Io };
  pub fn lookup(path: []const u8) IdentityError!UsbIds;
  pub fn parseSysfsHex(text: []const u8) error{Invalid}!u16;
  // testing/pty.zig
  pub const Pty = struct { master: std.posix.fd_t, slave_path: [:0]const u8; pub fn open() !Pty; pub fn close(self: *Pty) void; };
  ```

- [ ] **Step 1: Write the failing tests**

```zig
test "open a pty, set 9600 8E1 and read it back" {
    var pty = try Pty.open(); defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false }); defer p.close();
    try p.setLine(.{ .baud = 9600, .data_bits = 8, .parity = .even });
    try std.testing.expectEqual(LineSettings{ .baud = 9600, .data_bits = 8, .parity = .even }, try p.getLine());
    try p.setLine(.{ .baud = 115200, .data_bits = 8, .parity = .none });
    try std.testing.expectEqual(@as(u32, 115200), (try p.getLine()).baud);
}
test "second open of the same device is Locked" { // Review Focus 4
    var pty = try Pty.open(); defer pty.close();
    var a = try Port.open(pty.slave_path, .{ .require_ftdi = false }); defer a.close();
    try std.testing.expectError(error.Locked, Port.open(pty.slave_path, .{ .require_ftdi = false }));
}
test "stale lock file not held by anyone does not block" { // Review Focus 5
    var pty = try Pty.open(); defer pty.close();
    var tmp = std.testing.tmpDir(.{}); defer tmp.cleanup();
    // create the lock file the way Port.open names it, without holding flock
    try createLockFileFor(tmp.dir, pty.slave_path);
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false, .lock_dir = try tmp.dir.realpathAlloc(std.testing.allocator, ".") });
    p.close();
}
test "missing device is NoDevice" { // Review Focus 3
    try std.testing.expectError(error.NoDevice, Port.open("/dev/cu.usbserial-doesnotexist", .{}));
}
test "a pty is not FTDI when FTDI is required" {
    var pty = try Pty.open(); defer pty.close();
    try std.testing.expectError(error.NotFtdi, Port.open(pty.slave_path, .{}));
}
test "parseSysfsHex" {
    try std.testing.expectEqual(@as(u16, 0x0403), try parseSysfsHex("0403\n"));
    try std.testing.expectError(error.Invalid, parseSysfsHex("zz"));
}
```

(`createLockFileFor` is a test helper beside the tests; it uses the same naming function
`Port.open` uses.)

- [ ] **Step 2: Run them to see them fail**

Run: `cd kline && zig build test`. Expected: compile errors.

- [ ] **Step 3: Implement `Port.open`/`close`/`setLine`/`getLine` and `identity.lookup`**

**`open`**, in this order:
1. `stat` the path: `ENOENT` is `NoDevice`, so a missing cable is never reported as
   `NotFtdi`.
2. If `require_ftdi`, check identity: anything other than `0403:6001` is `NotFtdi`, and a
   non-USB device is also `NotFtdi`.
3. Take `flock(LOCK_EX|LOCK_NB)` on `<lock_dir>/kline-<basename>.lock`. The lock dir is
   `lock_dir`, else `$XDG_RUNTIME_DIR`, else `$TMPDIR`, else `/tmp`. The file is created
   `0600`; `EWOULDBLOCK` is `Locked`.
4. `open(O_RDWR|O_NOCTTY|O_NONBLOCK)`: `ENOENT` is `NoDevice`, `EACCES` is `Permission`,
   `EBUSY` is `Locked`.
5. `ioctl(TIOCEXCL)`.
6. Raw mode: `cfmakeraw`, `CLOCAL|CREAD`, one stop bit, `VMIN=0`, `VTIME=0`.

`close` releases the fd, then the flock.

**`setLine`**: rates 9600, 19200, 38400, 57600 and 115200 go through `cfsetspeed`. Any other
rate uses `IOSSIOSPEED` on macOS or termios2 `BOTHER` on Linux. `Unsupported` if the platform
refuses. Parity changes call `tcdrain` first.

**`getLine`** reports what `tcgetattr` (termios2 on Linux) says.

**`identity.lookup`**:
- On macOS, walk IOKit from the `IOSerialBSDClient` whose `IOCalloutDevice` equals `path` up
  through its parents to `idVendor`/`idProduct`.
- On Linux, resolve `/sys/class/tty/<basename>/device` and walk up to the directory holding
  `idVendor`/`idProduct`.

- [ ] **Step 4: Run the tests**

Run: `cd kline && zig build test`. Expected: all pass on macOS.

- [ ] **Step 5: Commit**

```bash
git add kline
git commit -m "kline: Port open with lock, exclusive access and FTDI identity; line settings with read-back"
```

---

### Task 6: `Port`: modem lines, break, latency, timed receive

**Files:**
- Modify: `kline/src/port.zig`
- Test: inline `test` blocks in `port.zig`

**Interfaces:**
- Consumes: `Port` and `Pty` (Task 5).
- Produces (all methods on `Port`):
  ```zig
  pub const IoError = error{ Io };
  pub const LatencyError = error{ Unsupported, Io };
  pub const ReceiveError = error{ Timeout, Io };
  pub fn setDtr(self: *Port, on: bool) IoError!void;
  pub fn setRts(self: *Port, on: bool) IoError!void;
  pub fn getDsr(self: *Port) IoError!bool;
  pub fn setBreak(self: *Port, on: bool) IoError!void;
  pub fn setLowLatency(self: *Port) LatencyError!void;
  pub fn write(self: *Port, bytes: []const u8) IoError!void;
  pub fn purge(self: *Port) IoError!void;
  /// Fills buf exactly. The first byte must arrive within timeout_ms and no later gap may exceed
  /// timeout_tel_end_ms (EdiabasLib ReceiveData semantics, EdInterfaceObd.cs:3381-3450).
  pub fn receive(self: *Port, buf: []u8, timeout_ms: u32, timeout_tel_end_ms: u32) ReceiveError!void;
  ```

- [ ] **Step 1: Write the failing tests**

```zig
test "receive gets bytes written by the other end" {
    var pty = try Pty.open(); defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false }); defer p.close();
    _ = try std.posix.write(pty.master, "\x83\xf1\x01");
    var buf: [3]u8 = undefined;
    try p.receive(&buf, 100, 20);
    try std.testing.expectEqualSlices(u8, "\x83\xf1\x01", &buf);
}
test "receive times out when nothing arrives" {
    var pty = try Pty.open(); defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false }); defer p.close();
    var buf: [1]u8 = undefined;
    const t0 = std.time.milliTimestamp();
    try std.testing.expectError(error.Timeout, p.receive(&buf, 50, 20));
    try std.testing.expect(std.time.milliTimestamp() - t0 >= 50);
}
test "receive times out on a gap longer than timeout_tel_end" {
    // writer thread: 1 byte, sleep 60 ms, 1 byte; receive(2 bytes, 100, 20) -> Timeout
}
test "write reaches the other end" { /* write "\x55" then read pty.master */ }
test "receive after the other end has gone is Io, not a hang" { // Review Focus 3
    var pty = try Pty.open();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false }); defer p.close();
    pty.close();
    var buf: [1]u8 = undefined;
    try std.testing.expectError(error.Io, p.receive(&buf, 100, 20));
}
test "low latency on a pty is Unsupported, not a crash" {
    var pty = try Pty.open(); defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false }); defer p.close();
    try std.testing.expectError(error.Unsupported, p.setLowLatency());
}
```

The modem-line and break calls can't be checked on a pty. They are verified on the cable in
Task 7's `probe`. The unit tests check only that `setBreak(true)`/`setBreak(false)` return
without crashing on a pty (either success or `Io` is accepted).

- [ ] **Step 2: Run them to see them fail**

Run: `cd kline && zig build test`. Expected: compile errors.

- [ ] **Step 3: Implement the methods**

- **`receive`** uses `poll(2)` with the remaining budget on `std.time.Instant` (monotonic),
  then `read` of whatever is available. `POLLHUP`/`POLLERR` or a read of 0 bytes after HUP
  gives `Io`.
- **`setLowLatency`** uses `IOSSDATALAT` with value 1 (ms) on macOS. On Linux it is
  `TIOCGSERIAL`, then setting `ASYNC_LOW_LATENCY`, then `TIOCSSERIAL`. `ENOTTY`/`EINVAL` gives
  `Unsupported`.
- **`setBreak`** uses `TIOCSBRK`/`TIOCCBRK`.
- **DTR/RTS** use `TIOCMBIS`/`TIOCMBIC`, and **DSR** uses `TIOCMGET`.
- **`purge`** uses `tcflush(TCIOFLUSH)`.

- [ ] **Step 4: Run the tests**

Run: `cd kline && zig build test`. Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add kline
git commit -m "kline: modem lines, held break, low latency and EdiabasLib-style timed receive"
```

---

### Task 7: `kline` CLI for desk bring-up

**Files:**
- Create: `kline/src/cli.zig` (executable root), `kline/README.md`
- Test: inline `test` blocks in `cli.zig` for argument parsing

**Interfaces:**
- Consumes: `Port`, `identity` (Tasks 5–6).
- Produces the commands:
  - **`kline list`**: one line per `/dev/cu.usbserial-*` (macOS) or `/dev/ttyUSB*` (Linux),
    with `VID:PID` and `ftdi`/`other`.
  - **`kline probe <device>`** prints one `ok`/`FAIL` line per check:
    - `identity 0403:6001`;
    - `lock` (and a second open refused);
    - `line 9600 8E1`, `line 115200 8N1` and `line 10400 8N1`, each with the read-back value;
    - `latency 1ms` (or `unsupported`);
    - `dtr on/off`;
    - `dsr` (the value);
    - `break 25ms` (set, sleep 25 ms on the monotonic clock, clear).

    It exits 0 only if identity, lock and all three line settings pass.
  - **`kline loopback <device> --bench --latency <1|16> --count <n>`**: refuses to run without
    `--bench`. It sends `n` single bytes 0x55 at 115200 8N1 and times each echo, then prints
    the min/median/max round trip in ms. With `--latency 16` it skips `setLowLatency`.

- [ ] **Step 1: Write the failing tests**

```zig
test "loopback without --bench is refused" {
    try std.testing.expectError(error.BenchFlagRequired, parseArgs(&.{ "loopback", "/dev/x", "--latency", "1", "--count", "10" }));
}
test "probe needs a device" {
    try std.testing.expectError(error.MissingDevice, parseArgs(&.{"probe"}));
}
test "unknown command" {
    try std.testing.expectError(error.UnknownCommand, parseArgs(&.{"flash"}));
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `cd kline && zig build test`. Expected: compile errors.

- [ ] **Step 3: Implement `parseArgs(args: []const []const u8) ParseError!Command` and the three commands**

`Command` is a tagged union of `list`, `probe: []const u8` and
`loopback: struct { device: []const u8, latency_ms: u8, count: u32 }`. `ParseError` is
`error{ UnknownCommand, MissingDevice, BenchFlagRequired, BadNumber }`. The README covers what
each command does, that loopback needs a bench 12 V supply on OBD pin 16 (ground pins 4/5) and
never a car, and the Linux udev rule:

```
SUBSYSTEM=="tty", ATTRS{idVendor}=="0403", ATTRS{idProduct}=="6001", TAG+="uaccess"
```

- [ ] **Step 4: Run the tests and build**

Run: `cd kline && zig build test && zig build && ./zig-out/bin/kline list`.
Expected: tests pass. `list` prints the connected FTDI devices; with no cable, it prints
nothing and exits 0.

- [ ] **Step 5: Commit**

```bash
git add kline
git commit -m "kline CLI: list, probe and bench-only loopback for desk bring-up"
```

---

### Task 8: M0 desk session and results

This task is a document plus a session Ryan runs. The implementer writes the document and the
steps that need no hardware. Ryan does the rest, and the plan isn't finished until his results
are in.

**Files:**
- Create: `docs/native/m0-results.md`

**Interfaces:**
- Consumes: `census concepts`, `census opcodes` (Tasks 2–3), `kline list|probe|loopback` (Task 7).

- [ ] **Step 1: Write `docs/native/m0-results.md` with these sections, each with the command to run and an empty results table**

1. **SGBD copy.** Ryan copies `C:\EDIABAS\Ecu` from the VM to `~/ediabas-ecu`, for example
   through a UTM shared folder. Record the file count and the SHA-256 of `E60.prg`,
   `NVE_65.prg` and `TMODE.prg` on both sides (the spike NOTES "Phase 2" commands).
2. **Concept census.** Run
   `dotnet run --project dev/census -- concepts ~/ediabas-ecu --json ~/ediabas-ecu-census.json`.
   Record:
   - the tally;
   - the concepts and bauds for the modules in `docs/at-the-car.md`;
   - the decision: **does any SGBD use 0x010C (KWP2000 BMW, fast init)?**
3. **Opcode census.** Run `dotnet run --project dev/census -- opcodes ~/ediabas-ecu`. Record any
   unimplemented opcode found, with its files. The decision: none, or a list for M3.
4. **INIT question (Ryan, in the VM, by hand, cable unplugged from the car).**
   1. Back up `C:\EDIABAS\Bin\EDIABAS.INI`, then set `IfhTrace = 3`.
   2. In Tool32, load `NVE_65` and run `_JOBS`.
   3. Read `C:\EDIABAS\TRACE\ifh.trc` (`vm_logs_read` can fetch it).
   4. Restore the INI.

   Record whether INITIALISIERUNG appears. The bridge never edits the INI itself (safety rule
   8).
5. **Cable on the Mac (not in the car).**
   1. Detach the cable from the UTM VM.
   2. Run `kline list` and then `kline probe /dev/cu.usbserial-…`.
   3. Record every line, especially `line 10400` read-back and `latency 1ms`.
6. **Echo timing (only with a bench 12 V supply).** Run
   `kline loopback … --bench --latency 1 --count 200`, then the same with `--latency 16`.
   Record min/median/max. If there's no bench supply, say so; this moves to the start of M2.
7. **Linux repeat.** On a Linux machine (a UTM Linux VM with the cable passed through is fine),
   install the udev rule, then run `zig build test` (Linux constants test) and
   `kline probe /dev/ttyUSB0`. Record the results.
8. **Break width (only if section 2 found 0x010C).** With a scope or logic analyser on the
   cable's K-line, measure `kline probe`'s 25 ms break on macOS and on Linux.
9. **Decisions for M1–M3.** Record:
   - the concepts M3 must port (always including DS2, for the E46 M3);
   - whether fast init is in M2;
   - any opcode work;
   - whether `NoInitForVJobs = true` matches real EDIABAS;
   - whether latency 1 ms took effect.

- [ ] **Step 2: Run the hardware-free parts now**

Run the census commands against `third_party/ediabaslib/EdiabasLib/Test/Ecu` as a dry run, and
paste their output under sections 2–3 as "dry run on upstream test SGBDs". Run
`dev/check-no-bmw-files.sh`; expected: exit 0.

- [ ] **Step 3: Commit**

```bash
git add docs/native/m0-results.md
git commit -m "M0 results document with the desk-session procedure"
```

- [ ] **Step 4: Ryan's desk session**

Ryan works through sections 1 and 4–8. Claude runs sections 2–3 once `~/ediabas-ecu` exists,
fills in the tables (not the SGBD contents), and records section 9's decisions with Ryan. The
census JSON stays outside the repo: it names BMW files but contains no BMW content, so whether
to commit a copy is Ryan's call.

- [ ] **Step 5: Commit the results**

```bash
dev/check-no-bmw-files.sh
git add docs/native/m0-results.md
git commit -m "M0 results: concept and opcode census, INIT behaviour, cable bring-up on macOS and Linux"
```
