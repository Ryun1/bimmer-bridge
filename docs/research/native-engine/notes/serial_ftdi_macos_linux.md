# Driving a BMW K+DCAN (FTDI FT232R) cable natively on macOS and Linux, from .NET 10 or Zig 0.16

Evidence labels used below: **[TESTED-LOCAL]** = checked directly on this Mac (macOS 26.6.2, build 25G83, Apple Silicon, Xcode SDK, Zig 0.16.0 from Homebrew) on 2026-11-22, but with no cable attached, so no line was driven. **[SOURCE]** = read in source code. **[DOC]** = documentation. **[FORUM]** = user report. **[INFERENCE]** = my reasoning, not verified.

## Q1. macOS: Apple's FTDI driver, latency timer, FTDI VCP/D2XX, libusb/libftdi, and the ioctls

### Takeaway
On macOS 26, Apple's FTDI driver is a **DriverKit dext** (`com.apple.DriverKit-AppleUSBFTDI.dext`, class `AppleUSBFTDI` under `IOUserSerial`), not a kext. It matches VID 0x0403 / PID 0x6001 (FT232R), which is what K+DCAN cables use. The 16 ms latency timer **can be lowered per open fd with the `IOSSDATALAT` ioctl**. Apple confirmed this to a developer in 2022, and users report that the value is taken in milliseconds even though the header says microseconds. Custom baud rates such as 10400 go through `IOSSIOSPEED`, and break/DTR/exclusive access use the standard BSD ioctls. So the VCP path through `/dev/cu.usbserial-<serial>` is workable, and the D2XX/libusb route is optional.

### Cited Findings
- **[TESTED-LOCAL]** `/System/Library/DriverExtensions/com.apple.DriverKit-AppleUSBFTDI.dext` exists. Its Info.plist shows `CFBundlePackageType = DEXT`, `IOClass = IOUserSerial`, `IOUserClass = AppleUSBFTDI`, `IOProviderClass = IOUSBHostInterface` and `CFBundleIdentifierKernel = com.apple.driver.driverkit.serial`. The personality "DriverKit-AppleUSBFTDI" matches `idVendor 1027 (0x0403)` / `idProduct 24577 (0x6001 = FT232R)`. There is no FTDI kext in `/System/Library/Extensions`. The loaded kernel side is `com.apple.driver.driverkit.serial (6.0.0)` together with `com.apple.iokit.IOSerialFamily`. Sibling dexts exist for CH34x, PL2303 and SiLabs. (Local inspection: `plutil -p .../Info.plist`, `kmutil showloaded`.)
  - This contradicts a search-aggregator claim that "Apple removed their built-in FTDI kernel driver in Monterey… no longer a source of conflict" ([pylibftdi troubleshooting, via search summary](https://pylibftdi.readthedocs.io/en/latest/troubleshooting.html)). The kext was removed, but a dext replaced it, and the dext still claims FT232R.
- **[TESTED-LOCAL]** The dext binary contains strings `calculateActualBaudRate` and `idChip - Invalid chip ID (%x), assuming F1232AM`. This suggests it does its own FTDI divisor math, including chip-type detection. (`strings` on the dext binary.)
- **[DOC]/[TESTED-LOCAL]** The SDK header `IOKit/serial/ioss.h` defines:
  - `IOSSIOSPEED _IOW('T', 2, speed_t)`: "Sets the input speed and output speed to a non-traditional baud rate".
  - `IOSSDATALAT _IOW('T', 0, unsigned long)`: "Sets the receive latency (in microseconds) with the default value of 0 meaning a 256 / 3 character delay latency".
  - `IOSSPREEMPT _IOW('T', 1, int)`.
  
  (Local SDK `MacOSX.sdk/System/Library/Frameworks/IOKit.framework/Headers/serial/ioss.h`.)
- **[TESTED-LOCAL]** Numeric ioctl values on arm64 macOS, from a compiled C probe (useful for Zig, which lacks them, and for .NET P/Invoke):

  | ioctl | value |
  |---|---|
  | TIOCEXCL | 0x2000740d |
  | TIOCNXCL | 0x2000740e |
  | TIOCSBRK | 0x2000747b |
  | TIOCCBRK | 0x2000747a |
  | TIOCMGET | 0x4004746a |
  | TIOCMSET | 0x8004746d |
  | TIOCMBIS | 0x8004746c |
  | TIOCMBIC | 0x8004746b |
  | FIONREAD | 0x4004667f |
  | TIOCDRAIN | 0x2000745e |
  | IOSSIOSPEED | 0x80085402 |
  | IOSSDATALAT | 0x80085400 |

  Also: `sizeof(speed_t) = 8`, `sizeof(struct termios) = 72`, `TIOCM_DTR = 2`, `TIOCM_RTS = 4`. (Definitions in SDK `sys/ttycom.h`, `sys/filio.h`, `IOKit/serial/ioss.h`.)
- **[FORUM, Apple response]** Apple Feedback Assistant replied in June 2022: "Latency timer can be configured with ioctl IOSSDATALAT". The reply said the buffer size is not configurable. User "sth" (August 2022) set it with `ioctl(fd, IOSSDATALAT, &value)` after open. He found the unit is **milliseconds, not microseconds**: a value of 1 worked, and 0 "behaved oddly", so avoid it. In a 1000-character echo loopback, latency 2 took about 2 s and latency 1 took about 1 s. He tested on Mojave and Monterey. — [OpenBCI forum thread](https://openbci.com/forum/index.php?p=%2Fdiscussion%2F3108%2Fftdi-driver-latency-timer-for-macbook-m1)
- **[FORUM]** In the same thread, mkeeter (June 2022) used Homebrew libftdi to set the latency timer from 16 to 2 while the Apple driver was in place. The value reset to 16 on replug, which shows it lives in volatile chip state, so it must be reapplied on every connect. — [OpenBCI forum](https://openbci.com/forum/index.php?p=%2Fdiscussion%2F3108%2Fftdi-driver-latency-timer-for-macbook-m1)
- **[FORUM]** The Apple Developer Forums thread "Adjust Latency Timer for the AppleUSBFTDI Driver" (September 2021, with a follow-up in May 2024) has **no Apple answer**. — [developer.apple.com/forums/thread/690306](https://developer.apple.com/forums/thread/690306)
- **[FORUM]** A robotiq-sdk issue opened 2026-08-15 still states that "macOS exposes no SDK-accessible control" of the latency timer through VCP. It proposes a D2XX backend calling `FT_SetLatencyTimer(1)`. It notes that on macOS 11+ the device "must be released from Apple's built-in VCP driver (via a codeless dext or driver exclusion) before D2XX can claim it". IOSSDATALAT is not mentioned. — [robotiq/robotiq-sdk#43](https://github.com/robotiq/robotiq-sdk/issues/43)
- **[DOC]** FTDI's own Mac VCP kext was configured through the `LatencyTimer` key under `ConfigData` in its Info.plist (default 16). This applies only to FTDI's kext, not to Apple's dext. — [FTDI TN_105](https://www.ftdichip.com/Support/Documents/TechnicalNotes/TN_105%20Adding%20Support%20for%20New%20FTDI%20Devices%20to%20Mac%20Driver.pdf)
- **[DOC]** FTDI's VCP page: the latest macOS VCP is 1.5.0 (2022-06-13), marked **Beta**, for macOS 10.15 and 11/12. D2XX for macOS is 1.4.30 (2024-05-04). FTDI states that "With the latest Apple developed VCP driver, both the FTDI D2XX and the Apple VCP driver will work together without conflict." Arm64 build labelling was unclear in the fetched table. — [FTDI VCP drivers](https://ftdichip.com/drivers/vcp-drivers/), [FTDI D2XX drivers](https://ftdichip.com/?p=701)
- **[SOURCE/DOC]** libusb on macOS: detaching a kernel driver (capture) requires the binary to hold the `com.apple.vm.device-access` entitlement (plus a successful `IOServiceAuthorize()`), **or** to run as root. It is done by re-enumerating with a capture bit. Developers report that Apple is reluctant to grant this entitlement to non-VM apps. — [libusb darwin capture commit](https://flutter.googlesource.com/third_party/libusb/+/abd788c203624930241f45f32b501c16936bd806%5E%21/), [Apple forum: entitlement request](https://developer.apple.com/forums/thread/857175), [libusb FAQ](https://github.com/libusb/libusb/wiki/FAQ)

### Inferences
- **Recommended macOS stack (VCP):**
  1. `open("/dev/cu.usbserial-XXXX", O_RDWR|O_NOCTTY|O_NONBLOCK)`. Use the `cu.` node, not `tty.`, which blocks on DCD.
  2. `ioctl(TIOCEXCL)`.
  3. `tcsetattr` in raw 8N1 mode with a standard placeholder speed.
  4. `ioctl(IOSSIOSPEED, &(speed_t){10400})` for non-standard rates.
  5. `ioctl(IOSSDATALAT, &(unsigned long){1})`.
  6. `TIOCMBIS TIOCM_DTR` (EdiabasLib holds DTR high).
  
  Re-issue IOSSIOSPEED after any later `tcsetattr`, because tcsetattr may overwrite the speed. .NET's shim already re-applies it after tcsetattr (see Q3).
- Break on macOS uses `TIOCSBRK`/`TIOCCBRK`. Each one becomes an asynchronous USB control request (SET_DATA with the break bit) issued from the dext. Expect jitter of roughly ±1–2 ms plus scheduling noise. That is fine for a 25 ms KWP fast-init low pulse and a 200 ms 5-baud bit. **Not verified on Apple's dext.** The kw1281test author said in 2023 that his ~2022 logic-analyzer test showed macOS did not time TIOCSBRK/TIOCCBRK precisely ([kw1281test#35](https://github.com/gmenounos/kw1281test/issues/35)). This **must be scoped on real hardware**.
- Whether the libftdi latency write in the OpenBCI thread worked while the Apple dext was bound is not explained there. Plausibly, libusb on darwin falls back to an unseized device open that still permits device-level control transfers (the latency timer is a vendor control request to the device). This is speculation; test it.
- D2XX is the only way to get FTDI bit-bang mode, but it needs the Apple dext unbound: root, a codeless dext, or the entitlement. Given that FT232R bit-bang timing is known to be poor (Q6), D2XX is not worth that friction just for 5-baud init.

### Gaps
- No primary Apple documentation describes AppleUSBFTDI's IOSSDATALAT handling. The unit (ms vs µs) comes from one forum tester on pre-dext macOS versions. Its behaviour on the macOS 15/26 **dext** is unverified. Test by echo timing with the cable.
- No measurement of TIOCSBRK/TIOCCBRK edge timing on the AppleUSBFTDI dext was found.
- Whether a USB-side IORegistry property exposes the latency timer was not found.
- Whether FTDI's 1.5.0 VCP kext installs or loads on macOS 15/26 Apple Silicon is not confirmed. Third-party kexts need reduced security on Apple Silicon anyway; treat this route as dead.
- No source covered a "USB device capture" entitlement beyond `com.apple.vm.device-access`.

## Q2. Linux: ftdi_sio latency, custom baud, break, exclusivity, libftdi, permissions

### Takeaway
Linux is the easy platform. ftdi_sio exposes a root-writable sysfs `latency_timer`, in ms from 0 to 255. Setting `ASYNC_LOW_LATENCY` through `TIOCSSERIAL` forces the timer to 1 ms, and an unprivileged owner of the tty can do this. Custom 10400 baud works through termios2/BOTHER. Break uses `TIOCSBRK`/`TIOCCBRK`, each of which is one immediate USB control request. Parity changes on an open port send SET_DATA immediately.

### Cited Findings
- **[SOURCE]** `latency_timer` is `DEVICE_ATTR_RW` and is written via `latency_timer_store()`, parsed with `kstrtou8` (0–255, units "milliseconds"). It is hidden for SIO/FT232A. If `ASYNC_LOW_LATENCY` is set, the show function reports 1. — [ftdi_sio.c](https://raw.githubusercontent.com/torvalds/linux/master/drivers/usb/serial/ftdi_sio.c)
- **[SOURCE]** `set_serial_info()` (TIOCSSERIAL) applies `priv->flags = ss->flags & ASYNC_FLAGS`. Non-root callers may only change `ASYNC_USR_MASK` bits. `write_latency_timer()` uses `l = 1` when `ASYNC_LOW_LATENCY` is set. Flags start at zero. The default latency is the chip's value, or 16 if reading it fails (the NDI quirk uses 1). — [ftdi_sio.c](https://raw.githubusercontent.com/torvalds/linux/master/drivers/usb/serial/ftdi_sio.c)
- **[SOURCE]** Baud: FT232R goes through `get_ftdi_divisor()` and then `ftdi_232bm_baud_to_divisor()` (48 MHz base), using the `divfrac[8] = {0,3,2,4,1,5,6,7}` fraction encoding for baud ≤ 3,000,000. The legacy `ASYNC_SPD_CUST` + 38400 + `custom_divisor` alias still exists but is deprecated: the kernel logs "use of SPD flags is deprecated" ([Linux Foundation forum](https://forum.linuxfoundation.org/discussion/854459/ftdi-sio-ttyusb0-use-of-spd-flags-is-deprecated)). The actual rate is written back with `tty_encode_baud_rate()`. — [ftdi_sio.c](https://raw.githubusercontent.com/torvalds/linux/master/drivers/usb/serial/ftdi_sio.c)
- **[SOURCE]** `ftdi_break_ctl()` sends one `FTDI_SIO_SET_DATA_REQUEST` with the break bit OR'd into `last_set_data_value`, or cleared from it. There is no timing logic in the driver, so the length of the break is set entirely by user space. — [ftdi_sio.c](https://raw.githubusercontent.com/torvalds/linux/master/drivers/usb/serial/ftdi_sio.c)
- **[SOURCE]** `ftdi_set_termios()` sends SET_DATA immediately when any of `CSIZE|PARODD|PARENB|CMSPAR|CSTOPB` change, so a parity flip on an open port is applied at once. `ftdi_tiocmset()` → `update_mctrl()` sends `SET_MODEM_CTRL` immediately, while `tiocmget` returns cached DTR/RTS. — [ftdi_sio.c](https://raw.githubusercontent.com/torvalds/linux/master/drivers/usb/serial/ftdi_sio.c)
- **[FORUM]** The Linux kernel handles `TCSBRKP` as `send_break(tty, arg ? arg*100 : 250)`, i.e. in deciseconds. `tcsendbreak(fd, 0)` gives 0.25–0.5 s. This is why timed breaks for 5-baud init need TIOCSBRK/TIOCCBRK pairs. — [kw1281test#35](https://github.com/gmenounos/kw1281test/issues/35)
- **[DOC]** The latency sysfs path is `/sys/class/tty/<ttyUSBn>/device/latency_timer`. One low-latency serial library sets it to 1 together with `ASYNC_LOW_LATENCY`. — [anyserial Linux tuning](https://graysonbellamy.github.io/anyserial/linux-tuning/), [serialport_low_latency crate](https://lib.rs/crates/serialport_low_latency)

### Inferences
- **Recommended Linux recipe:**
  - Open `/dev/ttyUSBn` (or the stable `/dev/serial/by-id/usb-FTDI_...`).
  - Call `ioctl(TIOCEXCL)`. Optionally add `flock(LOCK_EX|LOCK_NB)` for cooperative locking with other tools.
  - Call `TIOCGSERIAL`, set `flags |= ASYNC_LOW_LATENCY`, then `TIOCSSERIAL`. No root needed.
  - Use termios2 (`TCGETS2`/`TCSETS2`, `c_cflag &= ~CBAUD; c_cflag |= BOTHER; c_ispeed = c_ospeed = 10400`) for 10400.
  - Use `TIOCMBIS TIOCM_DTR`.
  - Permissions: the user must be in the `dialout` group (Debian/Ubuntu; `uucp` on Arch). A udev rule such as `ACTION=="add", SUBSYSTEM=="usb-serial", DRIVER=="ftdi_sio", ATTR{latency_timer}="1"` is the usual way to set sysfs without root. This is a common pattern; I did not fetch a citation for this exact rule text.
- libftdi on Linux works via `libusb_detach_kernel_driver` with no special privileges beyond device-node access (udev `MODE="0660", GROUP="plugdev"`). It also gives `ftdi_set_latency_timer(1)` and bit-bang mode. It is only needed if VCP proves inadequate.

### Gaps
- `ASYNC_USR_MASK` membership of `ASYNC_LOW_LATENCY` comes from the source comment and the behaviour quoted above. I did not fetch `serial.h` itself to quote it.
- I found no measurements of TIOCSBRK→line edge latency on ftdi_sio.

## Q3. FTDI baud divisor math (FT232R)

### Takeaway
FT232R baud = 3 MHz / (n + k/8), with n a 14-bit integer.
- **10400 → divisor 288.5 → 10398.6 baud (−0.013%)**
- **115200 → 26.0 → 115384.6 baud (+0.16%)**
- 9600 is exact (312.5).

All of these are well within UART tolerance. The **minimum rate is about 183 baud**, so **5 baud cannot be generated by the UART** and must be emulated with break or bit-bang.

### Cited Findings
- **[DOC]** The FT232R/FT232B baud generator divides the 48 MHz clock by 16 to give a 3 MHz reference, then applies a prescaler of integer + sub-integer. The sub-integers {0, .125, .25, .375, .5, .625, .75, .875} are supported on FT232R/FT232B. Divisor 0 gives 3 MBaud and 1 gives 2 MBaud; divisors between 1 and 2 are impossible. The driver picks the closest divisor within ±3%. The integer part is in bits 13:0. — [FTDI AN232B-05](https://www.ftdichip.com/old2020/Support/Documents/AppNotes/AN232B-05_BaudRates.pdf), [AN_120 aliasing](https://www.ftdichip.com/old2020/support/Documents/AppNotes/AN_120_Aliasing_VCP_Baud_Rates.pdf)
- **[FORUM]** A Linux bug report measured that all sub-integer prescalers work on FT232R. — [kernel bugzilla 210351](https://bugzilla.kernel.org/show_bug.cgi?id=210351)
- **[TESTED-LOCAL, calculation]** Using round(3e6×8/baud)/8:

  | Requested baud | Divisor | Actual baud | Error |
  |---|---|---|---|
  | 9600 | 312.5 | 9600 | 0 |
  | 10400 | 288.5 | 10398.61 | −0.0133% |
  | 57600 | 52.125 | 57553.96 | −0.08% |
  | 62500 | 48 | 62500 | 0 |
  | 115200 | 26 | 115384.6 | +0.160% |

  Maximum divisor 16383.875 gives 183.1 baud.

### Inferences
- The Linux `ftdi_232bm_baud_base_to_divisor` rounds to the nearest eighth, so it should land on the same 288.5 for 10400. Apple's `calculateActualBaudRate` presumably does the same, but this is unverified; reading back with `cfgetospeed` or a scope would confirm it.

### Gaps
- The exact rounding in Apple's dext is unknown because it is closed source.

## Q4. .NET System.IO.Ports on macOS/Linux (as of .NET 10)

### Takeaway
On Unix, System.IO.Ports is a thin termios shim, and custom baud works (IOSSIOSPEED on macOS, termios2 on Linux). **`BreakState = true` is not a held break.** It calls `tcsendbreak(fd, 0)`, a one-shot pulse of about 0.25–0.5 s, and `BreakState = false` does nothing. That breaks EdiabasLib's KWP fast init (25 ms low) and its BreakState-based 5-baud bit-bang. Read/Write timeouts are rounded up to 30 ms. There is no API for latency timer, exclusivity or thread priority. Plan to P/Invoke `ioctl` directly for break, latency and TIOCEXCL, or bypass SerialPort entirely.

### Cited Findings
- **[SOURCE]** Behaviour of `SerialStream.Unix.cs`:
  - `BreakState` setter: `if (value) TermiosSendBreak(_handle, 0)`, then stores `_inBreak`. The getter returns the cached field. A code comment says Unix has no infinite break and the duration is platform-dependent.
  - `Parity` setter: calls `TermiosReset(handle, baud, dataBits, stopBits, parity, handshake)`, which re-applies the whole termios.
  - `BytesToRead` uses `TermiosGetAvailableBytes` (FIONREAD).
  - The IO loop polls with a 1 ms `poll()` timeout and `Thread.Sleep(1)` when idle. It exits after 2000 ms idle.
  - `TimeoutResolution = 30` ms, so smaller timeouts are rounded up.
  - DTR goes through `TermiosSetSignal`.
  
  — [source.dot.net SerialStream.Unix.cs](https://source.dot.net/System.IO.Ports/System/IO/Ports/SerialStream.Unix.cs.html)
- **[SOURCE]** Behaviour of `pal_termios.c`:
  - `TermiosSetSpeed`: standard rates use `cfsetspeed` + `tcsetattr(TCSANOW)`. Non-standard rates (10400 maps to B0) try `ioctl(fd, IOSSIOSPEED, &brate)` when `HAVE_IOSS_H` (macOS), otherwise `Termios2SetSpeed` when `HAVE_TERMIOS2` (Linux).
  - `TermiosReset` applies attributes with TCSANOW and **no flush**, then re-applies the deferred non-standard speed via IOSSIOSPEED/termios2. It never sets VMIN/VTIME.
  - `TermiosSendBreak` is `return tcsendbreak(fd, duration)`.
  - `TermiosSetSignal` uses TIOCMGET + modify + TIOCMSET.
  - Available bytes use `FIONREAD` (read) or `TIOCOUTQ` (write).
  
  — [dotnet/runtime pal_termios.c](https://raw.githubusercontent.com/dotnet/runtime/main/src/native/libs/System.IO.Ports.Native/pal_termios.c)
- **[FORUM]** `tcsendbreak(fd, 0)` lasts "between 0.25 and 0.5 seconds", and nonzero durations are inconsistent across OSes. — [kw1281test#35](https://github.com/gmenounos/kw1281test/issues/35)
- **[FORUM]** Reports of high CPU use and a memory leak in Unix System.IO.Ports (dotnet/runtime #2379, #55146) led some projects to forks. — [dotnet/iot#1832](https://github.com/dotnet/iot/issues/1832)
- **[FORUM]** An older corefx thread said non-standard low baud rates were then unsupported on Linux. This predates the termios2 path now in pal_termios.c. — [dotnet/corefx#33146](https://github.com/dotnet/corefx/issues/33146)
- **[DOC]** System.IO.Ports 10.0.x ships per-RID native shims, e.g. `runtime.native.System.IO.Ports 10.0.10` and osx packages. I found no .NET 10 release note changing the Unix behaviour. — [NuGet System.IO.Ports 10.0.10](https://nuget.org/packages/System.IO.Ports/10.0.10)

### Inferences
- **For an EdiabasLib port:** keep SerialPort for open, baud, parity and read/write if convenient. Obtain the fd from `SerialPort.BaseStream` via reflection on the `SafeHandle` (an internal field, fragile), or better, open the device yourself with a P/Invoke'd `open()` and wrap it in a `FileStream`/`SafeFileHandle`. Implement BreakState with `ioctl(fd, TIOCSBRK/TIOCCBRK)`, latency with `IOSSDATALAT` or `TIOCSSERIAL`, and exclusivity with `TIOCEXCL`.
- The 1 ms `BytesToRead` polling in EdiabasLib still works because FIONREAD is direct. A blocking `poll()` on the fd, or `read` with VMIN=0/VTIME=0 and a deadline, would be cleaner and cheaper.
- A parity switch via `TermiosReset` (TCSANOW, no drain) is applied immediately. The caller must drain TX first with `tcdrain` (`Flush()` does this) to avoid changing parity mid-byte.

### Gaps
- `Termios2SetSpeed` (in a separate file) was not fetched, so its exact Linux BOTHER implementation is not confirmed here.
- No .NET 10–specific changelog for System.IO.Ports Unix was found. I assume it is unchanged.

## Q5. Timing: ~1 ms inter-byte/telegram timing in user space; real-time priority

### Takeaway
The USB-serial floor is the FTDI latency timer (set it to 1 ms) plus USB full-speed scheduling (1 ms frames), so expect RX turnaround of about 1–2 ms at best. For TX timing (KWP P3/P4, init pulses), use `clock_nanosleep`/`mach_wait_until` on a raised-priority thread. Do not use .NET timers or `Thread.Sleep`. On macOS that means `THREAD_TIME_CONSTRAINT_POLICY`; on Linux, `SCHED_FIFO` (needs `CAP_SYS_NICE` or an rtprio rlimit) or simply a busy-wait on `CLOCK_MONOTONIC` for sub-ms gaps.

### Cited Findings
- **[SOURCE]** XNU `THREAD_TIME_CONSTRAINT_POLICY` takes period, computation, constraint and preemptible. `thread_policy_set` rejects constraint < computation, or computation outside the real-time quantum bounds. — [xnu thread_policy.h](https://opensource.apple.com/source/xnu/xnu-4570.1.46/osfmk/mach/thread_policy.h.auto.html)
- **[SOURCE]** Production use: the `audio_thread_priority` crate sets computation = constraint/2 "per macOS 12 behaviour", capped at 50 ms. Chromium notes that `thread_policy_set` can fail under heavy load. — [audio_thread_priority rt_mach.rs](https://docs.rs/crate/audio_thread_priority/0.35.0/source/src/rt_mach.rs), [Chromium platform_thread_apple.mm](https://chromium.googlesource.com/chromium/src/+/b5470ac602dd94de3fa37f35c8a2a3649c6d013d/base/threading/platform_thread_apple.mm)
- **[FORUM]** Whether the policy is honoured on Apple Silicon is still asked on Apple's forums, with no definitive answer in the snippet. — [Apple forum 732171](https://developer.apple.com/forums/thread/732171)
- **[FORUM]** With IOSSDATALAT=1, a byte echo round trip measured about 1 ms. — [OpenBCI forum](https://openbci.com/forum/index.php?p=%2Fdiscussion%2F3108%2Fftdi-driver-latency-timer-for-macbook-m1)

### Inferences
- From .NET you can P/Invoke `thread_policy_set` (libSystem) or `pthread_setschedparam` on a dedicated `Thread`. GC pauses remain a risk: consider `GCSettings.LatencyMode = SustainedLowLatency` and allocation-free I/O loops.
- The 5-baud and fast-init waveforms are 25–200 ms, so ±1 ms jitter is acceptable. The tighter constraints are the ECU response windows (P2 25–50 ms for KWP; BMW DS2/KWP-2000* timing as in EdiabasLib). These are relaxed relative to 1 ms.

### Gaps
- No measured TX-side break/edge jitter for either OS.

## Q6. Zig 0.16: serial libraries, termios/ioctl gaps, IOKit, libusb/libftdi

### Takeaway
Zig 0.16 std has `posix.tcgetattr`/`tcsetattr` and per-OS `termios`/`speed_t`. On **Linux** `std.os.linux.T` includes `IOCEXCL`, `IOCSBRK`, `IOCMSET`/`IOCMGET`, `IOCGSERIAL` and so on. On **Darwin** it has **no TIOC\*/IOSS\* constants**, so define them yourself (values in the Q1 table) and call `std.c.ioctl`. termios2/`TCSETS2`/`BOTHER` were not found in std, so define the struct and constants manually for Linux. ZigEmbeddedGroup/serial supports Linux, macOS and Windows with config, parity and flush, and targets Zig 0.16. It has no break, custom-baud or latency API.

### Cited Findings
- **[TESTED-LOCAL]** In `/opt/homebrew/Cellar/zig/0.16.0_1/lib/zig/std`:
  - `posix.zig:1163/1178` defines `tcgetattr`/`tcsetattr`.
  - `c.zig:9479` defines `speed_t` per OS (Darwin `B115200 = 115200`, i.e. the numeric speed is the constant, so IOSSIOSPEED takes a plain integer).
  - `os/linux.zig` defines `T.IOCEXCL = 0x540c`, `T.IOCSBRK = 0x5427`, `T.IOCMSET = 0x5418`, `T.IOCMGET = 0x5415`, `T.IOCGSERIAL = 0x541e`, `T.CSBRKP = 0x5425` (x86/arm generic table).
  - grep for `TIOCEXCL|IOSSIOSPEED|TIOCSBRK|BOTHER|TCSETS2` found nothing in `c/darwin.zig` or elsewhere.
- **[DOC]** ZigEmbeddedGroup/serial (MIT; 98 stars, 113 commits): "supports Windows, Linux and Mac". `SerialConfig` covers baud, parity (none/even/odd/mark/space), stop bits, handshake and byte size. It also has `configureSerialPort`, flush and port listing (listing on Windows only). The README states Zig 0.16.0. Break and control pins are not mentioned. — [github.com/ZigEmbeddedGroup/serial](https://github.com/ZigEmbeddedGroup/serial)

### Inferences
- Zig plan:
  - Use std or zig-serial for open/termios.
  - Add a small `darwin_serial.zig` with `const TIOCEXCL: c_ulong = 0x2000740d; IOSSIOSPEED = 0x80085402; IOSSDATALAT = 0x80085400; TIOCSBRK/TIOCCBRK/TIOCMBIS/TIOCMBIC` and call `std.c.ioctl(fd, req, &arg)`.
  - For Linux, define `termios2` (`extern struct { c_iflag, c_oflag, c_cflag, c_lflag: u32, c_line: u8, c_cc: [19]u8, c_ispeed, c_ospeed: u32 }`, `TCGETS2 = _IOR('T',0x2A,termios2)`, `TCSETS2 = _IOW('T',0x2B,termios2)`, `BOTHER = 0o010000`). Verify against kernel `asm-generic/termbits.h` before use.
  - Real-time priority: `@cImport` `<mach/thread_policy.h>` or declare `thread_policy_set` extern.
  - IOKit is not needed for the VCP path. To find the device by VID/PID/serial, scan `/dev/cu.usbserial-*` (the suffix is the FTDI serial number) or link `-framework IOKit -framework CoreFoundation` and use `IOServiceMatching(kIOSerialBSDServiceValue)`.
  - libusb/libftdi can be linked as C libraries via `@cImport`. No maintained Zig-native bindings were researched.

### Gaps
- I did not verify that zig-serial's latest master compiles on 0.16, or check its open issues.
- No Zig libftdi binding survey was done.

## Q7. How other open-source K-line tools handle 5-baud init and latency on FTDI

### Takeaway
The common pattern is to emulate 5-baud init with timed break set/clear (200 ms per bit) rather than UART 5 baud, which FTDI cannot generate, and to lower the FTDI latency timer. Tools that want precise timing on Unix often go to FTDI D2XX/libftdi. EdiabasLib itself supports a `FTDI` port prefix that uses D2XX "for better timing".

### Cited Findings
- **[FORUM/DOC]** kw1281test (C#, VAG KW1281):
  - It simulates 5-baud by setting/clearing break for 200 ms per bit, which works on Windows.
  - On Linux/macOS it originally supported only FTDI by ID (D2XX), because standard APIs lack timed break.
  - Proposals: TIOCSBRK/TIOCCBRK, and Linux `TCSBRKP` in deciseconds.
  - The author's ~2022 logic-analyzer test found macOS TIOCSBRK/TIOCCBRK timing imprecise.
  - A Linux interface was added in PR #73 (April 2024); results unconfirmed.
  - One user succeeded with an FT232RL-based KKL on a Raspberry Pi.
  
  — [kw1281test#35](https://github.com/gmenounos/kw1281test/issues/35)
- **[DOC]** EdiabasLib allows `FTDI` in place of `COM` in `ObdComPort`/`AdsComPort` to use D2XX directly "for better timing", especially for the ADS adapter. Devices are selected by index, serial number, description or location. — [EdiabasLib.config docs](https://uholeschak.github.io/ediabaslib/docs/EdiabasLib.config_file.html), [Adapter types](https://uholeschak.github.io/ediabaslib/docs/AdapterTypes.html)
- **[FORUM]** FT232R bit-bang mode has silicon errata that make its timing unreliable; FT232H/FT230X reportedly behave. — [Hackaday: silicon bugs in the FT232R](https://hackaday.com/2018/06/15/silicon-bugs-in-the-ftdi-ft232r-and-a-tidy-rf-vco-project)

### Inferences
- For K+DCAN on FT232R, use **break-based** 5-baud and fast init via TIOCSBRK/TIOCCBRK with a monotonic-clock wait, not D2XX bit-bang. Then switch to 10400 (IOSSIOSPEED / termios2) to catch the 0x55 sync and keywords. The ~10 ms needed to change baud is a risk, because the ECU's sync byte follows the address within W1 = 20–300 ms. Set the baud before the final stop bit completes, i.e. reconfigure while break-idle.
- Note that K+DCAN cables also route the DCAN/K-line selection through DTR/RTS (EdiabasLib's DTR-high behaviour). On both OSes these map to TIOCMBIS/TIOCMBIC, which ftdi_sio applies immediately and Apple's dext presumably also applies immediately.

### Gaps
- FreeDiag's and ScanTool.net's Unix 5-baud code was not fetched (search returned nothing specific), so no citation for their exact technique.
- No source found on whether Ross-Tech supports Linux natively.
- No source measured AppleUSBFTDI break timing on the macOS 15/26 dext.
