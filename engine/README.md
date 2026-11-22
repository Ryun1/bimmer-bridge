# engine

The native engine, stage 1 (GPL-3.0): EdiabasLib, linked unmodified from the pinned submodule
`third_party/ediabaslib`, on the Zig cable layer `kline/` (`libkline`), behind the same service
and policy as the VM agent (`vm-agent/EdiabasAgent.Core`).

| Project | What it is |
|---|---|
| `EdiabasLib.Native` | EdiabasLib's interpreter and `EdInterfaceObd`, with `KlineInterface` binding its transport to `libkline`; `OfflineInterface` and `CaptureInterface` open nothing |
| `ediabasd-net` | `ediabasd`: the native `IEdiabasApi`, its config, the JSON-lines stdio front and the CLI (docs/api.md, "`ediabasd` stdio") |

Building runs `zig build` in `kline/` and copies `libkline` beside the assembly (Zig 0.16;
`/opt/homebrew/bin/zig` if present, else `zig` on the PATH; override with `-p:ZigExe=…`).

```sh
export DOTNET_ROOT=/opt/homebrew/opt/dotnet@10/libexec
DOTNET=/opt/homebrew/opt/dotnet@10/bin/dotnet
$DOTNET test engine/EdiabasLib.Native.Tests
$DOTNET test engine/ediabasd-net.Tests
$DOTNET run --project engine/ediabasd-net -- describe NVE_65
```

Tests use upstream's test SGBDs in place (`cmd_test1`, `cmd_test2`, `base1`, `base2`,
`cmd_ident` only) and never open a real cable: they use replay recordings, fakes, or a device
path that does not exist. Never run `ediabasd job`, `jobs` or `vehicle` with the cable in a car
unless you mean to read that car.
