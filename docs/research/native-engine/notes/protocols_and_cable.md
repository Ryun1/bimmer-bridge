# E60/E61 diagnostic protocols and K+DCAN cable: what goes over the wire

Scope: a 2005 E61 550i (N62, DME = Bosch ME9.2, SGBD `ME9N62_2.PRG`) on a K+DCAN FTDI cable, with EDIABAS `Interface = STD:OBD`. The goal is a Zig port of EdiabasLib's `EdInterfaceObd`. F-series/ENET (HSFZ/DoIP) is out of scope.

Primary source used throughout: the EdiabasLib source, cloned at commit `5e9efc5` (2026-11-22). Line numbers below refer to that commit. Abbreviations used in the citations:
- [OBD] = https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/EdiabasLib/EdInterfaceObd.cs
- [BASE] = https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/EdiabasLib/EdInterfaceBase.cs
- [FW] = https://github.com/uholeschak/ediabaslib/blob/master/EdiabasLib/CanAdapter/CanAdapter/CanAdapter.c. This is Holeschak's replacement firmware for the ATmega inside standard K+DCAN cables, so it is a functional re-implementation of the cable side.
- [ADAPT] = https://github.com/uholeschak/ediabaslib/blob/master/docs/AdapterTypes.md
- [BUILD] = https://github.com/uholeschak/ediabaslib/blob/master/docs/Build_Bluetooth_D-CAN_adapter.md
- [SIM] = https://github.com/uholeschak/ediabaslib/blob/master/docs/EdiabasLib.simulation_file.md
- [ELM] = https://github.com/uholeschak/ediabaslib/blob/master/docs/Replacement_firmware_for_ELM327.md
- [TIS-BUS] = BMW TIS SBT 61 02 03 (015), "Bus diagnosis All models from E60" (issue 08/2007): https://5series.net/forums/attachments/diy-do-yourself-14/44001d1202390369-e60-technical-information-topic-bus_diagnosis_all_models_from_e60.pdf
- [DISC] = this project's own VM discovery dump, `discovery/20261121-164506-discovery.txt` (local file, from Ryan's VM)

## Q1. E60/E61 diagnostic topology for a 2005 car, K-line vs D-CAN, and the cable switch position

### Takeaway
A 2005 E60/E61 is diagnosed over a single "diagnosis wire" (K-line / D-bus) on OBD pin 7, not over D-CAN. The gateway is the SGM (up to 09/2005) or the KGM body-gateway module (from 09/2005), so which one this car has depends on its exact build date. The E60 is not one of the models that needs OBD pins 7+8 bridged, and EdiabasLib reaches it with the BMW-FAST/KWP protocols at the K-line setting. D-CAN (pins 6/14) is for BMW cars from 03/2007 onwards.

### Cited Findings
- BMW TIS for all models from E60: "Most buses are CAN buses… the PT-CAN has a fast data transmission rate, the K-CAN a slower data transmission rate. A fibre-optic cable is used for navigation and entertainment: the MOST bus… There is a separate data wire for diagnosis: the diagnosis wire, also known as the 'K-wire'." — [TIS-BUS]
- The DIS quick-test virtual unit is called "CAN/byteflight system analysis" on "E60, E61, E63, E64 up to 09/2005" and "CAN system analysis" on "E60, E61, E63, E64 from 09/2005". So byteflight (the SI-bus safety network) is part of the pre-09/2005 architecture. — [TIS-BUS]
- PT-CAN terminating resistor: "1 resistor is in the SGM control unit (safety and gateway module). From 09/2005, this resistor is in the KGM control unit (body-gateway module)." — [TIS-BUS]
- For MOST analysis, DIS first checks communication with "MPM: micro-power module" on E60/E61/E63/E64 up to 09/2005 and with "KGM: body gateway module" from 09/2005. — [TIS-BUS]
- A workshop page says the KGM superseded the SGM on E60/E61/E63/E64 from 09/2005, and that the SGM was the gateway between PT-CAN, K-CAN, byteflight and the diagnosis wire. — [workshop-manuals.com search summary](https://workshop-manuals.com/bmw/5_series_e60/m5_s85_sal/6_si_techniques/61__general_electrical_system/11_sbt__body-gateway_module_e60_e61_e63_e64/page_4252/). The fetched page itself covered only the KGM mirror and bistable-relay functions: "The bistable relay and micro-power module have been integrated in the body-gateway module."
- BMW TIS mentions "modifications in the bus structures on the E60, E61, E63, E64 from 03/2007", but the snippet I found cuts off before the details. — [workshop-manuals.com bus structures](https://workshop-manuals.com/bmw/5_series_e39/535i_m62_sal/7_si_techniques/61__general_electrical_system/3_sbt__bus_structures_all_models_from_e38_and_mini/page_4386/)
- E65/E66 training material gives the "D-Bus 115Kbps linear" (and K-CAN-S 100 Kbps). The E6x generation runs its diagnosis bus at about 115 kbit/s, which matches the 115200 baud of BMW-FAST. — [BMW ST042 E65 Bus Systems](https://ia801005.us.archive.org/11/items/BMWTechnicalTrainingDocuments/ST042%20E65%20Complete%20Vehicle%20%28Part%201%29/03_E65%20Bus%20Systems.pdf) (from a search snippet; the PDF was not fetched)
- EdiabasLib's vehicle classes:
  - "BMW-DS2: BMW models (with DS2 protocol): E36, E38, E39, E46, E52, E53, E83, E85 and E86. An OBD II Pin 7+8 connection in the adapter is required!"
  - "BMW-FAST: BMW E series with BMW-FAST protocol (DS2 successor protocol)."
  - The ELM327 path supports "Only D-CAN… (BMW vehicles starting from 3/2007)."
  
  The E60 is not in the pin-7+8 list. — [ADAPT]; repeated in [ELM]: "Vehicles E36, E38, E39, E46, E52, E53, E83, E85 and E86 additionally require a connection between OBD pin 7 and 8"
- Cable vendors describe the switch as "K-Line position: switch left; most model years 1998 to 2007. DCAN position: switch right; most model years 03/2007 to 2016", with the switch bridging pins 7 and 8 in one position. Date ranges vary between vendors. These are vendor and blog claims, not BMW documentation. — [obdexpress blog](https://blog.obdexpress.co.uk/2017/04/13/switch-mode-kdcan-cable-reviews-of-dcan-usb-interface/), [obd2express](https://obd2express.blogspot.com/2017/04/how-to-switch-kdcan-cable-7-pin-for-k.html)
- Ryan's VM has `obd.ini` with `[OBD] Port=Com1 Hardware=USB`, EDIABAS `Interface = STD:OBD`, and the SGBDs `ME9N62_2.PRG`, `N62_TUE.PRG`, `NVE_65.PRG` and `D_MOTOR.GRP`. — [DISC]

### Inferences
- For a 2005 E61, put the switch in the "K-line" (left) position. That position bridges pins 7 and 8. On an E60, pin 8 is not needed but bridging it should be harmless, and most K+DCAN users run E60s in that position. This is not verified for every pin-8 use on E60, which is a gap.
- Build date decides the gateway. A car built before 09/2005 has the SGM plus byteflight. A car built from 09/2005 has the KGM. Either way the tester only talks to the K-line and the gateway routes from there, so the interface layer does not need to know which gateway is fitted.
- The D-bus runs at about 115 kbit/s and BMW-FAST at 115200 8N1. On a pre-2007 E60, "BMW-FAST over K-line" is therefore the dominant concept. D-CAN concept 0x0110 should not occur on this car.

### Gaps
- I did not obtain the full TIS "bus structures from 03/2007" text, so the exact E60 pin-6/14 D-CAN introduction is unconfirmed. Vendors say 03/2007.
- I found no primary source on whether pin 8 is used on the E60, or on whether bridging 7+8 is harmful on it.
- I did not fetch an E60-specific topology diagram (ST055/ST402) listing which ECU sits on K-CAN, PT-CAN, MOST or byteflight. A candidate is BMW ST402 "08 E6x Body Electronics": https://ia801005.us.archive.org/11/items/BMWTechnicalTrainingDocuments/ST402%20Body%20Electronics%20III/08%20E6x%20Body%20Electronics.pdf

## Q2. Protocols and EDIABAS "concepts": baud, parity, framing, timing, init and keep-alive

### Takeaway
EdiabasLib selects the protocol from `CommParameter[0]`, the concept word the SGBD sets in `INITIALISIERUNG`/`SETZEN_PARAMETER`. Each concept fixes the parity and checksum algorithm, the header parsing, whether DTR is held, and the init sequence. The baud rate and timeouts come from the SGBD's parameter array, in milliseconds. A port has to reproduce this parameter-array parsing exactly, because the SGBD bytecode provides these arrays.

### Cited Findings (all from [OBD] unless noted; the concept switch is at about lines 480-850)
- **Concept dispatch** on `concept = CommParameterProtected[0]`:
  - 0x0000 Raw (EDIC)
  - 0x0001 Concept 1
  - 0x0002 Concept 2 ISO 9141 (KWP1281)
  - 0x0003 Concept 3
  - 0x0005 DS1
  - 0x0006 DS2
  - 0x010B KWP2000 Standard (motorbikes)
  - 0x010C KWP2000 BMW
  - 0x010D KWP2000*
  - 0x010F BMW-FAST
  - 0x0110 D-CAN
  - Anything else gives "*** Concept not implemented", error IFH_0014. — [OBD] L497-850
- **Concept 1:** even parity. If the adapter echoes, EdiabasLib refuses with IFH_0006 and the comment "only with ADS adapter". So concept 1 does not work on a K+DCAN cable. — [OBD] L561-595
- **DS1 / DS2 (0x0005/0x0006):**
  - Parameters: `baud = par[1]`, `parity = Even`, `TimeoutStd = par[5]`, `RegenTime = par[6]`, `TimeoutTelEnd = par[7]`, `InterbyteTime = par[8]` if present. `par[9]` is a parameter checksum.
  - The answer-length spec is `CommAnswerLen = {-1, 0}`: the length byte sits at offset 1 and counts the whole telegram.
  - DTR: only DS2 uses send-time DTR pulsing (`if (concept == 6) ParSendSetDtr = !HasAdapterEcho`).
  - Send path: `TransDs2` appends `CalcChecksumXor` over all bytes, waits for the RegenTime since the last response, sends with optional interbyte delay, strips and compares the echo, then reads the header and the length-derived tail, and checks the XOR.
  - No init or wake-up. `FrequentDs2` resends a stored request for "frequent" mode. — [OBD] L672-708, L4867-4980
- **DS2 byte example** (from [SIM], marked as DS2):
  - Request `00 04 00`: address 0x00, length 4 including checksum, command 0x00 = ident. The interface appends the checksum.
  - Response `00 07 A0 00 05 95 37`.
  - The XOR of the first six bytes is 0x37, which confirms an XOR checksum. The doc text wrongly says "sum over all bytes" for DS2 and "XOR" for BMW-FAST; the code and the arithmetic show the opposite. — [SIM]
- **A secondary reference confirms DS2** as 9600 baud, 8 data bits, even parity, 1 stop bit; frame ADDR, LEN (total length including ADDR, LEN and CS), CMD, DATA, CS (XOR of all preceding bytes); no init ("Direct"). — [ediabasx-docs obd32 protocols](https://emdzej.github.io/ediabasx-docs/reference/interfaces/obd32-protocols.html) (independent reverse-engineering docs, secondary)
- **KWP2000 BMW (0x010C)**, the ISO 14230 framing used on BMW K-line:
  - Parameters: `baud = par[1]`, `parity = None`, `TimeoutStd = par[2]`, `RegenTime = par[3]`, `TimeoutTelEnd = par[4]`, `InterbyteTime = par[5]`, `RetryNr78 = par[6]`, `TimeoutNr78 = par[7]`, `TesterPresentTime = par[8]`, `TesterPresentTelLen = par[9]` (≤ 11), tester-present telegram `par[10..20]`, `StartCommTelLen = par[21]`, start-comm telegram `par[22..]`, parameter checksum `par[33]`.
  - Init: if a StartComm telegram exists, `SendWakeFastInit`, then the StartComm telegram (typically 0x81 StartCommunication) as an ordinary KWP telegram.
  - Keep-alive: `IdleKwp2000Bmw` sends the SGBD-supplied tester-present telegram when `TesterPresentTime` has elapsed since the last communication. — [OBD] L711-772, L4685-4760
- **Fast init:** the interface holds BREAK (K-line low) for 25 ms, then releases it until 50 ms in total. DTR is optionally pulsed during this when `setDtr` is set. With FTDI ext functions it is `InterfaceSendPulse(0x02, 2, 25, …)`. — [OBD] L3506-3560
  - Secondary docs agree: fast init is about 25 ms low and 25 ms high, and the start request is `0xC1 0x33 0xF1` (functional OBD start-comm). — [ediabasx-docs](https://emdzej.github.io/ediabasx-docs/reference/interfaces/obd32-protocols.html)
- **5-baud init** exists as `SendWakeAddress5Baud(value, setDtr, bothLines, autoKeyByteDelay)` and is used by concepts 2/3 and ISO 9141 (`ParWakeAddress = par[2]`). Those concepts run at 8N1 with send-DTR, and bit-bang is allowed when `EnableFtdiBitBang` is set. — [OBD] L596-670, L3562ff
  - Secondary docs: the ISO 9141-2 key bytes are `0x55, 0x08, 0x08`, then the ECU sends the inverted KB2. — [ediabasx-docs](https://emdzej.github.io/ediabasx-docs/reference/interfaces/obd32-protocols.html)
- **KWP2000\* (0x010D):**
  - Parameters: `baud = par[1]`, `parity = Even`, `TimeoutStd = par[2]`, `RegenTime = par[3]`, `TimeoutTelEnd = par[4]`, `InterbyteTime = par[5]`, `RetryNr78 = par[6]`, `TimeoutNr78 = par[7]`, parameter checksum `par[21]`.
  - Framing (`TelLengthKwp2000S`): length = `buf[3] + 4`. So the header is always 4 bytes (Fmt, Target, Source, Len), and the length byte is mandatory.
  - The checksum is XOR (`CalcChecksumXor`), unlike BMW-FAST, which uses an additive sum.
  - The receive loop handles 7F xx 78 response-pending.
  - No init and no tester present. — [OBD] L774-800, L4762-4865
- **BMW-FAST (0x010F):**
  - Parameters: `baud = par[1]` (115200 in practice), `parity = None`, `TimeoutStd = par[2]`, `RegenTime = par[3]`, `TimeoutTelEnd = par[4]`, `RetryNr78 = par[5]`, `TimeoutNr78 = par[6]`, parameter checksum `par[7]`.
  - DTR is held at `stateDtr = HasAdapterEcho` (true for a K+DCAN cable) and send-DTR pulsing is off.
  - It uses `TransBmwFast`, which is the same as `TransKwp2000`. No init and no tester present at this layer. — [OBD] L801-826, L3768
- **D-CAN (0x0110):** fixed 115200 8N1 to the adapter. `TimeoutStd = par[7]`, `TimeoutTelEnd = 10`, `RegenTime = par[8]`, `TimeoutNr78 = par[9]`, `RetryNr78 = par[10]`, parameter array length at least 30. It also uses `TransBmwFast`: the cable firmware turns the BMW-FAST frame into ISO-TP on CAN. — [OBD] L828-845
- **Timeouts:** `ParTimeoutStd = 0` falls back to `DefaultStdTimeout = 60000` ms. The echo timeout is fixed at `EchoTimeout = 100` ms. `AddRecTimeout` is added to every receive. — [OBD] L163-164, L855-858, L3388-3390
- **Receive semantics** (port these exactly):
  - Wait up to `timeout` for the first byte.
  - Then keep reading until `length` bytes have arrived. The inter-byte gap timer (`timeoutTelEnd`) restarts whenever more bytes arrive, and the read fails if the gap is exceeded before `length` bytes. — [OBD] L3381-3470
- **Interbyte send:** when `InterbyteTime > 0`, each byte is sent on its own and the sender busy-waits `interbyteTime + byteTime`. byteTime = `1000/baud × (dataBits + 2, or + 3 with parity)` ms. The input buffer is purged before sending. — [OBD] L3241-3313
- **Send with DTR** (the `setDtr` path, used for ADS-style interfaces without echo): raise DTR, write, busy-wait `DtrTimeCorrCom (0.3 ms) + byteTime × length`, then drop DTR. Without DTR, the sender sleeps byteTime × length after the write. — [OBD] L3389-3420; config keys `ObdDtrTimeCorrCom` / `ObdDtrTimeCorrFtdi`, L346-355

### Inferences
- In EdiabasLib terms, P2 is `TimeoutStd`, P1 (the inter-byte gap for ECU responses) is `TimeoutTelEnd`, P3 (the minimum gap before the next request) is `RegenTime`, and P4 (tester inter-byte) is `InterbyteTime`. This mapping is explicit in the EDIC code path: `ParTimeoutStd = ParEdicP2; ParTimeoutTelEnd = ParEdicP1; ParInterbyteTime = ParEdicP4; ParRegenTime = ParEdicP3` — [OBD] L1740-1743. Values come from the SGBD, not from ISO defaults.
- Which concept a given E60 module uses: DME ME9N62 (ME9.2), CAS, KOMBI, DSC and NVE on a 2005 E60 most likely use BMW-FAST (0x010F) over K-line. This is an inference from [ADAPT] ("BMW E series with BMW-FAST") and the 115 kbit/s D-bus, and it is unverified per ECU. A third-party ECU list shows "EHC E65/E60 BMW-FAST" ([obdtester search snippet](https://www.obdtester.com/bimcom-eculist/bmw/6/2011_e63_e64)). Some older carry-over modules may still use KWP2000* or DS2 at 9600/10400 baud. Concept is set per SGBD, so the port must support all of them.

### Gaps
- I did not extract each E60 SGBD's actual CONCEPT and parameter arrays. They are inside the .PRG files in the VM. A cheap way to verify: run any job with `IfhTrace=3` (or EdiabasLib logging) and read the "CommParameter" line.
- I found no primary BMW document for standard KWP P1-P4 numeric defaults. ISO 14230-2 is paywalled.

## Q3. BMW-FAST telegram format and echo handling on a K+DCAN cable

### Takeaway
A BMW-FAST frame is `Fmt Tgt Src [Len] Data… CS`. Fmt is `0x80 | len` for 1-63 data bytes, or `0x80` with a following length byte (or 0x00 plus a 2-byte length) for longer payloads. CS is the 8-bit additive sum of all preceding bytes. On a K+DCAN cable every transmitted byte comes back as an echo first. The host reads and verifies exactly `sendLength` echo bytes within 100 ms, and only then parses the ECU response. 7F xx 78 response-pending frames extend the wait to `TimeoutNr78`.

### Cited Findings
- **Length:**
  ```
  TelLengthBmwFast: telLength = buf[0] & 0x3F;
    if 0: (buf[3]==0 ? (buf[4]<<8)+buf[5]+6 : buf[3]+4)
    else telLength += 3
  ```
  The result excludes the checksum. — [BASE] L881-905
- **Checksum:** `CalcChecksumBmwFast` = `sum += data[i]` (byte wraparound). — [BASE] L933-941
  - Worked check from [SIM]: response `83 F1 01 59 02 7F 4F`. 0x83+0xF1+0x01+0x59+0x02+0x7F = 0x24F, so the checksum is 0x4F. This matches the additive sum, not XOR. The doc's request example `83 01 F1 19 02 0C` does not check out under either algorithm, so treat it as illustrative only.
- **Receive** (`TransKwp2000`, L4110-4265):
  - Read a 4-byte header and require `(buf[0] & 0xC0) == 0x80` (physical addressing bits), otherwise "Invalid header".
  - If `(buf[0] & 0x3F) == 0 && buf[3] == 0`, read 2 more bytes for the 16-bit length.
  - Read the rest plus CS within `TimeoutTelEnd`, then verify the sum.
  - If the data is `7F sid 78`, note the ECU address (`buf[2]`) in `Nr78Dict` and keep reading with `TimeoutNr78`.
  - NR 0x21/0x23 trigger a delayed resend when configured.
  - Response-pending tracking is per source address, so a functional request that several ECUs answer is handled.
- **Echo handling:** after `SendData`, `if (HasAdapterEcho)` read `sendLength` bytes with `EchoTimeout` (100 ms). On "*** No echo received" or "*** Echo incorrect", drain and return IFH_0003. `HasAdapterEcho` defaults to `true` for the OBD/FTDI path (`AdapterEcho => true`). — [OBD] L4137-4160, L2769-2788
- **Header bits:** the format byte's bits 7-6 are `00` no address, `01` CARB exception, `10` physical, `11` functional. Bits 5-0 hold the length, and 0 means a length byte follows. Addresses: 0x33 is the OBD ECU (functional), 0xF1 the tester. — [ediabasx-docs](https://emdzej.github.io/ediabasx-docs/reference/interfaces/obd32-protocols.html) (secondary)
- **BMW-FAST byte example** (8-bit length, physical): request `83 01 F1 19 02 0C`, which is ReadDTCByStatus (0x19 0x02, a UDS-style SID); response `83 F1 01 59 02 7F 4F`. Request `83 01 F1 22 20 00` (ReadDataByIdentifier 0x2000); response `93 F1 01 62 20 00 …` (19 data bytes, Fmt 0x93). — [SIM]
- **Cable-side handling in [FW]:**
  - The firmware receives a frame over the UART at 115200 bps on a 7.3728 MHz AVR. A frame ends when the length computed from the header is reached. Its UART length rule differs: `tel_len = buf[0]&0x3F ? +4 : buf[3]+5`, including the checksum. The firmware validates the additive checksum.
  - Single frame: CAN ID `0x600 | source` (0x6F1 for the tester), data `[target, 0x0L, payload…]`. This is BMW's extended-addressing ISO-TP.
  - First frame: `[target, 0x10|lenHi, lenLo, 5 bytes]`, then wait for flow control and honour its block size and STmin.
  - Responses from CAN ID `0x600 | ecu` are reassembled into `Fmt Tgt Src … CS` frames and sent back on the UART.
  - CAN defaults: 500 kbit/s (mode 1), 100 kbit/s (mode 9) or off (mode 0); block size 0; STmin 0.
  - The firmware sniffs CAN ID 0x130 (terminal status) for ignition. — [FW] L74-78, L380-400, L590-720, L754-900, L1045-1090
- **Adapter-internal telegrams** (all checksummed BMW-FAST-shaped, sent to "address" F1→F1):
  - `81 00 00 <cfg> cs` sets the CAN mode and replies with `~can_mode`.
  - `82 F1 F1 00|01|02|03 <val> cs` writes block size, separation time, CAN mode or PLD mode. Setting bit 7 of the selector reads instead.
  - `82 F1 F1 FD FD cs` returns the adapter type and version.
  - `82 F1 F1 FE FE cs` returns the ignition state (bit 0).
  - `82 F1 F1 FC FC` returns Vbat.
  - `82 F1 F1 FF FF` resets the adapter.
  
  — [FW] L440-560. EdiabasLib sends `82 F1 F1 FA FA` to read ignition through the adapter — [OBD] L1171. [BUILD]: "`82 F1 F1 FE FE 60`… response is (additionally to the echo): `82 F1 F1 FE <state> <checksum>`."
- **Adapter status frames:** `xx F1 F1 7F <status>`, where 00 = connected, 01 = disconnected, 02 = receive complete and 03 = CAN error (IFH_0011). — [OBD] L4452-4485
- **Ignition through DSR:** when the adapter cannot report ignition by telegram, EdiabasLib reports it from the DSR modem line (`GetDsrState() ? IgnitionVoltageValue : 0`). In [FW], DSR_OUT is driven by the ATmega and is "inverted by FTDI". — [OBD] L1150-1153; [FW] L62-71

### Inferences
- On the standard K+DCAN cable the PLD routes FTDI TX to both the K-line transmitter and the ATmega, with `PLD_MODE 0x00` = "all enabled". One BMW-FAST frame therefore goes out on K-line, and on CAN when CAN is enabled. The K-line transceiver produces the echo. On a pre-2007 E60 only the K-line answers. This reading comes from the [FW] PLD control defines; the original vendor firmware is closed, so it is not confirmed.
- **Echo strategy for the port:** after each write, read exactly N echo bytes (100 ms, plus the gap timer), compare them, then parse. On a mismatch, drain the input and fail with an IFH_0003 equivalent. Purge the RX buffer before every send.

### Gaps
- I found no source on whether the original (non-Holeschak) K+DCAN firmware also produces an echo in D-CAN mode. EdiabasLib's D-CAN concept assumes `HasAdapterEcho` and strips it, which suggests it does.
- I did not find the 16-bit-length (0x80 tgt src 00 HI LO) usage on K-line E60 modules documented. It probably only occurs on D-CAN or for long flash data.

## Q4. K+DCAN cable hardware, DTR/RTS, clones and identification

### Takeaway
A standard "INPA compatible" K+DCAN cable contains:
- an FTDI FT232RL (USB-UART);
- an ATF16V8B PLD that steers TX/RX between K-line, L-line and the MCU;
- an AVR (ATmega, 7.3728 MHz) with a CAN controller (MCP2515 or similar per the can-lib) for D-CAN;
- K-line transceiver(s);
- a switch that bridges OBD pins 7 and 8.

EdiabasLib drives it through an ordinary serial port. On that cable it holds DTR high for BMW-FAST and D-CAN, keeps DTR low for DS2/KWP, and never pulses DTR during sends, because the cable echoes. Pulsing DTR per send is for no-echo ADS interfaces.

### Cited Findings
- Adapter type: "FTDI USB: Standard FTDI based USB 'INPA compatible' D-CAN/K-Line adapters. Do not use adapters with fake FT232R chip, there are communication problems!" — [ADAPT]
- Hardware: "A standard D-CAN/K-Line Adapter. There are multiple manufactures who sell such adapters which are basically identical. I have used one with a PLCC AFT16V8B… The ATF16V8B requires 50mA in standby… replace this chip with a ATF16V8BQL type, which only requires 5mA." — [BUILD]
- MCU fuses (Low 0xFD, High 0xDC, Ext 0xF9) and AVR ISP programming, so the CAN MCU is an ATmega. — [BUILD]
- [FW] PLD control lines:
  - PA0/PA1 disable OBD RX-K and RX-L into FTDI RX.
  - PA2/PA3 disable FTDI TX into OBD TX-L and TX-K.
  - PA4 disables OBD RX into FTDI RX.
  - PA5/PA6 disable MCU TX1 into OBD TX-L and TX-K.
  - IGNITION on PB4, LEDs on PE0/PE1, DSR_OUT on PE2, CAN reset on PD4.
  
  So the cable has both a K-line and an L-line driver. — [FW] L50-72
- Known hardware defects:
  - "Some D-CAN cables have a design mistake and the 100nF capacitor is missing at the 3V3OUT… ca. 400mV of ripple."
  - "OBD pin 1 go to R6 1K and then the Atmega PB4, in parallel with this is R5 47K to ground… reduces the 12V to 11.75V, the absolute maximum rating is 5.5V for the Atmega." (Pin 1 is the ignition sense line on BMW.)
  
  — [BUILD]
- DTR and RTS per concept in [OBD]:
  - `stateDtr` defaults to false and is set to `HasAdapterEcho` only for BMW-FAST (0x010F) and D-CAN (0x0110).
  - `stateRts` is always false.
  - Both are applied at config time (`SerialPort.DtrEnable/RtsEnable` or `InterfaceSetDtr/Rts`).
  - `ParSendSetDtr = !HasAdapterEcho` for DS2, KWP2000 BMW, KWP2000*, BMW-FAST and D-CAN.
  - Concepts 2/3 and EDIC raw always set `ParSendSetDtr = true`.
  
  — [OBD] L477-480, L555, L627, L665, L704, L768, L797, L817, L824, L836, L843, L886-955
- When the port is closed EdiabasLib sets `DtrEnable = false; RtsEnable = false`. — [OBD] L1586-1587
- EdiabasLib accesses FTDI on Android through D2XX: `FTDIx` (by index) or `FTDI:SER=<serial>`. "For the PC platform use the COM port to access the adapter." — https://github.com/uholeschak/ediabaslib/blob/master/docs/EdiabasLib.config_file.md
- Ryan's EDIABAS `obd.ini`: `[OBD] Port=Com1 Hardware=USB`. — [DISC]
- One anecdote: an E87 user on a cable without the 7/8 bridge got "Setting Mode '500 kbit/s D-CAN & PT-CAN' … Error: Echo K-line" in K+DCAN Utility 2.0. — [search summary of obdexpress/blog posts](https://blog.obdexpress.co.uk/2017/04/13/switch-mode-kdcan-cable-reviews-of-dcan-usb-interface/) (anecdotal)

### Inferences
- Holding DTR high on the K+DCAN cable in BMW-FAST/D-CAN mode is probably a mode or handshake signal to the PLD/MCU. The mapping is not documented, so copy EdiabasLib's behaviour exactly rather than reason from first principles.
- An FT232R's default USB latency timer is 16 ms. On macOS (`AppleUSBFTDI`, `/dev/cu.usbserial-*`) and Linux (`ftdi_sio`, `/sys/bus/usb-serial/devices/ttyUSBx/latency_timer`), it should be set to 1 ms, or the 100 ms echo and P1 gap timing gets tight. This comes from general FTDI knowledge plus the project's own design spec; no source fetched here.
- Bit-bang (`EnableFtdiBitBang`) is only used for 5-baud and fast init on DS2/KWP. With a plain serial port, fast init uses BREAK (TIOCSBRK/TIOCCBRK, or `tcsendbreak` is too coarse), so the Zig port needs precise BREAK timing of 25 ms ± 1 ms.

### Gaps
- I found no authoritative description of the original K+DCAN firmware, its DTR semantics, or how the switch is wired internally beyond the pin 7/8 bridge.
- FTDI EEPROM identification: no source found on what Product, Manufacturer or serial strings genuine and clone K+DCAN cables carry, or on detecting fake FT232R chips. FTDI's own documentation (FT_PROG, AN_124) would be next, as would `ioreg -p IOUSB -l` and `lsusb -v` on the real cable. The VID:PID is expected to be 0403:6001 (FT232R default); unverified for Ryan's cable.

## Q5. Functional / broadcast addressing and "whole car" reads

### Takeaway
BMW-FAST supports functional addressing: format bits 11, with targets such as 0xDF (all ECUs) and 0xEF. A functional request can draw responses from many ECUs. EdiabasLib keeps reading per-ECU frames and tracks response-pending per source address. "Whole car" fault reads in tools such as INPA/ISTA are mostly sequential physical requests per ECU, driven by group files (`D_xxxx.GRP`, for example `D_MOTOR.GRP`).

### Cited Findings
- Format byte bits 7-6 = `11` means functional. 0x33 is the OBD functional ECU address and the start request is `C1 33 F1`. — [ediabasx-docs](https://emdzej.github.io/ediabasx-docs/reference/interfaces/obd32-protocols.html)
- Simulation docs: "With enabled `Simulation` property the SGDB will read only the first response for BMW-FAST telegrams with functional addressing. The extended simulation file syntax allows multiple responses for one request in BMW-FAST mode. With this feature, ECU searching could be simulated." — [SIM]
- The response-pending tracking `Nr78Dict` is keyed by the responding ECU address (`Nr78DictAdd(receiveData[2])`), so the loop continues until every pending ECU has answered. — [OBD] L4246-4258, L3719
- Deep OBD pages support `sgbd_functional` for a "Global Error Reset" button. That is a functional clear, and so a write. — https://github.com/uholeschak/ediabaslib/blob/master/docs/Page_specification.md
- Ryan's VM has group files such as `D_MOTOR.GRP`; there are 353 .GRP files in total. — [DISC]

### Inferences
- The receive loop does not check `(buf[0] & 0xC0) == 0x80` strictly for functional responses; responses are physical (0x8x), and only the request uses 0xC0. The `0xDF` and `0xEF` functional target values come from general BMW knowledge and were not confirmed in a fetched source.
- An interface-level gate should treat any request with format bits `11` (0xC0-0xFF) plus a write or clear SID as more dangerous than the same request sent physically, because it hits every ECU.

### Gaps
- I found no primary source listing E60 functional addresses (0xDF/0xEF) or the exact IDENT_FUNKTIONAL telegram.

## Q6. Diagnostic safety: dangerous SIDs, and risky reads

### Takeaway
Gate on the service ID, the first data byte after the header. Block these KWP2000/BMW-FAST SIDs:
- 0x10 StartDiagnosticSession, for any session other than default. Programming or flash sessions are the risk.
- 0x11 ECUReset
- 0x14 ClearDiagnosticInformation
- 0x27 SecurityAccess
- 0x2E / 0x3B Write data
- 0x2F / 0x30 IO control
- 0x31-0x33 routines
- 0x34-0x37 download and upload / transfer
- 0x3D WriteMemoryByAddress
- 0x85 ControlDTCSetting
- 0x28 / 0x29 communication control (UDS-style)

Allow 0x17, 0x18, 0x19, 0x1A, 0x21, 0x22, 0x23 (read memory: allow with caution), 0x3E, and the 0x81/0x82 start/stop communication.

For DS2, commands are not standardised; gate them by SGBD job name rather than by byte. Reads carry their own risks: they keep the bus awake and run the battery down. On a KGM car the bistable relay cuts loads if quiescent current stays high.

### Cited Findings
- KWP2000 service names: 0x14 ClearDiagnosticInformation, 0x18 ReadDiagnosticTroubleCodesByStatus, 0x1A ReadECUIdentification, 0x21 ReadDataByLocalIdentifier, 0x31 StartRoutineByLocalIdentifier, 0x34 RequestDownload, 0x3B WriteDataByLocalIdentifier. These came from a search summary that explicitly flagged them as unverified model knowledge; the full table is in paywalled ISO 14230-3. — [ISO 14230-3 listing](https://webstore.ansi.org/standards/iso/iso142301999-1020600)
- "Bit 6 of all request Service Identifiers is 0, and bit 6 of all response Service Identifiers is 1" (positive response = SID + 0x40; negative = `7F SID NRC`). — [search summary, ISO 14230-3 references](https://www.ime-actia.de/en/?p=5089)
- EdiabasLib handles NRC 0x78 (responsePending), 0x21 (busyRepeatRequest) and 0x23 (routineNotComplete) specially. — [OBD] L4215-4258
- KGM bistable relay: it switches off terminal-30g-f loads (CA, CDC, JNAV/KNAV, COMBI, M-ASK/CCC, SZL, TCU/ULF) when quiescent current is too high. Switch-on triggers include unlocking, door contacts and "terminal R ON". Shutdown has "an afterrunning time of about 2 minutes", then a reset of about 10 s if the current is still too high. — [workshop-manuals KGM page](https://workshop-manuals.com/bmw/5_series_e60/m5_s85_sal/6_si_techniques/61__general_electrical_system/11_sbt__body-gateway_module_e60_e61_e63_e64/page_4252/)
- An undervoltage entry in at least 2 control units is treated by DIS bus analysis as the most likely cause of bus failure. Low battery during long diagnostic sessions therefore pollutes fault memory across the car. — [TIS-BUS]

### Inferences
- Gate at the frame level for KWP/BMW-FAST. Parse the header (3 bytes; 4 when `Fmt & 0x3F == 0`; 6 for the 16-bit length) and take `data[0]` as the SID.
  - Refuse: 0x10 with sub-function ≠ 0x81 (defaultSession; even 0x89 and other BMW session values are risky), 0x11, 0x14, 0x27, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34-0x37, 0x3B, 0x3D, 0x85, 0x28, 0x29, plus any functional (0xC0-0xFF) frame that carries one of these.
  - Concept 2/3/DS1/DS2 command bytes are manufacturer-specific, so there the gate must be job-level, as it already is in the current bridge's policy.
- Even safe reads have side effects. They need ignition on (terminal 15), which drains the battery: an N62 E61 with ignition on and the engine off can draw tens of amps across modules. They also keep K-CAN awake, and they can set 0x78-pending loops on slow modules. Keep sessions short and stop tester-present when idle. These are general domain reasoning, not sourced figures.
- The adapter-internal `82 F1 F1 xx` telegrams (CAN mode, block size and so on) write to the cable's EEPROM. A port's gate should also refuse unknown `F1→F1` frames except FE/FD reads.

### Gaps
- I did not get a verified, primary SID table for ISO 14230-3. I also did not get BMW's own KWP2000* service list (BMW GS-specific services, for example 0x21/0x22 identifiers or BMW 0x1A options).
- I found no measured ignition-on current figure for an E60/E61.

## Q7. Byte-level examples

### Takeaway
I found real, checksum-valid example frames for BMW-FAST and DS2 in EdiabasLib's docs, and adapter control telegrams in its firmware. I found no E60-captured trace of a fault-memory read (0x18/0x17) or an ident read (0x1A). The quickest way to get them is an IFH trace (`IfhTrace=3`) from Ryan's own EDIABAS.

### Cited Findings
- BMW-FAST (sum checksum, verified): `83 F1 01 59 02 7F 4F` (response to `83 01 F1 19 02 ..`); `93 F1 01 62 20 00 01 00 12 28 80 32 80 28 80 31 80 28 01 00 20 28 3E`. — [SIM]
- DS2 (XOR checksum, verified): request `00 04 00` (+ CS 0x04 added by the interface); response `00 10 A0 88 36 94 83 14 03 02 00 09 96 02 13 82`; request `00 05 08 00` (+CS); response `00 07 A0 00 05 95 37`. — [SIM]
- Adapter ignition query: `82 F1 F1 FE FE 60` returns `82 F1 F1 FE <state> <cs>`, after the echo. — [BUILD]
- KWP tester present: `{0x81, 0x33, 0xF1, 0x3E}` + checksum, default interval about 2 s (secondary). — [ediabasx-docs](https://emdzej.github.io/ediabasx-docs/reference/interfaces/obd32-protocols.html)

### Inferences
- A fault-memory read on a BMW-FAST E60 ECU should look like `8x <ecu> F1 18 02 FF FF cs` (KWP 0x18, ReadDTCByStatus, group FFFF), and an ident read like `82 <ecu> F1 1A 80 cs`. That is the ISO 14230 shape. The exact bytes depend on the SGBD, so capture them; do not hardcode them.
- DS2 ECU addresses on older BMW modules are typically 0x00 for the ident command. DS2 matters only for pre-E60 cars or carry-over modules.

### Gaps
- No captured E60 trace of DME ME9N62 or NVE_65 traffic was found. Capturing one through the existing bridge, with IFH trace enabled, is the recommended next step before porting.
