# gate: XGate32.dll, the EDIABAS write gate

`XGate32.dll` is a Win32 x86 EDIABAS interface handler (IFH) that sits between EDIABAS and its
real one, `XStd32.dll`. It forwards every call, except that it refuses any telegram the shared
telegram rule does not allow, refuses every IFH function that can write, reprogram or switch
the cable into a raw mode, and after a refusal refuses everything else until EDIABAS shuts the
interface down. It is what guarantees that the bridge never writes to a car: the job policy and
the agent's IFH-trace check only refuse early or prove afterwards; this stops the bytes.

Only the bridge uses it: the agent opens EDIABAS with `apiInitExt("GATE:OBD", …)`. INPA, NCS
Expert and Tool32 keep `STD:OBD` and `XStd32.dll`, untouched. Why and how it was chosen:
[docs/spikes/2026-11-24-write-gate.md](../docs/spikes/2026-11-24-write-gate.md) (option A).

It fails closed: no `XStd32.dll` (or a missing export) → every call fails; no `XGate.ini` →
enforce; an unknown or older `MESSAGE` layout → every call refused; a failed safety check
inside the gate traps before anything is forwarded (ending the EDIABAS process unless it catches the exception).

## The rule

[docs/api.md "Telegram rule"](../docs/api.md#telegram-rule), version 1, from
[`policy/telegrams.json`](../policy/telegrams.json). `src/rule.zig` implements it as a pure
function, `check(concept, frame, list) ?Refusal`, with the reasons `no_concept`,
`unknown_concept`, `ds2`, `malformed`, `adapter`, `service`. The allow-list is compiled in:
`tools/gen_allowlist.zig` validates `policy/telegrams.json` at build time (`src/allowlist_json.zig`:
unknown, missing or duplicate keys, a version other than 1, anything but two-digit hex bytes,
an empty `note`, or `10`/`30`/`31` in `readServices` fail the build) and emits Zig source.
Nothing is read at run time, so changing the allow-list means rebuilding and reinstalling the
DLL. `zig build test` runs every case in
[`policy/telegram-cases.json`](../policy/telegram-cases.json).

## What each IFH function gets

`dllCallIFH(msgIn, msgOut)`, by `msgIn->fktNo`:

| Functions | Decision |
|---|---|
| 29 ifhSendTelegram, 30 ifhSendTelegramFreq | `msgIn` data judged as a telegram, under the channel's concept |
| 31 ifhRequTelegramFreq, 34 ifhRepeatLastMsg | allowed when `msgIn` len is 0, else judged as a telegram |
| 1–5, 8, 11–14, 20–24, 27, 28, 32, 33, 35, 37, 40, 43, 46–49, 51, 54 | allowed without inspection, logged |
| 25, 26, 36, 38, 39, 41, 42, 44, 45, 50, and every other number | refused (`function`) |

The concept is taken from each forwarded `ifhSetParameter` (27): its first two data bytes,
little-endian (fewer than two bytes → none). It is kept **per `MESSAGE` channel** (a telegram on
a channel whose concept was never set is `no_concept`; at most 16 channels are tracked, a 17th
stays at none), and cleared by `ifhInit` (1), `ifhEnd` (8), `dllStartupIFH` and
`dllShutdownIFH`. `IfhSetParameterRaw` (54) does not set the concept: its data is only logged.

Other refusal reasons, besides the rule's: `function` (above), `compat` (the `MESSAGE` layout
is not known to be compatibility 5/6), `no_message` (`msgIn` is null), `ifh_name` (the
`dllStartupIFH` name is over 263 bytes).

`dllStartupIFH(iniPath, ifhName)` logs both and forwards; an `ifhName` starting with `GATE:`
(any case) is forwarded as `STD:` plus the rest (`GATE:OBD` → `STD:OBD`), anything else
unchanged. `dllCheckIFH(compat)` records the compatibility number and forwards.
`dllLockIFH`, `dllUnlockIFH`, `dllShutdownIFH` and `dllExitIFH` are forwarded and logged.

### The latch

A refusal latches: from then until `dllShutdownIFH` or `dllStartupIFH`, every `dllCallIFH`
returns failure without reaching `XStd32.dll`, except 3 ifhGetIfhStatus, 4 ifhGetIfhError,
5 ifhGetResult, 8 ifhEnd and 21 ifhDisconnect, which are still forwarded so that EDIABAS can
read its status and close cleanly.

A refused call returns `-1` and, when the layout is known, sets `msgOut->len` to 0. Which
EDIABAS error that produces is not known yet (see below); the agent detects refusals from the
log, not from EDIABAS's error.

## Modes: `XGate.ini`

Next to the DLL (`C:\EDIABAS\BIN\XGate.ini`), read once when the DLL is first called; see
[`XGate.ini.example`](XGate.ini.example):

    Mode=enforce
    LogPath=C:\EDIABAS\TRACE\XGate.log

- `Mode=enforce` (the default; also with no file, no `Mode` key, a file of 4 KB or more, or any value
  other than `log`; if `Mode` appears more than once, every one must say `log`): refusals stop
  the call and latch.
- `Mode=log`: the same decisions are computed and logged (`WOULD_REFUSE`, `WOULD_LATCH`,
  `verdict=latched judged=…`), but everything is forwarded. Two exceptions: with a
  compatibility number below 5 calls are refused in log mode too (the layout cannot be read
  safely), and if `dllCheckIFH` has not been called yet, log mode forwards and logs only
  `fktNo` and `wParam` (`layout=unknown`), where enforce refuses (`compat`).
- `LogPath` (default `C:\EDIABAS\TRACE\XGate.log`): ANSI path. If it cannot be opened the
  gate still enforces, but the agent will not see its refusals.

## The log

One line per event, local time, CRLF, written straight to the file with no buffering (it is
in the file when the call returns). When a line would take the file past 1 MiB it is renamed
to `<LogPath>.1` (replacing an older one) and a new file is started; if the rename fails
(someone has it open without delete sharing) the file is truncated instead. Readers should
expect the file to shrink, and read `.1` for the rest. Hex is uppercase without spaces; `-` is
no bytes, `(null)` a null pointer with a non-zero length, `+<n>` means `n` more bytes were not
logged (data is capped at 256 bytes, `msgOut` data at 64).

    2026-11-24 10:00:00.000 LOAD XGate32 pid=1234 mode=enforce ini=read rule=v1 inner=XStd32.dll
    2026-11-24 10:00:00.001 CHECK compat=6 fwd=yes rc=1
    2026-11-24 10:00:00.002 STARTUP ini=C:\EDIABAS\BIN\EDIABAS.INI ifh=GATE:OBD as=STD:OBD fwd=yes rc=0
    2026-11-24 10:00:00.010 CALL fkt=27 ifhSetParameter wParam=0 ch=0 len=12 data=0F01… concept=none verdict=allow fwd=yes rc=0 newConcept=010F outLen=0 out=-
    2026-11-24 10:00:00.020 CALL fkt=29 ifhSendTelegram wParam=0 ch=0 len=5 data=8212F11A80 concept=010F verdict=allow fwd=yes rc=0 outLen=3 out=C2F112
    2026-11-24 10:00:00.030 REFUSED service fkt=29 frame=8312F114FFFF
    2026-11-24 10:00:00.030 LATCHED
    2026-11-24 10:00:00.030 CALL fkt=29 ifhSendTelegram wParam=0 ch=0 len=6 data=8312F114FFFF concept=010F verdict=refuse:service fwd=no rc=-1
    2026-11-24 10:00:00.040 CALL fkt=29 ifhSendTelegram wParam=0 ch=0 len=5 data=8212F11A80 concept=010F verdict=latched judged=allow fwd=no rc=-1

| Line | When |
|---|---|
| `LOAD … mode= ini= rule=v1 inner=` | first call into the DLL; `inner=none: every call fails: <why>` if `XStd32.dll` did not load |
| `CHECK compat=<n> fwd= rc=` | `dllCheckIFH`; `layout=unsupported` below 5 |
| `STARTUP ini= ifh= as= fwd= rc=` | `dllStartupIFH`, with the name forwarded (`as=`) |
| `CALL fkt=<n> <name> wParam= ch= len= data= concept= verdict= [judged=] fwd= rc= [newConcept=] [outLen= out=]` | every `dllCallIFH`, after it returns; `layout=unknown` replaces `ch`…`concept` when the layout is not known |
| `REFUSED <reason> fkt=<n> frame=<hex>` then `LATCHED` | a refusal in enforce mode (written before that call's `CALL` line) |
| `WOULD_REFUSE <reason> …` / `WOULD_LATCH` | the same in log mode |
| `SHUTDOWN`, `EXIT eax=`, `LOCK rc=`, `UNLOCK` | the other entry points |
| `PANIC <message>` | a failed safety check inside the gate, just before the process ends |

## Build

Zig 0.16.0.

    cd gate
    zig build test        # the tests, natively (macOS is fine)
    zig build             # zig-out/bin/XGate32.dll (x86-windows-gnu, ReleaseSafe), then checks its exports

`zig build` (and `zig build check-exports` alone) parses the built DLL's PE export table and
fails unless it is an i386 DLL exporting all seven entry points under both names, each pair at
the same address: `_dllCallIFH@8`/`dllCallIFH`, `_dllCheckIFH@4`/`dllCheckIFH`,
`_dllExitIFH@0`/`dllExitIFH`, `_dllLockIFH@0`/`dllLockIFH`, `_dllShutdownIFH@0`/`dllShutdownIFH`,
`_dllStartupIFH@8`/`dllStartupIFH`, `_dllUnlockIFH@0`/`dllUnlockIFH` (all `__stdcall`). The
table also lists Zig's own `_DllMainCRTStartup@12` and TLS symbols (`_tls_index`, …), which
EDIABAS does not look up. The DLL imports only `KERNEL32.dll`.

| File | What |
|---|---|
| `src/rule.zig` | the telegram rule (pure) |
| `src/allowlist_json.zig`, `tools/gen_allowlist.zig`, `src/policy.zig` | `policy/telegrams.json` → compiled-in allow-list |
| `src/gate.zig` | function decisions, concepts, latch, modes, log lines; forwards through an abstract inner IFH |
| `src/ini.zig` | `XGate.ini` |
| `src/dll.zig` | the Windows side: exports, loading `XStd32.dll`, the log file, locking |
| `src/*_test.zig`, `tools/check_exports.zig` | tests: the cases file, a fake inner IFH, the export table |

## Install (in the VM; the owner's call)

1. Copy `zig-out/bin/XGate32.dll` and `XGate.ini.example` (renamed `XGate.ini`) into
   `C:\EDIABAS\BIN`. Nothing there is replaced or renamed; `XStd32.dll` stays as it is.
2. For the first install set `Mode=log` and verify the points below with a read session.
3. Set `Mode=enforce` (or delete the `Mode` line). The setting is read when EDIABAS first
   calls the DLL, so restart the agent (EDIABAS) after changing it.

Uninstall: delete `XGate32.dll`, `XGate.ini` and `XGate.log*` from `C:\EDIABAS\BIN` /
`C:\EDIABAS\TRACE`, and switch the agent back to `STD:OBD`. With the DLL gone, `GATE:OBD`
should fail to open, so the agent does not silently run ungated (check this once too).

## Not verified yet (confirm in log mode before enforcing)

| Assumption | How the log confirms it |
|---|---|
| `apiInitExt("GATE:OBD", …)` makes EDIABAS 7.3.0 load `XGate32.dll` | a `LOAD` line appears at all, from the agent's process (`pid=`); `STARTUP ifh=` shows what EDIABAS passes (`GATE:OBD`, `OBD`, …) and `rc=0`-style success from `XStd32.dll` with `as=STD:OBD` |
| `dllCheckIFH` is called before any `dllCallIFH`, with compatibility 5 or 6 | `CHECK compat=` comes before the first `CALL` (if it does not, enforce refuses everything with `compat`) |
| the `MESSAGE` layout (`fktNo, wParam, reserved, channel, len, data`, pack 4) | `CALL` lines show sensible `fkt=`, `len=` and `data=` (no crashes, no huge lengths) |
| 27 ifhSetParameter: data starts with the concept, UINT16 LE | `CALL fkt=27 … data=0F01…` and `newConcept=010F`, matching `SETECUCOMM` in EDIABAS's `IFH.trc` |
| 29/30 ifhSendTelegram(Freq): data is exactly the telegram | `data=` equals the `SENDECUTEL` input in `IFH.trc`, and reads come out `verdict=allow` |
| 31 and 34 carry either nothing or a telegram | their `len=` and `data=` |
| 54 IfhSetParameterRaw: whether it carries a concept | its `data=` next to the 27 lines (the gate ignores it for the concept) |
| the channel field: one channel per session, set before the telegrams on it | `ch=` on the 27 lines matches `ch=` on the 29 lines (else telegrams show `concept=none` and `judged=refuse:no_concept`) |
| a refused call's return (`-1`, `msgOut->len = 0`) gives a clean EDIABAS error, not a hang | in enforce mode, run a job the policy denies with the gate's help: note the EDIABAS error the agent reports, and that `ifhEnd`/`ifhDisconnect` still go through after `LATCHED` |
| `dllExitIFH`'s signature (taken as no arguments; the return register is passed through) | `EXIT eax=` is logged and EDIABAS unloads without an error |
| which function numbers a real read session uses | the `fkt=` values in a log-mode session: any `WOULD_REFUSE function` there needs a decision before enforcing |

## Licence

MIT, like the rest of `main`. Written from the IFH ABI facts (function numbers, signatures,
`MESSAGE` layout) only; no EdiabasLib (GPL-3.0) code is used.
