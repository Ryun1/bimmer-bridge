# Alternative hardware interfaces and access paths for a 2005 BMW E61 (K-line era) from macOS/Linux

Scope note: the car is a 2005 E61 550i (N62), built before 03/2007, so its diagnostic bus at the OBD socket is K-line (pin 7, with pin 8 as a second K-line on some E-series), not D-CAN. Most "BMW adapter" marketing targets D-CAN (E-series from 03/2007) or ENET (F/G-series). Local EdiabasLib checkout examined: `/Users/ryan/projects/ediabaslib-spike/upstream` (HEAD `5e9efc5 Code simplified`; the tree contains files such as `EdiabasClampSnoop.cs` and `EdiabasVoltageBridge.cs` that may be local spike additions rather than upstream, so check before citing them as upstream).

## 1. ELM327 / STN11xx / STN2xxx (OBDLink) adapters on a K-line E60/E61

### Takeaway
Stock ELM327 and STN (OBDLink) adapters cannot carry EdiabasLib's BMW-FAST/KWP2000* traffic on a pre-03/2007 E60/E61: EdiabasLib's ELM327 support is D-CAN only. On a 2005 E61 they are useful only for generic OBD-II emissions data, unless the adapter is a PIC18F25K80 ELM327 clone reflashed with Deep OBD firmware (see section 2).

### Cited Findings
- EdiabasLib's adapter matrix: ELM327 = BMW-DS2 "No", BMW-FAST "D-CAN only"; FTDI USB and Deep OBD = DS2 "Yes", BMW-FAST "Yes" (K-line and D-CAN). — [EdiabasLib AdapterTypes.md](https://uholeschak.github.io/ediabaslib/docs/AdapterTypes.html) (local: `docs/AdapterTypes.md`)
- "Only D-CAN is supported (BMW vehicles starting from 3/2007)." Recommended ELM327 versions are 1.4b, 1.5 and "origin 2.1" on PIC18F25K80 (no MCP2515). Fake PIC18F25K80 v1.5 chips "will not work". "ARM based adapters are in most cases not 100% ELM327 compatible. The adapter manufacturer must explicitly state, that the adapter is Deep OBD compatible." — [AdapterTypes.md](https://uholeschak.github.io/ediabaslib/docs/AdapterTypes.html)
- In source, `EdElmInterface.cs` drives the ELM327 as a raw CAN pipe. Its init sequence sets `ATSH6F1` (tester CAN ID 0x6F1), `ATSPB` (user CAN protocol B) and `ATPBC101` ("CAN B Custom Protocol 11/500 with var. DLC"). There is no K-line path. — local `EdiabasLib/EdiabasLib/EdElmInterface.cs` lines 61-83, 347 ([upstream repo](https://github.com/uholeschak/ediabaslib))
- Transports in EdiabasLib, by port-string prefix: `BLUETOOTH` (`EdBluetoothInterface.cs`, desktop build uses InTheHand 32feet `BluetoothClient`), `ELM327WIFI` (`EdElmWifiInterface.cs`), `DEEPOBDWIFI` (`EdCustomWiFiInterface.cs`), `FTDI` (D2XX via `[DllImport("ftd2xx")]` in `Ftd2xx.cs`) and plain COM ports. — local source, `EdiabasLib/EdiabasLib/*.cs`
- Deep OBD ships an `E61ElmWifi.cccfg` (interface `ELMWIFI`). Given the D-CAN-only rule, this can only apply to 03/2007+ LCI E61s. — local `BmwDeepObd/Xml/E61/E61ElmWifi.cccfg`
- Deep OBD app: stock ELM327 adapters are "D-CAN protocol only". FTDI USB "INPA compatible" cables and reflashed ELM327s cover "all protocols". — [Deep_OBD_for_BMW_and_VAG.md](https://github.com/uholeschak/ediabaslib/blob/master/docs/Deep_OBD_for_BMW_and_VAG.md)
- The ELM327 chip supports ISO 9141-2 (5-baud init, 10.4 kbit/s), ISO 14230-4 (5-baud and fast init) and CAN variants. The K-line support is for the OBD-II legislated protocols. — [ELM327 datasheet (scantool.net)](https://www.scantool.net/scantool/downloads/103/elm327dsh.pdf); [Wikipedia ELM327](https://en.wikipedia.org/wiki/ELM327)
- OBDLink LX uses STN1155 (ISO/KWP2000, PWM, VPW, CAN). OBDLink MX uses STN2255. Retailer descriptions cover generic OBD-II only, not BMW DS2/BMW-FAST. — [Gendan OBDLink LX](https://www.gendan.co.uk/product_OBDLINKLX.html), [Gendan OBDLink MX](https://gendan.co.uk/product_OBDLINKMX.html)

### Inferences
- An ELM327/STN adapter on a 2005 E61 works for generic OBD-II emissions PIDs (if the DME answers on K-line) but not for EdiabasLib/SGBD jobs to non-emissions modules (CAS, KOMBI, LM, DSC, airbag and so on). The project should exclude stock ELM327/STN adapters for K-line cars, or treat them only as an "OBD-II fallback" transport.
- STN chips have richer low-level K-line programming (custom headers and init) than ELM327. In principle someone could build a KWP2000*/BMW-FAST-over-K-line transport on them, but EdiabasLib does not do this and I found no project that does. Treat it as unproven.
- Bluetooth/Wi-Fi/USB variants of ELM differ only in transport. The D-CAN-only limitation applies to all of them. Wi-Fi (TCP to 192.168.0.10:35000-style) is the easiest to drive from macOS/Linux. Classic Bluetooth SPP is awkward on macOS (no RFCOMM serial in recent macOS for most apps). BLE needs GATT code.

### Gaps
- I found no authoritative source (OBDLink/STN docs or a forum report) confirming or refuting that STN adapters can do BMW-FAST over K-line to body modules on a pre-2007 E60.
- I did not verify the macOS Bluetooth SPP situation from a source in this pass. It is background knowledge and needs confirming.

## 2. Deep OBD custom adapters (reflashed ELM327, CanAdapterElm, custom Bluetooth K+DCAN)

### Takeaway
The only wireless option that EdiabasLib supports for a K-line E61 is the "Deep OBD" adapter: a PIC18F25K80 ELM327 clone reflashed with the open-source CanAdapterElm firmware, sold preprogrammed or DIY-flashed with a PICkit 3. It supports K-line (all protocols) and D-CAN over Bluetooth or ESP8266 Wi-Fi, using a custom protocol rather than AT commands.

### Cited Findings
- The replacement firmware for ELM327L adapters gives "Faster and more stable CAN communication", "K-Line support (all protocols)", a bootloader for updates without opening the device, and sleep mode. Firmware variants: 38400 baud for unmodified adapters, 115200 for modified Bluetooth (recommended). — [Replacement_firmware_for_ELM327.md](https://www.github.com/uholeschak/ediabaslib/blob/master/docs/Replacement_firmware_for_ELM327.md)
- Hardware: buy a "PIC18F25K80 ELM327", preferably with a CSR BC417 Bluetooth chip (BK3231 also supported). Firmware subdirectories exist for `default`, `def115200`, `bc04`, `hc04`, `hc06` (BLE), `esp8266` (Wi-Fi), `yc1021`, `spp_uart`. The source is in `EdiabasLib/CanAdapterElm`. First programming needs a PICkit 3. — same source
- Preprogrammed adapters are sold on eBay and at shop.ibus-app.de (the latter has a fixed pin 7+8 bridge for DS2 cars), with a licence for one F-series coding. — same source
- The Deep OBD adapter can stand in for an OBD/ADS interface on Windows INPA/Tool32/ISTA-D through EdiabasLibConfigTool, which patches the EDIABAS installations. — same source
- Older "Custom Bluetooth D-CAN/K-Line adapter": an FTDI K+DCAN cable with a replaced PLD (ATF16V8BQL) plus an HC-05 module. Now marked "not supported any more"; users are pointed to the ELM327 replacement firmware instead. The same AVR firmware can still replace stock K+DCAN cable firmware. — [Build_Bluetooth_D-CAN_adapter.md](https://github.com/uholeschak/ediabaslib/blob/master/docs/Build_Bluetooth_D-CAN_adapter.md)
- In the support matrix, "Custom" and "Deep OBD" are "Yes" for both BMW-DS2 and BMW-FAST. DS2 cars (E36/E38/E39/E46/E52/E53/E83/E85/E86) need a pin 7+8 bridge. — [AdapterTypes.md](https://uholeschak.github.io/ediabaslib/docs/AdapterTypes.html)

### Inferences
- For a native macOS/Linux engine, the Deep OBD Wi-Fi (ESP8266) variant is the most portable wireless target: a plain TCP socket, using `EdCustomWiFiInterface` (`DEEPOBDWIFI`) as the reference implementation. Bluetooth variants inherit the macOS SPP problem.
- Availability is niche: one maintainer, AliExpress clones of mixed quality (fake PIC18F25K80s), and a few preprogrammed sellers. Treat it as a "supported if you have one" option, not a recommended default.

### Gaps
- No latency or reliability measurements for Deep OBD Bluetooth versus a USB K+DCAN on K-line were found.

## 3. Bluetooth/Wi-Fi adapters for BimmerCode, BimmerLink and Carly on E-series

### Takeaway
Consumer coding apps target E-series mostly as "2008+" (D-CAN) cars. Their adapters (OBDLink CX/MX+, vLinker BM/MC, MHD/THOR Wi-Fi) are D-CAN/ENET devices driven by the apps' own proprietary stacks. None is documented as supporting a pre-03/2007 K-line E60/E61.

### Cited Findings
- BimmerCode's adapter page lists, for E-series, OBDLink CX/MX+/LX/SX/EX and UniCarScan UCSI-2100 ("all series"), vLinker BM/BM+/MC/MC+ (sometimes "E 2008+"), Veepeak BLE/BLE+ ("E 2008+"), MHD Universal Wi-Fi ("E 2008+"), MHD/THOR Wi-Fi, and a "DCAN USB cable" (E and R series). ENET adapters are F/G/I only. The page says "not every adapter can be used for all supported vehicles". It does not mention K-line. — [BimmerCode adapters](https://bimmercode.app/adapters)
- OBDLink CX is marketed as "MADE FOR BIMMERCODE". — [Scantool OBDLink CX](https://scantool.net/obdlink-cx/)
- K+DCAN cable vendors describe a switch: "K-LINE (left): … E60/E61 up to 03/2007" and "K+DCAN (right): … E60/E61 from 03/2007". An E61 owner reports that the E61 does not use ENET and needs a K+DCAN USB cable. — [avinusa K+DCAN listing](https://avinusa.com/k-dcan-obd2-inpa-interface-car-diagnostic-tool-cable-with-ft232rl-chip-and-switch-for-bmw-e60-e61-e63-e83-code-aux.html); [5post E60 forum](https://e60.5post.com/forums/showthread.php?p=31850258)
- Carista lists 31 diagnostic items and 101 customisations for the E60/E61 (CAS, KOMBI, ABS, airbags, "Basic OBD2" and others) using its own Carista EVO/OBD Reader Bluetooth adapters. It gives no year/protocol caveats and does not mention ELM327. — [Carista E60](https://carista.com/apps/supported-cars/bmw/5series/e60)

### Inferences
- Users who own an OBDLink CX/MX+ or vLinker for BimmerCode will expect it to work. On a pre-2007 E61 it will most likely not reach the body modules through EdiabasLib. The project should detect this and explain it rather than fail silently.
- vLinker BM/MC are STN/ELM-compatible devices. EdiabasLib would treat them as ELM327 (D-CAN only), and only if the vendor states Deep OBD compatibility.

### Gaps
- BimmerCode's per-vehicle page for E60/E61 (bimmercode.app/vehicles/bmw) was not retrievable in this pass. Whether BimmerCode supports pre-03/2007 E60s at all, and with which adapter, is unconfirmed.
- BimmerLink's adapter page returned 404. The app protocols are proprietary and undocumented.

## 4. ENET and ICOM

### Takeaway
ENET (Ethernet on OBD pins, HSFZ/DoIP) is F-series and later. It does not apply to E60/E61. BMW ICOM A does multiplex K-line on the OBD connector and is the dealer interface for E-series. It is expensive and normally driven by ISTA on Windows, with EDIABAS reaching it over network (remote) mode, so it is low relevance for this project.

### Cited Findings
- In the EdiabasLib matrix, BMW-FAST-ENET means "F series or higher with HSFZ or DoIP protocol". The Deep OBD ENET WiFi adapter is "for BMW F-models". "For BMW pre F-series use the Bluetooth adapter." — [AdapterTypes.md](https://uholeschak.github.io/ediabaslib/docs/AdapterTypes.html); [ENET_WiFi_Adapter.md](https://github.com/uholeschak/ediabaslib/blob/master/docs/ENET_WiFi_Adapter.md)
- BimmerCode lists ENET adapters only for F/G/I series. — [BimmerCode adapters](https://bimmercode.app/adapters)
- BMW: ICOM A handles OBD communication, and ICOM B adds MOST. Older cars with the 20-pin round connector need ICOM C. Some vehicles need OBD pins 7-8 bridged. A community pin table lists K-Line 1 and K-Line 2 among ICOM A signals. — [BMW system requirements](https://bmwtechinfo.bmwgroup.com/assets/system_requirements.pdf); [BMW ICOM training doc (archive.org)](https://ia801005.us.archive.org/11/items/BMWTechnicalTrainingDocuments/ST050%20Technical%20Systems/04c_ICOM.pdf); [ICOM Next user guide](https://bmwtechinfo.bmwgroup.com/equipment_manual/ICOM_Next_User_Guide_ENG.pdf)
- EdiabasLib contains `EdInterfaceEnet.cs` and `EdInterfaceEdic.cs`/`EdInterfaceRplus.cs`/`EdInterfaceAds.cs` (the ADS interface is Windows-hardware specific). — local `EdiabasLib/EdiabasLib/`

### Inferences
- An ICOM is out of scope as a primary target: cost, clone quality issues, and EDIABAS talking to it over a vendor network protocol. Note it as "deliberately excluded".

### Gaps
- Not confirmed from a primary source whether ICOM Next (as opposed to ICOM A) supports K-line E-series. Not confirmed whether EdiabasLib can drive an ICOM for K-line cars.

## 5. Generic OBD-II (modes 01/03) on a 2005 E61 550i (N62) as a no-SGBD fallback

### Takeaway
A generic OBD-II reader (ELM327/STN) should reach the DME's legislated emissions data (mode 01 PIDs, mode 03/07 DTCs, readiness) on the K-line. I could not confirm from a source whether the 2005 N62 DME uses ISO 9141-2 or ISO 14230-4 (KWP2000). As a fallback it needs no BMW files but gives only powertrain emissions data.

### Cited Findings
- The ELM327 supports ISO 9141-2 and ISO 14230-4 (5-baud and fast init), both on the K line. — [ELM327 datasheet](https://www.scantool.net/scantool/downloads/103/elm327dsh.pdf)
- Third-party data loggers document BMW OBD-II over ISO 9141-2 for some BMW engines (product doc title: "BMW OBDII ISO9141-2"). — [AIM Sportline BMW OBDII ISO9141-2 doc](https://aim-sportline.com/download/ecu/stock/bmw/1_BMW_OBDII-ISO9141-2+BMWPT6_eng.pdf)
- Carista lists "Basic OBD2" as a separate item from BMW-module diagnostics on E60/E61. — [Carista E60](https://carista.com/apps/supported-cars/bmw/5series/e60)

### Inferences
- A generic OBD-II mode is a cheap, legally clean fallback feature: SAE J1979 has public PIDs and no proprietary SGBDs. It can even run over the same K+DCAN cable (ISO 9141/14230 framing on pin 7). Content is limited to engine/emissions data such as coolant temperature, fuel trims, O2 sensors, MIL status and P-codes. It gives nothing on CAS, KOMBI, airbag, DSC or night vision.
- The protocol should be auto-detected (try ISO 9141-2 5-baud and then KWP fast init) rather than hard-coded.

### Gaps
- No source confirmed the exact OBD-II protocol for the 2005 N62 (ME9.2.x) DME. This needs a test on the car (for example, an ELM327 `ATSP0` then `ATDPN`) or a BMW document.

## 6. Linux SocketCAN and CAN adapters

### Takeaway
SocketCAN adapters (PCAN, CANable, slcan and similar) only matter for D-CAN cars (E-series from 03/2007, ISO-TP on pins 6/14 at 500 kbit/s). They are irrelevant for the 2005 K-line E61. Note them as future scope only.

### Cited Findings
- EdiabasLib's ELM path uses CAN 11-bit at 500 kbit/s with tester ID 0x6F1, which is the BMW D-CAN framing a SocketCAN transport would also need. — local `EdElmInterface.cs` (`ATSH6F1`, `ATPBC101` "11/500")
- K+DCAN vendor listings put E60/E61 D-CAN from 03/2007. — [avinusa listing](https://avinusa.com/k-dcan-obd2-inpa-interface-car-diagnostic-tool-cable-with-ft232rl-chip-and-switch-for-bmw-e60-e61-e63-e83-code-aux.html)

### Inferences
- If the Zig port abstracts "transport" (K-line UART with DTR/RTS line control, D-CAN ISO-TP, TCP to Deep OBD/ELM), SocketCAN becomes a small add-on for LCI E-series on Linux. It is not worth building first.

### Gaps
- No source searched specifically on SocketCAN BMW D-CAN projects. Excluded by scope.

## 7. Host options: Raspberry Pi / in-car Linux box, VM USB passthrough

### Takeaway
A Linux host (including a Pi) with a USB K+DCAN cable is technically the cleanest native setup, because Linux's `ftdi_sio` exposes the FTDI latency timer and line control. VM passthrough on Apple Silicon (UTM/QEMU) is reported as fragile for USB devices generally, and macOS FTDI latency defaults can be high. Both argue for a native engine.

### Cited Findings
- EdiabasLib README: "runs on all platforms that support .NET framework (Windows, Windows CE, Mono)". Its current csproj targets `net10.0-windows…`, `net11.0-windows…`, `net481` and Android. There is no plain `net10.0` (cross-platform) target. — local `README.md` line 7; `EdiabasLib/EdiabasLib/EdiabasLib.csproj` line 6
- The K+DCAN timing code has a separate `DtrTimeCorrFtdi` correction and an optional `ObdFtdiBitBang` mode. K-line init depends on precise DTR/RTS/break timing through the FTDI chip. — local `EdInterfaceObd.cs` lines 157-159, 352-367
- macOS FTDI latency: one report says FTDI USB-serial shows about 16 ms latency with the macOS-included driver and that changing the VCP config helps. Another vendor document says 2 ms for its own IDs. On Linux the timer is adjustable through sysfs. — [Digilent forum](https://forum.digilent.com/topic/19351-pmod-usbuart-latency-under-macos/); [BU application note](https://www.bu.edu/neuroimaging/files/2022/01/Application-Note-for-User-on-Mac-or-Linux-v2.pdf)
- FTDI VCP 1.5.0 on macOS Sequoia 15.1.1: long delays on open()/close(). — [Apple Discussions](https://discussions.apple.com/thread/255763367)
- "USB passthrough on Apple Silicon is unusable" (QEMU issue, USB Wi-Fi device). UTM USB support was called buggy by a commenter. Apple's Virtualization framework has no USB passthrough. — [QEMU issue 2178](https://gitlab.com/qemu-project/qemu/-/issues/2178); [VirtualBuddy discussion 307](https://www.github.com/insidegui/VirtualBuddy/discussions/307); [UTM issue 3778](https://github.com/utmapp/UTM/issues/3778)
- Parallels users report trouble passing USB serial devices to guests. — [Parallels forum](https://forum.parallels.com/threads/passing-through-a-serial-usb-device-to-ubuntu-it-wont-work.363600/latest)

### Inferences
- A native engine should use the FTDI D2XX library or the OS serial driver directly, and set the latency timer to 1-2 ms where possible. Interbyte timing on the K-line (10.4k for KWP fast init, then BMW-FAST at 115200) is sensitive to a 16 ms USB latency.
- A Pi with a K+DCAN cable plus an MCP/HTTP bridge to the Mac is a viable "car box" architecture. It also removes the laptop-battery and sleep problems during long sessions.

### Gaps
- No K+DCAN-specific report of UTM passthrough failures was found. The project's own experience is the best evidence.
- No source found for a Raspberry Pi + EdiabasLib/Mono K-line project.

## 8. Wine/CrossOver running EDIABAS/INPA with a serial-mapped K+DCAN cable

### Takeaway
I found no credible, tested community report of INPA/EDIABAS talking to a car through a K+DCAN cable under Wine on Linux or CrossOver on macOS. Only vendor boilerplate claims it. It is plausible on Linux (Wine maps `/dev/ttyUSB0` to COMx), but timing-sensitive DTR/RTS K-line handling through Wine's serial layer is unproven. Treat it as unsupported and experimental.

### Cited Findings
- A vendor page claims INPA works on Linux "via Wine or Dockerized tools", without evidence. — [bimmergroup coding cable](https://ie.bimmergroup.com/bmw-coding-cable)
- Wine needs native Linux drivers for the hardware. INPA setups commonly hit COM port numbering errors (IFH-0018 init error). — [peak-system forum](https://forum.peak-system.com/viewtopic.php?p=5532); [e46zone IFH-0018](https://www.e46zone.com/forum/blogs/entry/226-inpa-ifh-0018-initialization-error-solution)
- A comparable diagnostic suite (VCDS) has a community Wine packaging project for Linux, which shows the general approach is attempted for OBD tools. — [VCDS for Linux](https://github.com/cryinkfly/VCDS-VAG-COM-Diagnosis-system-for-Linux)

### Inferences
- EDIABAS's OBD interface uses `obd32.dll`/serial with the FTDI-specific DTR timing that EdiabasLib corrects for. Wine's serial emulation adds latency and may not honour these timings. On macOS, CrossOver plus an FTDI VCP adds the Sequoia FTDI issues above. A Windows VM remains the practical Windows-tool route, and a native EdiabasLib/Zig engine is the cleaner long-term route.

### Gaps
- No first-hand Wine/CrossOver + K+DCAN success or failure report was found on bimmerfest/e90post/bimmerforums in this pass.

## 9. Safety and community knowledge for diagnosis on a K-line E60/E61

### Takeaway
The main risks are low battery voltage during long sessions and any write operation (coding, flashing, clearing adaptations), plus wrong cable mode or pin wiring. Read-only diagnosis is low risk, but ignition-on time drains the battery and can leave modules awake. Keep a charger (about 13 V or more) on for anything long, and never write anything to a module over a flaky link.

### Cited Findings
- BMW programming guidance (quoted on 5series.net): an approved battery charger must be attached and switched on during programming. Forum users keep about 13 V or more, cite 12.6 V as a minimum, and say it should never drop below 12 V. — [5series.net coding thread](https://5series.net/forums/diy-do-yourself-14/coding-118300); [bimmerforums archive](https://www.bimmerforums.com/forum/archive/index.php/t-1478492.html); [BMW E60 CIP programming (slideshare)](https://www.slideshare.net/slideshow/bmw-e60-cipprogramming-software-update-progcoding/54632532)
- For cars built before 03/2007, pins 7 and 8 should be bridged "for the double K-line to work", otherwise some modules are not reachable for coding. (Conflicts with EdiabasLib, which lists the 7+8 bridge as needed for DS2 cars such as E39/E46 and does not list the E60.) — [5series.net NCSExpert thread](https://5series.net/forums/diy-do-yourself-14/ncsexpert-code-mods-108089/page76); [AdapterTypes.md](https://uholeschak.github.io/ediabaslib/docs/AdapterTypes.html)
- One poster was told a K-line cable "won't work safely" on a 2006 E60 and could brick a unit. This is anecdotal and unconfirmed. Another thread asking whether anyone had bricked an ECU with NCS Expert got "no" answers. Recovery for bad coding is to re-run SG_CODIEREN with factory data, and low battery causes odd symptoms. — [5series.net 2006 530i incorrect modules](https://5series.net/forums/e60-discussion-2/2006-bmw-530i-incorrect-modules-programmed-154162/page2); [5series.net car coding](https://5series.net/forums/diy-do-yourself-14/car-coding-106042/page69)
- Battery registration on E60 is a coding/service step (affects charging strategy). — [5series.net battery registration](https://5series.net/forums/diy-do-yourself-14/coding-registering-new-battery-114909)
- EdiabasLib warns that fake FT232R chips cause communication problems. — [AdapterTypes.md](https://uholeschak.github.io/ediabaslib/docs/AdapterTypes.html)

### Inferences
- For a read-only engine the practical rules are: (1) check battery voltage (pin 16) and ignition (DSR/pin 15 sensing on K+DCAN) before and during sessions; (2) bound ignition-on time; (3) refuse write-class jobs (coding, flash, `STEUERN_*`, clearing adaptations) by default; (4) warn on cable switch position or K-line mode mismatch; (5) detect and warn about fake FTDI chips; (6) never issue jobs that put modules into programming or diagnostic-session modes on a shaky transport such as Bluetooth or a VM-passed USB link.
- Brick reports for E60 tend to involve flashing (WinKFP) with low voltage or interrupted transfers, not read diagnostics. I did not find a specific list of commonly bricked E60 modules from a primary source.

### Gaps
- No authoritative list of commonly bricked E60/E61 modules was found.
- No BMW primary source with exact voltage thresholds for diagnosis-only (as opposed to programming) sessions was found.
- I did not verify whether the E60 actually needs the pin 7+8 bridge (sources conflict). It should be tested on the car or checked in the BMW wiring docs.
