# Open-source and third-party stacks for native BMW E-series diagnostics (macOS/Linux, no Windows VM)

Research date: 2026-11-22. Repo metadata (licence, created, last push, stars) was read live from the GitHub API on that date. "Last push" is the `pushed_at` date. Most of the newer projects below were created in 2026 and have few stars, so treat them as young, mostly single-author projects.

## 1. Reimplementations of EDIABAS / the BEST/2 interpreter (other than EdiabasLib/Deep OBD)

### Takeaway
There are now at least six independent BEST/2 interpreters outside EdiabasLib: TypeScript plus C11 (ediabasx), two in Rust (bimmerscope and klartext's `best` crate, plus the early-stage ediabas-prg), and an older C++/Pascal pair (lpcvoid). Two are directly relevant to the Zig port. **bimmerscope** (Rust, GPL-3, Linux) says it implements "all 184 opcodes" plus DS2/KWP2000/BMW-FAST transport over K+DCAN, and it checks itself against EdiabasLib output. **ediabasx-embedded** (C11) is a malloc-free VM written for small devices. Its PolyForm Noncommercial licence rules out copying code, but its design is a useful reference.

### Cited Findings
**EdiabasLib (baseline, for comparison)**
- uholeschak/ediabaslib: C#, GPL-3.0, last push 2026-11-22 (latest commit "Code simplified", 2026-11-22T08:35Z), 968 stars, 229 forks, 0 open issues. Latest binary release `binaries_20260607` (2026-06-07). — [GitHub API / repo](https://github.com/uholeschak/ediabaslib)
- The README says it reads and interprets `.GRP`/`.PRG` directly and supports the "D-CAN, BMW-FAST, KWP-2000*, KWP-2000 BMW, DS2, DS1, Concept 3, ISO9141 (Concept 2) and Concept 1 concepts". It also says "most BEST/1 commands are implemented, but a few are still missing", and that the implemented commands are tested automatically against EDIABAS output. — [ediabaslib README](https://github.com/uholeschak/ediabaslib)
- It ships a binary-compatible `api32.dll` wrapper, tested with Tool32, INPA and Rheingold. It ignores `EDIABAS.INI` and uses its own `EdiabasLib.config`. There is also an `apiNET32.dll`. — [ediabaslib README](https://github.com/uholeschak/ediabaslib)

**emdzej/ediabasx (TypeScript) and emdzej/ediabasx-embedded (C11)**
- ediabasx: "TypeScript port of EdiabasLib". It has a PRG/GRP parser, a BEST/2 decompiler, and a "BEST/2 interpreter — register file (B/A/I/L/S/F), flags (Z/C/V/S), call & data stacks, 184 opcodes". Hardware interfaces: "Serial K-Line / K+DCAN (DS2 + ISO-TP), SAE J2534 PassThru, Ethernet/ENET (DoIP), JSON-RPC gateway". The CLI examples use `/dev/cu.usbserial-…` (macOS) and `/dev/ttyUSB0`. There is a `gateway` mode that shares a cable over TCP or WebSocket JSON-RPC, and a hosted relay called "Bimmerz Connect". Created 2026-02-03, last push 2026-07-06, 18 stars. Licence: **PolyForm Noncommercial 1.0.0**, not OSI open source. — [emdzej/ediabasx](https://github.com/emdzej/ediabasx)
- The ediabasx README lists K-line protocols as KWP2000 and DS2. It does not explicitly mention BMW-FAST over K-line (the protocol a pre-2007 E60/E61 uses). — [emdzej/ediabasx](https://github.com/emdzej/ediabasx)
- ediabasx-embedded: a "C11 port of the BMW BEST/2 interpreter". About a 50 KB VM struct with "no malloc in hot paths". The transport is an opaque vtable (`edxn_transport_t`), with a POSIX serial K-line backend and an ESP32 backend planned. The opcode handlers are split by category (arith/flow/string/…). It covers the GRP→PRG variant swap via INITIALISIERUNG/IDENTIFIKATION, and the `apiJobData` binary-payload channel (`pary`, opcode 0x7F). Same PolyForm Noncommercial licence. Created 2026-06-06, last push 2026-06-13. — [emdzej/ediabasx-embedded](https://github.com/emdzej/ediabasx-embedded)
- Related repos from the same author: `emdzej/ediabasx-docs` and `emdzej/ediabasx-docs-sgbd` (docs, last push 2026-02-20 and 2026-04-06). — [GitHub search](https://github.com/emdzej?tab=repositories)

**TheOnlyLoki/bimmerscope (Rust)**
- Rust, GPL-3.0. Created and last pushed 2026-11-05, 0 stars. Crates: `sgbd` (reads SGBD files), `best` ("BEST/2 interpreter: runs jobs from the bytecode, all 184 opcodes"), `obd` ("Serial transport for K+DCAN cables: DS2, KWP2000, BMW-FAST, 5-baud and fast init"), and `bmwa` (a terminal UI). "Status: early. Works against recorded car data (EdiabasLib `.sim` files) and matches EdiabasLib output job for job. First tests on a real car (E87, K-line) are in progress." Platform: "Linux. Other platforms are planned." It has a golden-test harness, `tools/oracle`, that builds EdiabasLib's test tool in a .NET 8 Docker image and compares the two outputs. It ships a udev rule that sets FTDI `latency_timer=1`. — [TheOnlyLoki/bimmerscope](https://github.com/TheOnlyLoki/bimmerscope)

**HadiCherkaoui/klartext (Rust)**
- Rust, AGPL-3.0-or-later, last push 2026-11-10, 19 stars. Targets F-series over ENET (HSFZ/UDS). Crates: `sgbd` (".prg container parser: XOR-0xF7 body + tables") and `best` ("Offline BEST/2 (EDIABAS bytecode) VM + a read-only live job runner"). Live job execution is read-only ("the gate refuses any write the bytecode emits"). It also has a read-only MCP server. It has no K-line or E-series transport. — [HadiCherkaoui/klartext](https://github.com/HadiCherkaoui/klartext)

**MrHilgert/ediabas-prg (Rust)**
- Rust, no licence file, created 2026-07-01, last push 2026-07-05, 0 stars, no README. Its CLAUDE.md (in Russian) describes "a full EDIABAS `.prg` interpreter (BEST/2 bytecode) with all BMW K-line transport protocols". Its current target is a DDE4.0 diesel at address 0xB8 over DS2 (9600 baud, even parity, XOR checksum) on `/dev/ttyUSB0`. Uses the `serialport` crate. Workspace has `gui` and `inpa` members and `docs/BEST2-DECODER.md`. Early stage. — [MrHilgert/ediabas-prg](https://github.com/MrHilgert/ediabas-prg)

**lpcvoid BEST2 VM / disassembler (older)**
- `lpcvoid/bmw-best2-vm`: Pascal/Delphi, GPL-2.0, last push 2021-11-09. "Every job that doesn't actually send data to the car works pretty well". It notes that many opcodes are missing and that it has been superseded by the C++ rewrite. — [lpcvoid/bmw-best2-vm](https://github.com/lpcvoid/bmw-best2-vm)
- `lpcvoid/bmw-better-vm`: C++, GPL-3.0, last push 2020-10-26. "Can now successfully execute a broad amount of jobs… some opcodes are not implemented". Instruction-set documentation is on the author's blog. — [lpcvoid/bmw-better-vm](https://github.com/lpcvoid/bmw-better-vm); [BEST2 instruction set blog](https://lpcvoid.com/blog/0005_best2_instructionset/index.html)
- `lpcvoid/bmw-best2-disassembler`: C++/wxWidgets, GPL-3.0, last push 2020-09-13. — [lpcvoid/bmw-best2-disassembler](https://github.com/lpcvoid/bmw-best2-disassembler)

**Disassemblers / PRG browsers (no interpreter, no comms)**
- radelbro/BimmerDis: C++, no licence, last push 2025-05-30, 38 stars. A BEST disassembler that XORs each byte with 0xF7 and writes `.b1v`/`.b1g` output. It credits EdiabasLib's `Tools/BESTDIS`. Windows `.exe` usage is documented. — [radelbro/BimmerDis](https://github.com/radelbro/BimmerDis)
- zer02dev/BimmerDaten: Python, GPL-3.0 badge (API says NOASSERTION), last push 2026-11-20, 31 stars. A Windows PRG/job viewer, NCS TRC editor and WinKFP helper that "requires BMW Standard Tools". It shows the DS2 telegram breakdown of BETRIEBSWTAB. — [zer02dev/BimmerDaten](https://github.com/zer02dev/BimmerDaten)

**Wrappers around real EDIABAS (Windows-only, not reimplementations)**
- BembelBytes/pydiabas: Python, MIT, last push 2026-09-16, 12 stars. A Python wrapper over an EDIABAS install's `api32.dll`. — [BembelBytes/pydiabas](https://github.com/BembelBytes/pydiabas)
- arcielao/bimmer-reader: Python, MIT, created and last pushed 2026-10-20. "INPA-style" GUI that needs Windows 10/11, **32-bit** Python and `C:\EDIABAS`. — [arcielao/bimmer-reader](https://github.com/arcielao/bimmer-reader)
- Other small Python EDIABAS wrappers that GitHub search surfaced (all under 1 star, 2026; not inspected): George-Gabra-Jr/bimmerdiag, coledetrick/INPA-Health-Scan, KAMAELUA/bmw-pro-diagnostic. — [GitHub search "ediabas language:python"](https://github.com/search?q=ediabas+language%3Apython&type=repositories)

### Inferences
- Several independent ports converge on 184 opcodes (ediabasx and bimmerscope), and bimmerscope has a working "golden test vs EdiabasLib .sim" oracle. That pattern (run EdiabasLib and the port on the same `.sim` file and diff the results) is the obvious conformance strategy for a Zig port.
- bimmerscope is the closest analogue to stage 2 (a non-.NET interpreter plus K-line/BMW-FAST transport, GPL-3). It is days old and has not been proven on a real car. Its GPL-3 licence matches EdiabasLib, so code or ideas can flow between them.
- ediabasx-embedded's architecture (transport vtable, no-malloc VM, separate wrapper for system results and variant swap) is a good template for a Zig layout. The PolyForm NC licence means reading it for ideas only, with no copying, if the Zig port is to be GPL or permissive.
- None of the alternative interpreters shows evidence of real-car validation on a pre-2007 E60/E61 (BMW-FAST over K-line). EdiabasLib remains the only proven reference for that car.

### Gaps
- No Go, Java, JavaScript (other than ediabasx) or Zig BEST/2 interpreter turned up in GitHub search. GitLab and Codeberg were not searched, so treat that as an unconfirmed absence.
- Opcode completeness claims ("184 opcodes") were not checked against source.
- MrHilgert/ediabas-prg has no licence, so its code cannot be reused legally as published.

## 2. Projects that talk to E-series modules with hard-coded telegrams (no SGBD interpreter)

### Takeaway
Hard-coded DS2/KWP2000 projects are plentiful, but almost all target the DS2 generation (E36/E46/E39, Siemens MS4x DMEs) or use generic OBD-II. **beemuu** (Rust/Tauri, GPL-3) is the main hard-coded KWP2000 E-series app that runs on Linux. **Klemme15** (C#/Avalonia, GPL-3, Win/macOS/Linux) has a native stack but explicitly says pre-03/2007 E60/E87 cars "need EDIABAS mode", which is Windows-only. That is the user's exact car.

### Cited Findings
- **Expos-AI/Klemme15**: C#, GPL-3.0, created 2026-10-27, last push 2026-11-18, 7 stars. Builds for Windows, macOS (arm64/x64) and Linux x64. "The native protocol stack is written from public standards (ISO 14230, ISO 9141-2, SAE J1979) and community knowledge." EdiabasLib is "used for EDIABAS mode". Coverage table: E60/E61 is listed under D-CAN "(2007 on)". "E60 and E87-family cars built before March 2007 have no D-CAN and need EDIABAS mode". The table header labels EDIABAS mode as "(EDIABAS mode, Windows)". Validated on a 2006 E90 only. — [Expos-AI/Klemme15](https://github.com/Expos-AI/Klemme15)
- **ohgeeceee/beemuu**: Tauri 2 + Rust core + Python backend, GPL-3.0, created 2026-07-04, last push 2026-11-16, 24 stars, v5.0.1. Speaks KWP2000 for "E-series (E36 → early E9x)" over a "USB K+DCAN cable (FTDI FT232RL)" at `/dev/ttyUSB0`, plus UDS/DoIP for F/G. Has a "Parameter Explorer" to probe KWP2000 local IDs. DTC texts, DID maps and engine profiles live in TOML. Has a hosted backend at api.beemuu.com. No SGBD interpreter is described. — [ohgeeceee/beemuu](https://github.com/ohgeeceee/beemuu)
- **jjshoots/e36cli**: Python 3.11+, GPL-3.0, last push 2026-11-18. Terminal tool for the E36 M52/MS41 over DS2/K-line with a VAG KKL FTDI cable. "Built on Linux, for Linux." Has a simulator. — [jjshoots/e36cli](https://github.com/jjshoots/e36cli)
- **handmade0octopus/ds2**: C (Arduino/ESP32), MIT, last push 2026-11-13, 61 stars. A DS2 K-line library. It documents the frame format (address, length, A0/B0 ack, payload, XOR checksum), local echo on K-line, and example telegrams for MS41–MS43 (`12 04 00 16` ECU id). — [handmade0octopus/ds2](https://github.com/handmade0octopus/ds2)
- **mushitaro/E46M3-Monitoring** (plus the related mss54hp/smg2 repos): TypeScript, MIT, last push 2026-11-22. A browser DS2/K-line tool (Web Serial on desktop, WebUSB+FTDI) covering 51 E46 M3 modules. Its README says telegrams were "statically scraped from SGBD bytecode" and that real-car validation is still pending. — [mushitaro/E46M3-Monitoring](https://github.com/mushitaro/E46M3-Monitoring)
- **husey09777-beep/bmw-ds2**: HTML, last push 2026-10-23. Browser DS2 diagnostics for E30/E34/E36. — [husey09777-beep/bmw-ds2](https://github.com/husey09777-beep/bmw-ds2)
- **terraphantm/MS45-Flasher, MSS6x-Flasher, MSx70-Flasher**: C#, GPL-3.0 (MSS6x last push 2026-06-18). DME read/flash tools that mention EDIABAS in their README. They are Windows-oriented. — [terraphantm/MSS6x-Flasher](https://github.com/terraphantm/MSS6x-Flasher)
- **Generic KWP2000 libraries** (not BMW-specific): bri3d/kwp2000 (Java, 2016), aster94/Keyword-Protocol-2000 (C++, GPL-3, 332 stars, 2026-09-12), muki01/OBD2_KLine_Library (C++, GPL-3, 2026-11-11), EliasTuning/KWP2000-CAN (Python, MIT, 2026-02-20). — [aster94/Keyword-Protocol-2000](https://github.com/aster94/Keyword-Protocol-2000); [muki01/OBD2_KLine_Library](https://github.com/muki01/OBD2_KLine_Library)
- **I-Bus/K-Bus** (a different bus, low priority): toxsedyshev/imBMW (C#, 101 stars), muki01/BMW_IBus_KBus (C++, GPL-3, 2026-10-14), jimbojsb/ibus (Go). — [toxsedyshev/imBMW](https://github.com/toxsedyshev/imBMW); [muki01/BMW_IBus_KBus](https://github.com/muki01/BMW_IBus_KBus)
- Ydjin1984/automotive-protocol-re: Python, MIT. A "ZCode plugin" for reverse-engineering DS2/EDIABAS/KWP2000 protocols (not inspected in depth). — [Ydjin1984/automotive-protocol-re](https://github.com/Ydjin1984/automotive-protocol-re)

### Inferences
- For a 2005 E61 (BMW-FAST/KWP2000 over K-line, not DS2), the DS2 projects only help with framing and echo handling. Their telegrams don't carry over.
- A hard-coded approach would need per-module telegrams for dozens of E60-era ECUs. The SGBD-interpreter approach (EdiabasLib, then Zig) is the only scalable route to Tool32-equivalent coverage, which is the conclusion Klemme15 and others reached too.

### Gaps
- No Python "pyBMW" or "bmw-ds2" Python library of note was found. GitHub's multi-word searches ("bmw kline", "kwp2000 bmw", "e46 ds2") returned no results, possibly because of how the search API handles multiple terms, so some projects may have been missed.
- beemuu's KWP2000 E-series code was not checked for BMW-FAST/K-line support on E60-era modules specifically.

## 3. EdiabasLib's own ecosystem: tools, .NET on Linux/macOS, forks

### Takeaway
Upstream does **not** officially target Linux or macOS. The library's csproj targets `net10.0-windows10.0.26100.0`, `net11.0-windows…`, `net481` and Android, and `EdiabasTest` targets `net481` only. The maintainer's stance (2022) is that "EdiabasLib.dll should work with the mono runtime on Linux… you have to create your own application". Two third-party macOS projects now prove that stage 1 works:
- **OpenDS2** compiles EdiabasLib's sources unmodified into a `net10.0` exe, adding a stubs file.
- **bmw-mac-diag** uses the plain `net10.0` EdiabasLib.dll that ships inside the official release zip (under `S29CertGenerator/`), with the unix `System.IO.Ports`.

### Cited Findings
**Upstream layout and tools**
- Top-level directories: `BmwDeepObd`, `EdiabasLib`, `Tools`, `docs`. `EdiabasLib/` contains `Api32`, `ApiInternal`, `ApiNetAndroid`, `apiNET`, `BmwFileReader`, `CanAdapter`, `CanAdapterElm`, `EdiabasLib`, `EdiabasLibConfigTool`, `EdiabasTest`, `EnetAdapter`, `IfhLog` (C++), `Test` and `UdsFileReader`. `Tools/` contains `ApkUncompress(2)`, `ApkUploader`, `BESTDIS`, `BestNet`, `CreateObb`, `ExtractEcuFunctions`, `FileDecoder`, `LogfileConverter` (with `CreateSimFiles.bat`), `Psdz`, `S29CertGenerator` and `Win32Override`. — [ediabaslib repo tree via GitHub API](https://github.com/uholeschak/ediabaslib/tree/master/Tools)
- Core interface sources in `EdiabasLib/EdiabasLib/`: `EdiabasNet.cs` (interpreter), `EdOperations.cs` (opcodes), `EdInterfaceObd.cs` (5,936 lines; serial/FTDI K+DCAN), `EdInterfaceEnet.cs`, `EdInterfaceAds.cs`, `EdInterfaceEdic.cs`, `EdInterfaceRplus.cs`, `EdElmInterface.cs`, `EdSimFile.cs`, `Ftd2xx.cs`, `EdFtdiInterfaceAndroid.cs`. — [EdiabasLib/EdiabasLib](https://github.com/uholeschak/ediabaslib/tree/master/EdiabasLib/EdiabasLib)
- `EdiabasLib.csproj`: `<TargetFrameworks>net10.0-windows10.0.26100.0;net11.0-windows10.0.26100.0;net481;$(_AndroidTargets)</TargetFrameworks>`. `System.IO.Ports` is referenced only under the `windows` TFM condition. `EdiabasTest.csproj`: `<TargetFramework>net481</TargetFramework>`. — [EdiabasLib.csproj](https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/EdiabasLib/EdiabasLib.csproj); [EdiabasTest.csproj](https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/EdiabasTest/EdiabasTest.csproj)
- In `EdInterfaceObd.cs`, the serial path uses `System.IO.Ports.SerialPort` under `#if USE_SERIAL_PORT`, with `NETFRAMEWORK`/`ANDROID` conditionals. It controls DTR/RTS, parity and baud changes at runtime, and reads `DsrHolding`. — [EdInterfaceObd.cs](https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/EdiabasLib/EdInterfaceObd.cs)
- The upstream README still says it "runs on all platforms that support .NET framework (Windows, Windows CE, Mono)". — [ediabaslib README](https://github.com/uholeschak/ediabaslib)
- Issue #123 "Can it support running on linux?" (closed 2022-05-20). Maintainer: "At least the EdiabasLib.dll should work with the mono runtime on Linux, but I have not tested this for a longer time." Then: "Use it with mono, but you have to create your own application." A commenter points to a Raspberry Pi/Debian project on bmwraspcontrol.de that uses the DLL. — [ediabaslib issue #123](https://github.com/uholeschak/ediabaslib/issues/123)
- EdiabasLib adapter support: FTDI USB "INPA compatible" adapters support DS2, BMW-FAST, and BMW-FAST-ENET over D-CAN. It warns about fake FT232R chips. ELM327 supports D-CAN only (from 3/2007). — [EdiabasLib supported adapter types](https://uholeschak.github.io/ediabaslib/docs/AdapterTypes.html)
- Docs include `EdiabasLib.config_file.md`, `EdiabasLib.simulation_file.md`, `EdiabasTest_parameters.md`, `LogfileConverter_parameters.md` and `EDIABAS_result_types_and_formats.md`. — [ediabaslib docs dir](https://github.com/uholeschak/ediabaslib/tree/master/docs)

**Proven macOS/Linux consumers of EdiabasLib (stage-1 prior art)**
- **ecrawshaw/opends2 (OpenDS2)**: SwiftUI app plus .NET 10 engine, GPL-3.0, created and last pushed 2026-11-04. `Engine/OpenDS2Engine.csproj` targets `net10.0` and compiles `../ediabaslib/EdiabasLib/EdiabasLib/*.cs`, excluding `*Android.cs`, `BtLeGattSpp.cs`, `EdBluetoothInterface.cs`, `EdFtdiInterface.cs` and `Ftd2xx.cs`. It adds `MacStubs.cs` ("Stubs for EdiabasLib's Windows/Bluetooth parts") and uses `System.IO.Ports` 10.0.13, so the "K+DCAN cable is used as a plain serial port". Upstream is cloned "used as-is, not modified". The app talks to the engine via JSON lines on stdin/stdout. It has a local MCP server (`127.0.0.1:47146/mcp`), engine-side safety refusals (`DeniedReason`), and a separate Swift `KLine` package (SerialPort, Session, Frames, OBD, CAN, TraceImport) plus an ESP32 adapter. Tested on an E46 M3. E60-generation cars are listed as "partial, untested". — [ecrawshaw/opends2](https://github.com/ecrawshaw/opends2); [OpenDS2Engine.csproj](https://github.com/ecrawshaw/opends2/blob/main/Engine/OpenDS2Engine.csproj)
- **sakharniy/bmw-mac-diag**: C#/.NET 10 terminal app, GPL-3.0, created 2026-09-26, last push 2026-09-30, 14 stars. `build.sh` downloads `binaries_20260607` (SHA-256 pinned) and extracts `S29CertGenerator/EdiabasLib.dll` ("the net10.0 build of EdiabasLib inside the release zip"), plus `runtimes/unix/lib/net10.0/System.IO.Ports.dll` and `runtimes/osx-*/native/libSystem.IO.Ports.Native.dylib`. It notes that "the System.IO.Ports.dll in the root of the folder is a Windows stub — use the unix one". Tested on an E93 320d 2010, Apple Silicon, clone FT232RL cable. "Windows guides tell you to set the cable's FTDI latency timer to 1 ms — on macOS this is not needed." Error mapping: IFH-0003 means "flip the switch", IFH-0018 means the port is in use. — [sakharniy/bmw-mac-diag](https://github.com/sakharniy/bmw-mac-diag); [build.sh](https://github.com/sakharniy/bmw-mac-diag/blob/main/build.sh)
- **Expos-AI/Klemme15**: ships macOS/Linux builds, but EDIABAS mode (EdiabasLib) is labelled Windows. — [Expos-AI/Klemme15](https://github.com/Expos-AI/Klemme15)
- **Ridawo/InpaDroid**: C# Android "INPA-like" app on EdiabasLib, licence NOASSERTION, last push 2026-11-16. Needs an FTDI K+DCAN over OTG ("CH340… will not work"). Its E60 catalog is an "unverified" skeleton. — [Ridawo/InpaDroid](https://github.com/Ridawo/InpaDroid)
- **isaacandy/iZND-BMW-OBD-2-Diagnose**: Apache-2.0, 2017. A Deep OBD configuration set for an E60/E61 (`E61Bt`, `E61Enet`, FTDI). — [isaacandy/iZND-BMW-OBD-2-Diagnose](https://github.com/isaacandy/iZND-BMW-OBD-2-Diagnose)
- Forks: none of the most-recently-pushed forks (about 40 checked, all 0–1 stars) advertises Linux/macOS work in its description. `AderLess47/ediabaslib-ios` (pushed 2026-09-03) is named for iOS. Older side forks: `darkspr1te/CanAdapterElm`, `meikeric/EdiabasLib`. — [ediabaslib forks list via GitHub API](https://github.com/uholeschak/ediabaslib/forks)

### Inferences
- Stage 1 has two proven recipes:
  - (a) Compile upstream sources into your own `net10.0` project, excluding the Windows/Android/FTDI-D2XX files and adding stubs (OpenDS2).
  - (b) Use the `net10.0` EdiabasLib.dll from the release's `S29CertGenerator` folder, plus the unix `System.IO.Ports` runtime assets (bmw-mac-diag).

  Recipe (a) is better for a stage-2 port, because you control the build and can add tracing hooks.
- `EdiabasTest` is `net481`-only, so a cross-platform CLI oracle (needed for golden tests, as bimmerscope does with Docker/.NET 8) has to be built separately or run under Mono.
- Neither macOS project reports validation on a pre-2007 E60/E61 over K-line/BMW-FAST. bmw-mac-diag's claim that the latency timer is "not needed on macOS" comes from a D-CAN car, so K-line BMW-FAST timing on macOS's Apple FTDI driver is still unproven.

### Gaps
- The exact purpose of `Tools/BestNet` (BEST compiler vs. runner) and `ApiInternal` was not confirmed from source; only the directory contents were listed.
- The bmwraspcontrol.de thread (EdiabasLib on Debian/Raspberry Pi) was not read.
- No upstream issues mention macOS serial specifically (searches for "mac" and "mono" found only issues #83 and #123).

## 4. Running real EDIABAS/INPA/Tool32 under Wine or CrossOver (macOS/Linux)

### Takeaway
I found no first-hand, well-documented success report for INPA/EDIABAS with a K+DCAN cable under Wine or CrossOver, on Linux or on Apple Silicon. Forum opinions disagree, and Wine serial mapping is known to be fragile. A forum reply even points Linux users to EdiabasLib instead.

### Cited Findings
- E46Fanatics "BMW scan software on Linux?": one reply suggests Wine plus several .NET runtimes. Another points to "a .NET EDIABAS interpreter library that works just fine on Linux" (EdiabasLib) instead of the INPA GUI. — [E46Fanatics thread](https://www.e46fanatics.com/threads/bmw-scan-software-on-linux.1282577/) (via search snippet; the page itself redirected and was not read in full)
- Bimmerfest "Linux Diagnostic Programs for e39?": "Don't think you could run inpa on wine". — [Bimmerfest thread](https://www.bimmerfest.com/threads/linux-diagnostic-programs-for-e39.928087/) (via search snippet)
- General Wine serial behaviour: a USB-serial adapter needs a working host driver, then a symlink in the prefix's `dosdevices` (e.g. `com1 -> /dev/ttyUSB0`). Users report the link existing while the application still doesn't detect the port, and regressions between Wine versions. A Wine bug discussion suggests a host-side proxy exposing a pty as more reliable. — [WineHQ forum: map COM1](https://forum.winehq.org/viewtopic.php?p=135747); [wine-users: USB support](https://www.winehq.org/pipermail/wine-users/2008-May/035764.html)
- CrossOver on Apple Silicon: a FORScan user reports an OBD adapter working under CrossOver 24 on an M2 via a `dosdevices` symlink plus a registry tweak. A Multiecuscan user reports a cable appearing as COM1 under CrossOver 22.1.1 on an M1. Another user says Windows drivers (e.g. the FTDI D2XX/VCP) can't be used inside CrossOver because it runs in user space. — [CodeWeavers FORScan forum](https://www.codeweavers.com/compatibility/crossover/forum/forscan?msg=327553); [CodeWeavers Multiecuscan](https://www.codeweavers.com/compatibility/crossover/multiecuscan-51); [CodeWeavers forum](https://www.codeweavers.com/support/forums/general?msg=252685&t=27)
- Seller listings say K+DCAN use on non-Windows "typically requires a Windows virtual environment". — [BimmerGroup coding cable](https://ie.bimmergroup.com/bmw-coding-cable) (via search snippet)

### Inferences
- EDIABAS's `OBD.dll`/ADS IFH relies on precise UART timing and toggles DTR/RTS (EdiabasLib's `EdInterfaceObd.cs` mirrors this). Wine's COM emulation over macOS `cu.usbserial` adds latency and loses the FTDI-specific latency-timer control. Even if the COM port opens, BMW-FAST/K-line timing is the most likely failure point. This is an inference; no source confirms it.
- Wine/CrossOver is a weaker path than EdiabasLib on .NET for this project. Its only advantage is running BMW's own binaries unchanged.

### Gaps
- No primary-source report (with dates) of INPA or Tool32 + K+DCAN working under Wine or CrossOver on an Apple Silicon Mac. MHH Auto and similar forums were deliberately not used.
- No information on whether EdiabasLib's `api32.dll` replacement works under Wine (it needs .NET Framework 4.8.1 / .NET 10 Desktop plus the VC++ runtime per the README, all Windows-native).

## 5. Commercial/closed native apps (prior art only)

### Takeaway
The phone apps (BimmerCode/BimmerLink, Carly) use their own supported Bluetooth/Wi-Fi adapters (e.g. OBDLink MX+, Carly's adapter). They are aimed at 2005+/D-CAN and F/G-series cars. I found no primary source confirming that any of them supports a pre-03/2007 E60/E61 over K-line or a K+DCAN USB cable on macOS. ProTool (Bimmergeeks) is sold alongside K+DCAN cables for Windows.

### Cited Findings
- BimmerCode FAQ: "not possible to connect to the vehicle with an adapter other than the devices listed" (on /adapters/). It supports "nearly all BMW and MINI vehicles build in or after 2005". There is a "BimmerCode Desktop app" whose licence is tied to the machine; macOS is not mentioned. — [BimmerCode FAQ](https://bimmercode.app/faq/)
- The BimmerLink App Store listing requires "a supported Bluetooth or Wi-Fi OBD adapter or cable" and points to bimmerlink.app for the list. OBDLink lists BimmerLink among compatible apps. — [BimmerLink App Store](https://apps.apple.com/app/id1065360416); [OBDLink FAQ](https://www.obdlink.com/faq/)
- Carly lists E60/E61 among supported models without protocol or year detail. A retailer says E36/E38/E39/E46/E83/E85/E86 need a separate cable set. A forum user says Carly "pretty much only" works with its own adapter. — [Carly for BMW (mycarly.com)](https://www.mycarly.com/de/blog/carly-de/carly-for-bmw-and-mini/); [Bimmerpost thread](https://g42.bimmerpost.com/forums/showthread.php?p=27583393)
- K+DCAN cable listings: in K-line position the cable covers "E39, E60/E61 up to 03/2007", and in D-CAN position "E60/E61 from 03/2007". Bimmergeeks' own store lists ProTool among compatible software, with Windows "fully supported". — [BimmerGroup K+DCAN](https://ie.bimmergroup.com/KDCAN); [BimmerGroup coding cable](https://ie.bimmergroup.com/bmw-coding-cable)
- Abrites sells a dedicated "ZN049 adapter for connection with K-Line BMW vehicles (PassThru only)", suggesting pro tools treat BMW K-line as a special case. — [Abrites ZN049](https://abrites.com/partial/zn049)

### Inferences
- For a 2005 E61, the free open-source macOS/Linux route (EdiabasLib on .NET, then a native port) has no direct commercial equivalent that is documented to work over K-line from a Mac.

### Gaps
- bimmerlink.app/accessories returned 404, and the official BimmerLink and Carly adapter lists for pre-2007 E60 were not retrieved.
- ProTool's supported-OS and E-series K-line support were not confirmed from a Bimmergeeks primary page.

## 6. Existing Zig code relevant to stage 2

### Takeaway
Zig serial-port libraries exist and are current: **jcollie/zig-serial** (MIT, Zig 0.17, Linux termios2 with arbitrary baud, macOS/BSD via libc termios, DTR/RTS, break, exclusive open) and **ZigEmbeddedGroup/serial** (MIT, 98 stars). There is a small libusb wrapper (ssttevee/zig-libusb). No Zig KWP2000, OBD, K-line or BEST/2 code was found.

### Cited Findings
- jcollie/zig-serial: MIT, last push 2026-10-10. "Serial ports for Zig 0.17". Raw mode, `Settings` (rate, data bits, parity incl. mark/space, stop bits, flow control), `current` reads back what the driver actually chose, `modem`/`setLine` for DTR/RTS, `sendBreak`/`drain`/`flush`. Exclusive by default via `flock` + `TIOCEXCL`. Non-blocking by default. Linux uses raw syscalls with `termios2`/`BOTHER`, so any baud rate works. macOS/BSD use libc termios. — [jcollie/zig-serial](https://github.com/jcollie/zig-serial)
- ZigEmbeddedGroup/serial: MIT, last push 2026-05-30, 98 stars, "Serial port configuration library for Zig". — [ZigEmbeddedGroup/serial](https://github.com/ZigEmbeddedGroup/serial)
- ssttevee/zig-libusb: MIT, last push 2024-06-10, 4 stars, "zig builder and wrapper for libusb". Also tiny repos juliantos/zig-usb (2025-11-29) and ST3tter/zsm (a Zig serial monitor, 2026-10-07). — [ssttevee/zig-libusb](https://github.com/ssttevee/zig-libusb); [juliantos/zig-usb](https://github.com/juliantos/zig-usb)
- GitHub searches for "zig kwp2000", "zig obd2", "zig socketcan", "zig can bus" and "zig ftdi" returned nothing relevant. — [GitHub search](https://github.com/search?q=zig+kwp2000&type=repositories)

### Inferences
- The macOS Apple FTDI driver (`cu.usbserial-*`) exposes no latency-timer control through termios. If BMW-FAST/K-line timing needs 1 ms latency on macOS, the Zig port may need direct FTDI access via libusb (zig-libusb, or a hand-written FTDI vendor-request layer setting latency to 1 ms) rather than plain termios. EdiabasLib's `Ftd2xx.cs` path (D2XX) is the reference for which FTDI controls matter. This should be tested with EdiabasLib on .NET first (stage 1).
- jcollie/zig-serial is a good base for a termios transport. Its "read back actual settings" feature matters because K+DCAN clones and the macOS driver may round non-standard baud rates (BMW-FAST on K-line uses 115200; DS2 uses 9600 8E1; the ISO 9141 5-baud init needs break or bit-bang).

### Gaps
- No evidence found either way on whether the macOS Apple FTDI driver's default latency (16 ms) breaks BMW-FAST over K-line. bmw-mac-diag's "not needed" claim was made on a D-CAN car.
- Codeberg and sourcehut were not searched for Zig automotive code.
