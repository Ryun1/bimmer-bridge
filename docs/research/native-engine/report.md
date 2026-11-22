# EdiabasLib runs natively; the K-line still needs proving

Everything needed to read a 2005 E61 from macOS or Linux without Windows already exists in open source, but nobody has shown it working on this kind of car. **EdiabasLib (GPL-3) is still the only BEST/2 interpreter plus K+DCAN transport proven against real cars.** Two third-party macOS projects now run it on plain .NET 10: OpenDS2 compiles the upstream sources, and bmw-mac-diag uses the net10.0 DLL from the release zip. That confirms stage 1 is mechanically sound. **Neither project, nor any other surveyed, reports a pre-03/2007 E60/E61 read over K-line (BMW-FAST at 115200 baud) from a Mac.** Those reads are exactly what Ryan's car needs. Two findings change the current draft design:

- **macOS's FTDI latency timer is not fixed at 16 ms.** Apple's DriverKit FTDI driver accepts the `IOSSDATALAT` ioctl. The forum evidence says it takes milliseconds, not the microseconds the header states.
- **.NET's `SerialPort.BreakState` on Unix is a one-shot `tcsendbreak` of 0.25–0.5 s, not a held break.** EdiabasLib's 25 ms KWP fast init therefore cannot work unmodified on macOS or Linux.

For stage 2 there are now at least five independent BEST/2 interpreters outside EdiabasLib. The closest analogue is **bimmerscope**, a GPL-3 Rust project that says it implements all 184 opcodes plus DS2, KWP2000 and BMW-FAST over K+DCAN, and diffs itself against EdiabasLib `.sim` replays. It is weeks old and unproven on a car. There is no Zig diagnostic code at all, only serial-port libraries, and Zig 0.16's standard library lacks the Darwin serial ioctls. The practical consequence: stage 1 has to patch or wrap EdiabasLib's serial layer, and both stages should run a "concept census" of Ryan's SGBDs at the desk before anything goes near the car.

## Two macOS projects already run EdiabasLib; none has touched a K-line E60

The upstream library does not officially target macOS or Linux. `EdiabasLib.csproj` targets `net10.0-windows10.0.26100.0`, `net11.0-windows…`, `net481` and Android, and references `System.IO.Ports` only under the Windows TFM ([EdiabasLib.csproj](https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/EdiabasLib/EdiabasLib.csproj)). The maintainer's position since 2022 is that the DLL "should work with the mono runtime on Linux… but you have to create your own application" ([issue #123](https://github.com/uholeschak/ediabaslib/issues/123)). Upstream's own CLI, `EdiabasTest`, is **`net481`-only** ([EdiabasTest.csproj](https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/EdiabasTest/EdiabasTest.csproj)). A cross-platform test oracle therefore has to be built, not borrowed. Ryan's own spike reached the same point: seven unmodified upstream files built the interpreter for `osx-arm64` with zero warnings, and `EdInterfaceObd` compiled but was never run ([spike NOTES](file:///Users/ryan/projects/ediabaslib-spike/NOTES.md)).

Two independent projects have since taken the next step. **ecrawshaw/opends2** (GPL-3, Swift + .NET 10, pushed 2026-11-04) compiles `ediabaslib/EdiabasLib/EdiabasLib/*.cs` into a `net10.0` engine. It excludes the Android, BLE, Bluetooth and D2XX files, adds a `MacStubs.cs`, and uses `System.IO.Ports` 10.0.13, so the "K+DCAN cable is used as a plain serial port" ([opends2](https://github.com/ecrawshaw/opends2); [OpenDS2Engine.csproj](https://github.com/ecrawshaw/opends2/blob/main/Engine/OpenDS2Engine.csproj)). Its architecture is nearly the design in the draft spec: a separate engine process speaking JSON lines over stdin/stdout, engine-side safety refusals (`DeniedReason`) and a local MCP server. The difference is that its MCP server listens on `127.0.0.1:47146` where the draft uses stdio. It was tested on an E46 M3, and E60-generation cars are listed as "partial, untested". **sakharniy/bmw-mac-diag** (GPL-3, C#, 14 stars, pushed 2026-09-30) skips compiling altogether. Its `build.sh` downloads the SHA-256-pinned `binaries_20260607` release, then extracts the plain `net10.0` `EdiabasLib.dll` from the `S29CertGenerator/` folder and the **unix** `System.IO.Ports` runtime assets. It warns that the root-folder `System.IO.Ports.dll` is a Windows stub ([bmw-mac-diag](https://github.com/sakharniy/bmw-mac-diag); [build.sh](https://github.com/sakharniy/bmw-mac-diag/blob/main/build.sh)). It was tested on a 2010 E93 (D-CAN) with a clone FT232RL on Apple Silicon. Its claim that the FTDI latency timer is "not needed on macOS" was therefore never exercised on K-line timing.

The wider landscape divides by what each project can run:

| Project | Language / licence | What it is | Relevance to a 2005 E61 |
|---|---|---|---|
| [EdiabasLib](https://github.com/uholeschak/ediabaslib) | C#, GPL-3 | Reference BEST/2 VM + DS2/KWP/BMW-FAST/D-CAN over FTDI | Only proven E60 K-line stack; Windows/Android-first |
| [bimmerscope](https://github.com/TheOnlyLoki/bimmerscope) | Rust, GPL-3 | `sgbd`, `best` ("all 184 opcodes"), `obd` (DS2, KWP2000, BMW-FAST, 5-baud, fast init), TUI | Closest stage-2 analogue; Linux only; first car test (E87, K-line) "in progress". Its GitHub description also advertises fault clearing, so it is not read-only |
| [ediabasx](https://github.com/emdzej/ediabasx) / [ediabasx-embedded](https://github.com/emdzej/ediabasx-embedded) | TypeScript / C11, PolyForm Noncommercial | Port of EdiabasLib; C11 no-malloc VM with a transport vtable | Good design reference, but the licence forbids copying into a GPL project; K-line docs mention DS2 and KWP2000, not BMW-FAST explicitly |
| [klartext](https://github.com/HadiCherkaoui/klartext) | Rust, AGPL-3 | `.prg` parser + BEST/2 VM, read-only live runner and MCP server | F-series ENET only; useful for its "gate refuses any write the bytecode emits" pattern |
| [lpcvoid bmw-better-vm](https://github.com/lpcvoid/bmw-better-vm) | C++, GPL-3 | Older partial VM (2020) | Historical; opcodes incomplete |
| [OpenDS2](https://github.com/ecrawshaw/opends2), [bmw-mac-diag](https://github.com/sakharniy/bmw-mac-diag) | Swift/C#, GPL-3 | EdiabasLib hosted on macOS | Stage-1 prior art; neither validated on BMW-FAST over K-line |
| [Klemme15](https://github.com/Expos-AI/Klemme15) | C#, GPL-3 | Native stack from public standards, with EdiabasLib as an "EDIABAS mode" | States pre-03/2007 E60s "need EDIABAS mode", which it labels Windows |
| [beemuu](https://github.com/ohgeeceee/beemuu) | Rust/Tauri, GPL-3 | Hard-coded KWP2000 for "E36 → early E9x" over FT232RL | No SGBD interpreter; telegrams per module |

Hard-coded telegram projects such as beemuu, e36cli and handmade0octopus/ds2 mostly target DS2-era cars. For a BMW-FAST car they offer framing and echo-handling patterns, not usable telegrams. Running BMW's own binaries under Wine or CrossOver is a dead end. No first-hand report shows INPA or EDIABAS driving a K+DCAN cable under Wine, Wine's COM mapping is fragile, and one E46Fanatics reply simply points Linux users to EdiabasLib ([E46Fanatics](https://www.e46fanatics.com/threads/bmw-scan-software-on-linux.1282577/); [WineHQ forum](https://forum.winehq.org/viewtopic.php?p=135747)). VM USB passthrough on Apple Silicon is itself reported as fragile ([QEMU #2178](https://gitlab.com/qemu-project/qemu/-/issues/2178); [UTM #3778](https://github.com/utmapp/UTM/issues/3778)). That strengthens the case for leaving the VM.

The design's choice of the K+DCAN cable as the only interface is also supported by the evidence on alternatives. **Stock ELM327 and STN (OBDLink) adapters are D-CAN-only in EdiabasLib**, which drives them as a raw CAN pipe (`ATSH6F1`), so they cannot reach a pre-03/2007 E61's body modules ([AdapterTypes](https://uholeschak.github.io/ediabaslib/docs/AdapterTypes.html)). The only wireless K-line path is a PIC18F25K80 ELM327 clone reflashed with Holeschak's Deep OBD firmware ([Replacement firmware](https://github.com/uholeschak/ediabaslib/blob/master/docs/Replacement_firmware_for_ELM327.md)). The adapters BimmerCode lists for E-series cars are D-CAN or ENET devices ([BimmerCode adapters](https://bimmercode.app/adapters)).

## A 2005 E61 speaks BMW-FAST over one diagnosis wire

The E60 family has a single diagnosis wire, the "K-wire", on OBD pin 7. The gateway routes from it to PT-CAN, K-CAN, MOST and, before 09/2005, byteflight. **The gateway is the SGM up to 09/2005 and the KGM from 09/2005**, so Ryan's 2005 car could have either, depending on build date ([BMW TIS SBT 61 02 03](https://5series.net/forums/attachments/diy-do-yourself-14/44001d1202390369-e60-technical-information-topic-bus_diagnosis_all_models_from_e60.pdf)). The interface layer does not need to know which. The KGM does matter for battery behaviour, covered below. EdiabasLib does not list the E60 among the cars that need OBD pins 7+8 bridged; that list is the DS2 cars from E36 to E86 ([AdapterTypes](https://uholeschak.github.io/ediabaslib/docs/AdapterTypes.html)). Coding forum posts insist pre-03/2007 E60s need the bridge "for the double K-line" ([5series.net](https://5series.net/forums/diy-do-yourself-14/ncsexpert-code-mods-108089/page76)). The cable's K-line switch position bridges 7+8 anyway, so the practical answer is simply "switch left". The spike notes' statement that the E61 is "pins 7+8 bridged" should be treated as a convention, not a requirement.

EdiabasLib does not choose the protocol itself: **the SGBD's `INITIALISIERUNG` job sets `CommParameter[0]` (the "concept") and a parameter array**, and `EdInterfaceObd` dispatches on that concept ([EdInterfaceObd.cs](https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/EdiabasLib/EdInterfaceObd.cs), about lines 480–850 at commit `5e9efc5`). The concepts that matter on a K+DCAN cable are these:

| Concept | Line settings | Framing / checksum | Init / keep-alive | DTR on K+DCAN |
|---|---|---|---|---|
| 0x0006 DS2 | `par[1]` baud (9600), **Even** | `Addr Len Cmd… CS`, length counts the whole telegram, **XOR** | none | low |
| 0x010C KWP2000 BMW | `par[1]` baud, None | ISO 14230 | **fast init** (25 ms break) + StartComm telegram; SGBD-supplied tester present | low |
| 0x010D KWP2000* | `par[1]` baud, **Even** | 4-byte header, **XOR** | none | low |
| 0x010F **BMW-FAST** | `par[1]` baud (**115200**), None | `Fmt Tgt Src [Len] Data… CS`, **additive sum** | none at this layer | **held high** |
| 0x0110 D-CAN | 115200 None to adapter | BMW-FAST frames, which the cable turns into ISO-TP | none | held high |
| 0x0001–0x0003 | 5-baud / KWP1281 | — | 5-baud init | Concept 1 refuses with IFH-0006 on an echoing adapter ("only with ADS adapter"); the spike's reading says the same for concepts 2 and 3 |

BMW-FAST is the dominant concept on this car. The E6x diagnosis bus runs at about 115 kbit/s, which matches BMW-FAST's 115200 baud, and EdiabasLib describes BMW-FAST as "BMW E series with BMW-FAST protocol (DS2 successor)" ([AdapterTypes](https://uholeschak.github.io/ediabaslib/docs/AdapterTypes.html)). Which concept each of Ryan's SGBDs actually sets has not been extracted. Carry-over modules may still use DS2 or KWP2000*.

Several wire-level details have to be ported from the code, not the docs. **EdiabasLib's own simulation doc has the checksums swapped**: it says DS2 uses a sum and BMW-FAST uses XOR. Its worked example disproves that, since the bytes `83 F1 01 59 02 7F` sum to 0x24F and the frame's checksum is `4F` ([simulation doc](https://github.com/uholeschak/ediabaslib/blob/master/docs/EdiabasLib.simulation_file.md); `CalcChecksumBmwFast` in [EdInterfaceBase.cs](https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/EdiabasLib/EdInterfaceBase.cs)). The BMW-FAST length rule is `buf[0]&0x3F`, plus 3 header bytes. A zero length means a length byte follows in `buf[3]`, and if that is also zero, a 16-bit length follows. **Every byte the host sends comes back as an echo on a K+DCAN cable.** EdiabasLib reads exactly `sendLength` echo bytes within a fixed 100 ms `EchoTimeout`, compares them, and on mismatch drains the port and fails with IFH-0003. Only then does it parse the response, which must start with `(Fmt & 0xC0) == 0x80`. `7F sid 78` (response pending) frames extend the wait to `TimeoutNr78`, tracked per responding ECU address. That tracking is what lets functionally addressed requests collect several answers ([EdInterfaceObd.cs](https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/EdiabasLib/EdInterfaceObd.cs), L4110–4265). The P1–P4 timings come from the SGBD, not ISO defaults: the EDIC path maps P2 to `TimeoutStd`, P1 to `TimeoutTelEnd`, P3 to `RegenTime` and P4 to `InterbyteTime`. Receive succeeds if the first byte arrives within the timeout and no later inter-byte gap exceeds `TimeoutTelEnd`. `ObdAddRecTimeout` (default +20 ms) is added to both.

Inside the cable are an FT232RL, an ATF16V8B PLD that steers TX/RX between the K-line, the L-line and an ATmega, the ATmega with its CAN controller, and the 7/8 switch ([Build_Bluetooth_D-CAN_adapter](https://github.com/uholeschak/ediabaslib/blob/master/docs/Build_Bluetooth_D-CAN_adapter.md)). Holeschak's open replacement firmware shows the adapter-internal command set. These are BMW-FAST-shaped frames sent to `F1→F1`. `82 F1 F1 FE FE` reads ignition and `FD FD` reads the firmware version. **Selectors 00–03 write block size, separation time, CAN mode and PLD mode into the adapter, and `FF FF` resets it** ([CanAdapter.c](https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/CanAdapter/CanAdapter/CanAdapter.c)). When the adapter cannot report ignition by telegram, EdiabasLib infers it from the DSR modem line. bmw-mac-diag found that IFH-0003, the echo failure, in practice means "flip the switch" ([bmw-mac-diag](https://github.com/sakharniy/bmw-mac-diag)). That mapping belongs in the bridge's error messages.

Safety has to work at two levels. For KWP and BMW-FAST frames, the service ID is `data[0]` after the header. The research supports refusing:

- 0x10 with any session other than default
- 0x11
- 0x14 (clear DTCs)
- 0x27
- 0x2E, 0x2F and 0x30
- 0x31–0x37
- 0x3B and 0x3D
- 0x85
- 0x28 and 0x29
- any functionally addressed frame (format bits `11`) carrying one of the above, because it hits every ECU at once

The supporting SID table comes from ISO 14230-3, which is paywalled. The names in the notes are a secondary summary, so the list should be confirmed against captured traffic before it becomes a hard gate. DS2 command bytes are manufacturer-specific, so DS2 still needs the job-level policy the bridge already enforces. The KGM matters here: from 09/2005 its bistable relay cuts terminal-30g-f loads when quiescent current stays high, after about 2 minutes of after-run ([workshop-manuals KGM](https://workshop-manuals.com/bmw/5_series_e60/m5_s85_sal/6_si_techniques/61__general_electrical_system/11_sbt__body-gateway_module_e60_e61_e63_e64/page_4252/)). BMW's own bus diagnosis treats undervoltage entries in two or more modules as the likely cause of a bus fault ([TIS SBT 61 02 03](https://5series.net/forums/attachments/diy-do-yourself-14/44001d1202390369-e60-technical-information-topic-bus_diagnosis_all_models_from_e60.pdf)). Long read sessions on a weak battery do more than drain it: they write misleading faults across the car.

## macOS can lower the FTDI latency timer, but .NET cannot hold a break

The draft design says Apple's driver "fixes the FTDI latency timer at 16 ms". The evidence says otherwise. On macOS 26 the FTDI driver is a **DriverKit dext** (`com.apple.DriverKit-AppleUSBFTDI.dext`, `IOUserSerial`), not a kext, and it matches VID 0x0403 / PID 0x6001, the FT232R. This was checked locally on 2026-11-22 with `plutil` and `kmutil`, with no cable attached. The SDK header `IOKit/serial/ioss.h` defines `IOSSDATALAT` to set receive latency and `IOSSIOSPEED` for non-standard baud rates. In 2022 Apple's Feedback Assistant told a developer that "Latency timer can be configured with ioctl IOSSDATALAT". A user then found the **unit is milliseconds, not the documented microseconds**: a value of 1 worked, 0 "behaved oddly", and a 1000-character echo loopback dropped from about 2 s to about 1 s ([OpenBCI forum](https://openbci.com/forum/index.php?p=%2Fdiscussion%2F3108%2Fftdi-driver-latency-timer-for-macbook-m1)). That test ran on Mojave and Monterey, before the dext, and Apple's developer forum thread on the topic has no official answer ([Apple forum 690306](https://developer.apple.com/forums/thread/690306)). So this is strong but unconfirmed for macOS 26. The value is volatile chip state: libftdi users saw it reset to 16 on replug, so it must be reapplied on every open.

The fallback route the design keeps for stage 2, claiming the device from Apple's driver with libusb or D2XX, is expensive. libusb on darwin can only detach a kernel driver with the `com.apple.vm.device-access` entitlement or as root, and Apple is reluctant to grant that entitlement to non-VM apps ([libusb capture commit](https://flutter.googlesource.com/third_party/libusb/+/abd788c203624930241f45f32b501c16936bd806%5E%21/); [Apple forum 857175](https://developer.apple.com/forums/thread/857175)). FT232R bit-bang mode, the only thing D2XX adds, has known silicon errata in its timing ([Hackaday](https://hackaday.com/2018/06/15/silicon-bugs-in-the-ftdi-ft232r-and-a-tidy-rf-vco-project)). Linux is the easy platform. `ftdi_sio` exposes `latency_timer` in sysfs in ms, and an unprivileged tty owner can set `ASYNC_LOW_LATENCY` through `TIOCSSERIAL`, which forces the timer to 1 ms. `TIOCSBRK` and `TIOCCBRK` each send one immediate USB control request, and a parity change on an open port goes out as SET_DATA at once ([ftdi_sio.c](https://raw.githubusercontent.com/torvalds/linux/master/drivers/usb/serial/ftdi_sio.c)).

Baud rates are not a problem. The FT232R divides 3 MHz by n + k/8 ([FTDI AN232B-05](https://www.ftdichip.com/old2020/Support/Documents/AppNotes/AN232B-05_BaudRates.pdf)):

| Requested | Actual | Error |
|---|---|---|
| 9600 | 9600 | exact |
| 10400 | 10398.6 | −0.013% |
| 115200 | 115384.6 | +0.16% |

The slowest possible rate is about 183 baud, so **5-baud init can only be emulated with timed breaks**.

The real stage-1 hazard is in .NET. On Unix, System.IO.Ports implements `BreakState = true` as `tcsendbreak(fd, 0)`, a one-shot pulse of **0.25–0.5 s**. `BreakState = false` does nothing, and the getter returns a cached field ([SerialStream.Unix.cs](https://source.dot.net/System.IO.Ports/System/IO/Ports/SerialStream.Unix.cs.html); [pal_termios.c](https://raw.githubusercontent.com/dotnet/runtime/main/src/native/libs/System.IO.Ports.Native/pal_termios.c); [kw1281test#35](https://github.com/gmenounos/kw1281test/issues/35)). EdiabasLib's fast init holds BreakState for 25 ms inside a 50 ms window and bit-bangs 5-baud init through the same property. **Any SGBD using concept 0x010C (KWP2000 BMW) will therefore fail its wake-up on macOS and Linux unless the break is reimplemented with `ioctl(TIOCSBRK/TIOCCBRK)`.** BMW-FAST and DS2 have no init and are unaffected. The rest of the shim is workable:

- Non-standard rates go through `IOSSIOSPEED` on macOS and termios2 on Linux, and are re-applied after every `tcsetattr`.
- `BytesToRead` maps directly to `FIONREAD`, so EdiabasLib's 1 ms polling still works.
- The 30 ms `TimeoutResolution` does not hurt EdiabasLib, which polls rather than relying on read timeouts.
- There is **no API for latency, `TIOCEXCL` or thread priority**, so those need P/Invoke on the file descriptor.

The ioctl values for arm64 macOS were measured locally:

| ioctl | Value |
|---|---|
| `TIOCEXCL` | 0x2000740d |
| `TIOCSBRK` | 0x2000747b |
| `TIOCCBRK` | 0x2000747a |
| `TIOCMBIS` | 0x8004746c |
| `TIOCMBIC` | 0x8004746b |
| `IOSSIOSPEED` | 0x80085402 (`speed_t` is 8 bytes) |
| `IOSSDATALAT` | 0x80085400 |

On the Zig side, the 0.16 standard library has `tcgetattr`/`tcsetattr` and Linux's `T.IOCEXCL`, `IOCSBRK` and `IOCGSERIAL`. It has **no Darwin TIOC\* or IOSS\* constants and no termios2/`BOTHER`**, so these must be declared by hand (local grep of the Homebrew Zig 0.16 std, 2026-11-22). The two serial libraries do not settle this. **jcollie/zig-serial** has the right feature set: DTR/RTS, `sendBreak`, exclusive open via `flock` + `TIOCEXCL`, termios2 arbitrary baud on Linux, and read-back of the settings the driver actually applied. But it targets **Zig 0.17**, and its licence is in doubt: the research notes call it MIT, while the GitHub API on 2026-11-22 detected no licence at all ([zig-serial](https://github.com/jcollie/zig-serial)). **ZigEmbeddedGroup/serial** is MIT, targets 0.16 and has 98 stars, but has no break, custom-baud or latency API ([ZigEmbeddedGroup/serial](https://github.com/ZigEmbeddedGroup/serial)). No Zig KWP2000, OBD, K-line or BEST/2 code turned up. Break timing itself is the least certain part of either stage. kw1281test's author found macOS `TIOCSBRK`/`TIOCCBRK` timing imprecise on a logic analyser around 2022, and no one has measured edge timing on the macOS 26 dext ([kw1281test#35](https://github.com/gmenounos/kw1281test/issues/35)).

## BEST/2 is a small register VM with a ready-made differential oracle

The SGBD container has no BMW specification, but EdiabasLib's parser and the MIT-licensed BESTDIS disassembler bundled in its repo describe it fully ([BESTDIS Program.cs](https://github.com/uholeschak/ediabaslib/blob/master/Tools/BESTDIS/Program.cs); [EdiabasNet.cs](https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/EdiabasLib/EdiabasNet.cs)):

- Files start with the magic `@EDIABAS OBJECT\0`.
- A little-endian header holds absolute offsets at 0x7C–0x98: uses, job code, tables, job list, description and info.
- The dword at 0x10 is 0 for a `.grp` and 1 for a `.prg`.
- Everything after the header is XOR 0xF7, except a few counts read raw. The table count is the odd one out and is XOR-decoded.
- Jobs are 0x44-byte records. Tables are cp1252 string cells, row-major, with row 0 as the column headers.
- `USES` inheritance is resolved one level deep, and local jobs override inherited ones.

The two references disagree on one field: EdiabasLib reads the info block's date as 0x20 bytes and BESTDIS as 0x24. The interpreter is a register machine:

| Register file | Count |
|---|---|
| Byte (B0–BF, A0–AF) | 32 |
| I | 16 |
| L | 8 |
| S (string) | 16 |
| F (float) | 8 |

It has C/Z/S/V flags, an error-trap mask that lets an SGBD swallow errors such as IFH-0056, and result sets built by `erg*` opcodes, with set 0 as the synthetic system set. Each instruction is an opcode byte plus a mode byte selecting one of 16 addressing modes for each operand. **Porting the operand decoder and flag helpers exactly matters more than the opcode count.** **184 opcodes are defined (0x00–0xB7), and 16 have null handlers in EdiabasLib.** These are mostly subroutine and flash-transfer opcodes (`jtsr`, `ret`, `pcall`, `xdownl`, `xsendr`, `xopen`…), and the maintainer says they "seem to be not required in the current ECU files" ([README](https://github.com/uholeschak/ediabaslib)). A frequency count over one BMW-derived DME disassembly found none of them. Behaviour also branches on the emulated EDIABAS version (7.7.3) and `CompatMode` (7.3.0 math by default), so the port must pin both.

Two semantic traps matter for the bridge's read-only guarantees. First, **`ExecuteVJob` runs `INITIALISIERUNG` before `_JOBS` and the other virtual jobs unless `NoInitForVJobs` is set, and in BMW SGBDs that job opens the cable.** Deep OBD itself sets the flag before metadata reads. Whether real EDIABAS behaves the same is undocumented. BMW's standard INIT comment says EDIABAS runs it on the first access to an SGBD. Second, **resolving a `.grp` runs `IDENTIFIKATION` against the ECU**, so group files cannot be resolved offline. A `.prg` can.

Upstream's testing method is the best template for both stages. `coverage.bat` runs one job list through three drivers: EdiabasLib, EdiabasLib behind the api32 shim, and **real BMW `api32.dll`**. It then compares their logs. The jobs cover flag semantics, `a2fix` edge cases, trap paths and result formatting ([coverage.bat](https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/Test/Scripts/coverage.bat)). The committed test SGBDs (`cmd_test1`, `cmd_test2`, `base1`, `base2`, `cmd_ident`) were written by Holeschak. EdiabasLib's `.sim` format has been a request/response INI with wildcards, masks, computed checksums and multi-frame functional responses since September 2024. `EdSimFile.cs` is already one of the spike's seven core files ([simulation doc](https://github.com/uholeschak/ediabaslib/blob/master/docs/EdiabasLib.simulation_file.md)). bimmerscope has already built the cross-language version: `tools/oracle` builds EdiabasLib's test tool in a .NET 8 Docker image and diffs it against the Rust VM job by job on `.sim` recordings ([bimmerscope](https://github.com/TheOnlyLoki/bimmerscope)). At Info log level, EdiabasLib also logs every executed instruction, which gives a step-by-step trace to diff against.

On licensing, the decided GPL-3 route is the clean one. A Zig translation of EdiabasNet is a derivative work and must be GPL-3 if distributed ([GPLv3](https://www.gnu.org/licenses/gpl-3.0.html)). bimmerscope's GPL-3 Rust can be borrowed from with attribution. ediabasx's PolyForm Noncommercial licence, which GitHub reports as NOASSERTION, rules out copying, and the notes flag that a noncommercial licence on a self-described EdiabasLib port sits uneasily with GPL-3 anyway ([ediabasx](https://github.com/emdzej/ediabasx)). klartext is AGPL-3 and MrHilgert/ediabas-prg has no licence, so neither should be copied from. In the EU, programming languages and data-file formats are not protected expression ([SAS v WPL](https://en.wikipedia.org/wiki/SAS_Institute_Inc_v_World_Programming_Ltd); [Directive 2009/24/EC](https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX:32009L0024)). BMW's SGBDs are BMW copyright and must stay user-supplied, as the maintainer has said himself ([issue #175](https://github.com/uholeschak/ediabaslib/issues/175)). Two less obvious hazards remain. The upstream folder also holds BMW-derived files (`dm338ds1_interbyte`, `d60m47a0_dcan`) that must not be copied. And **`cmd_test2.prg` was compiled with BMW's best2 toolchain, which links `B2Runtim.lib`, so it may embed BMW runtime code.** Check that before committing it as a fixture.

## Implications for the native-engine plan

The research supports the overall plan: approach A, GPL-3, stdio child process, one contract and a recording bridge. OpenDS2 independently reached almost the same architecture. What it changes is the stage-1 serial layer, the scope of stage 2's transport work, and what has to be proved before the car.

### What to reuse

Keep the spike's seven-file core and the draft's file list for `EdInterfaceObd`. That is narrower and cleaner than OpenDS2's "compile everything and stub", and it already builds. Keep bmw-mac-diag's recipe, the net10.0 DLL plus unix `System.IO.Ports` from a pinned release, as a cross-check that the source build behaves like a release build. Reuse EdiabasLib's own `.sim` support for EdiabasLib-side replay. Make the recording format a superset that can **export standard `.sim` files** (requests, responses, keybytes) alongside the richer JSON the design needs (comm parameters, telegram order, timing, result sets). Then the same recordings drive EdiabasLib's simulation mode, the Zig engine, and bimmerscope-style oracles. Adopt bimmerscope's oracle pattern directly: the stage-1 `ediabasd` is the oracle, because upstream's `EdiabasTest` is `net481`-only. Use upstream's `coverage.bat` job list as the interpreter conformance suite, and run it **three ways**: real EDIABAS in the VM, stage-1 .NET, and Zig. That gives the "silent divergence" anti-goal a check against BMW's interpreter, not just EdiabasLib. For stage 2's container parser, BESTDIS (MIT) is a non-GPL reference and a byte-for-byte disassembly oracle. ediabasx-embedded's layout (transport vtable, no-malloc VM, variant swap in a separate wrapper) can be read for structure only.

### What to change in the draft

| Draft says | Change to | Why |
|---|---|---|
| AppleUSBFTDI "fixes the FTDI latency timer at 16 ms" | Set `IOSSDATALAT = 1` (unit is ms) after every open on macOS; `ASYNC_LOW_LATENCY` via `TIOCSSERIAL` on Linux | Apple-confirmed ioctl; user-measured; resets on replug |
| Stage 1 links `EdInterfaceObd` unmodified; "if flaky, raise `ObdAddRecTimeout`" | Open the device yourself (`open` + `TIOCEXCL` + `IOSSDATALAT`) and either pass the fd into a `SerialPort`-compatible wrapper, or carry one documented patch in `third_party/patches/` that replaces `BreakState` with `TIOCSBRK`/`TIOCCBRK` and adds the latency ioctl | .NET's Unix `BreakState` is a 0.25–0.5 s one-shot, which breaks fast init; no latency or exclusivity API exists |
| Stage 2 step 5: libusb/IOKit direct FTDI as the first improvement | Demote it. Only pursue it if `IOSSDATALAT` fails on the macOS 26 dext | libusb capture needs root or an Apple VM entitlement; FT232R bit-bang has errata |
| Stage 2 transport scope includes "5-baud init" | Defer 5-baud. Build BMW-FAST, then DS2/KWP2000*, then 0x010C fast init | Concept 1 refuses on echoing cables (per the spike's reading, 2 and 3 too); 5-baud matters only for a generic OBD-II fallback |
| Interface gate refuses programming voltage, boot, SI relay and raw sends | Also gate at frame level: refuse the write/clear/session/security/routine/transfer SIDs listed above, any functional (0xC0–0xFF) frame carrying one, and any `F1→F1` adapter frame other than the FA/FD/FE reads | The SGBD bytecode builds telegrams; the cable has writable configuration (CAN mode, PLD mode, reset) |
| Pin Zig 0.16 and use a serial library | Write a small own serial module with hand-declared Darwin ioctls and Linux termios2, or vendor ZigEmbeddedGroup/serial (MIT, 0.16). Use jcollie/zig-serial only after reading its LICENSE file and resolving the 0.16/0.17 mismatch | GitHub detects no licence for zig-serial; it targets 0.17; std lacks the constants |
| Spike NOTES: E61 is "pins 7+8 bridged" | "Use the K-line (left) switch position." The bridge is harmless, and EdiabasLib does not require it for the E60 | Sources conflict; the switch position settles it in practice |

Map IFH-0003 to "check the cable switch is on K-line" in user-facing errors, as bmw-mac-diag does. Make `NoInitForVJobs = true` mandatory on the describe instance, as the draft already says, and treat `.grp` resolution as a cable operation that `describe` must never perform. Run the time-critical send/receive loop on a raised-priority thread (`THREAD_TIME_CONSTRAINT_POLICY` on macOS, `SCHED_FIFO` or busy-wait on Linux). In .NET, keep that loop allocation-free.

### Open risks, ranked

The largest risk is that **BMW-FAST over K-line from macOS is unproven by anyone**: no surveyed project has validated a pre-03/2007 E60/E61 on a Mac, and bmw-mac-diag's "latency not needed" came from a D-CAN car. Second is the **concept mix** in Ryan's SGBDs. If any module uses 0x010C, stage 1 needs the break fix before it can read that module, and the 25 ms break timing on Apple's dext is unmeasured. Third, **`IOSSDATALAT` behaviour on the macOS 26 dext** is extrapolated from pre-dext forum tests. Fourth, **original-firmware cable semantics** (what DTR-high does, ignition reporting, echo in D-CAN mode) are known only from Holeschak's replacement firmware. Copy EdiabasLib's behaviour rather than reason from first principles. Fifth, **clone FT232R chips**: EdiabasLib warns that fakes cause communication problems, and identification strings for Ryan's cable are unverified. Last, bimmerscope's opcode-completeness and fault-clearing scope are unverified claims: borrow ideas, not trust.

### Verify at the desk versus at the car

| At the desk (no car, or cable unplugged from the car) | At the car (Ryan present, charger on, never unattended) |
|---|---|
| **Concept census:** for every SGBD in the Ecu folder, run `INITIALISIERUNG` against an offline stub that records the `xsetpar` parameter array instead of refusing. Tabulate concept, baud and timeouts. This decides whether fast init matters at all. It is an inference that the stub can capture this before the first send; try it | First live job: `IDENT` on one BMW-FAST module, with IFH trace on in both the VM and native, compared byte for byte |
| **Opcode census:** disassemble every SGBD (BESTDIS or a Zig disassembler) and confirm none of the 16 unimplemented opcodes appears | Echo and latency in practice: count IFH-0003 and short-read failures with `IOSSDATALAT` at 1 versus the default |
| `coverage.bat` job list through real EDIABAS (VM), stage-1 .NET and later Zig on `cmd_test1/2`; diff result sets | `FS_LESEN` on the `docs/at-the-car.md` modules and `vehicle` (DSR/adapter ignition, battery voltage) versus the VM in the same session |
| Real EDIABAS `_JOBS` with `IfhTrace=3` and the cable unplugged: does INIT run? | Any 0x010C module found by the census: one fast-init wake-up, scoped if possible |
| Cable on the Mac, not in the car: enumerates as 0403:6001; opens `cu.usbserial-*` with `TIOCEXCL`; sets 9600E/115200N/10400 and reads them back; `IOSSDATALAT` accepted; loopback echo timing at latency 1 versus 16; `82 F1 F1 FD FD` version read | Linux repeat with the same cable, shorter run |
| Break-pulse width on a logic analyser or scope (macOS dext and Linux), if any 0x010C module exists | Session length and battery voltage logged throughout, given KGM after-run behaviour |
| Check the licence and provenance of `cmd_test2.prg` (B2Runtim) and zig-serial before committing either | — |

The thread running through all of this: interpreter fidelity is a solved, testable problem, with three independent oracles available at the desk. The car-facing risk is concentrated in a few hundred lines of serial timing that no one has run on this car from a Mac. Stage 1 is therefore less "run EdiabasLib natively" than "give EdiabasLib a correct Unix serial port". That layer is also stage 2's first real Zig deliverable, so it should be built once, behind a small interface with the ioctls declared explicitly, and proved at the desk before the first ignition-on minute is spent.
