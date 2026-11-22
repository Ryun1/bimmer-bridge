# kline

The cable layer of the native engine: a Zig library (`Port`) for the K+DCAN cable (FTDI FT232R,
USB `0403:6001`) on macOS and Linux, and a `kline` CLI for bringing the cable up at a desk.
M0 has no C ABI, frame gate or recorder yet; those come in M1.

**Never run `kline` with the cable plugged into a car.** `probe` toggles DTR and holds a
25 ms break on the K-line, and a break is how a fast-init wake-up starts. `loopback` is for a
bench 12 V supply only.

## Build and test

Zig 0.16.0.

```sh
cd kline
zig build test          # unit tests (pty-based; nothing touches a real device)
zig build check-linux   # from a Mac: type-check the Linux tests, verifying every
                        # hand-declared Linux constant against the kernel headers
zig build               # zig-out/bin/kline and zig-out/lib/libkline.a
```

## Commands

### `kline list`

One line per `/dev/cu.usbserial-*` (macOS) or `/dev/ttyUSB*` (Linux), with the USB `VID:PID`
and `ftdi` or `other`. With no cable it prints nothing and exits 0. On macOS, a cable passed
through to the UTM VM is absent here; detach it from the VM first.

### `kline probe <device>`

Opens the device and prints one `ok`/`FAIL` line per check:

| Check | What it does |
|---|---|
| `identity 0403:6001` | USB IDs via IOKit (macOS) or sysfs (Linux) |
| `lock` | holds the device, then tries a second open twice: under the same lock file (refused by `flock`) and under a different lock directory (refused by `TIOCEXCL`) |
| `line 9600 8E1`, `line 115200 8N1`, `line 10400 8N1` | sets each and prints what the driver reads back. 10400 uses `IOSSIOSPEED` (macOS) or termios2 `BOTHER` (Linux) |
| `latency 1ms` | `IOSSDATALAT` = 1 ms (macOS; resets on replug) or `ASYNC_LOW_LATENCY` (Linux) |
| `dtr on/off` | raises then drops DTR |
| `dsr` | prints DSR |
| `break 25ms` | holds a break for 25 ms on the monotonic clock and prints how long it was held by the host clock (the line itself needs a scope) |

It exits 0 only if identity, lock and all three line settings pass. Latency, DTR, DSR and break
are reported but do not change the exit code.

### `kline loopback <device> --bench [--latency 1|16] [--count N]`

**Bench 12 V supply only, never a car.** Power the cable with 12 V on OBD pin 16 and ground on
pins 4/5, with nothing else connected; the cable then echoes every byte it sends on the
K-line. `loopback` sends `N` single bytes 0x55 at 115200 8N1 (default 100) and times each
echo, then prints min/median/max round trip in ms. `--latency 1` (default) sets the latency timer to
1 ms first; `--latency 16` sets it back to 16 ms (the FTDI default) explicitly, since a 1 ms
setting from `probe` or an earlier run lasts until the cable is replugged. It refuses to run without
`--bench`.

## Linux permissions

Install a udev rule so the logged-in user can open the cable without root, then replug it:

```
# /etc/udev/rules.d/70-kline.rules
SUBSYSTEM=="tty", ATTRS{idVendor}=="0403", ATTRS{idProduct}=="6001", TAG+="uaccess"
```

```sh
sudo udevadm control --reload && sudo udevadm trigger
```

## Errors

`Port.open` returns `NoDevice` (missing or unplugged), `Locked` (another process or tool holds
it), `NotFtdi`, `Permission` or `Io`.

The device path is resolved first, so a symlink such as Linux's `/dev/serial/by-id/usb-FTDI_…`
is identified and locked as the `/dev/ttyUSB*` node it points to.

The lock file is always `/tmp/kline-<device>.lock` (macOS's `cu.`/`tty.` prefix dropped),
never taken from the environment, so a run under `sudo` or from another session meets the same
lock. It is an `flock`, released by the kernel when its holder exits, so a lock file left by a
crashed run never blocks the next open. Root bypasses the device's own exclusive-access flag
(`TIOCEXCL`) on both macOS and Linux; the lock file still applies.
