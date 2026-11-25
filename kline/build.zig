const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    const telegram = telegramPolicy(b);

    const mod = b.addModule("kline", .{
        .root_source_file = b.path("src/root.zig"),
        .target = target,
        .optimize = optimize,
        .link_libc = true,
    });
    telegram.attach(mod);
    if (target.result.os.tag == .macos) {
        mod.linkFramework("IOKit", .{});
        mod.linkFramework("CoreFoundation", .{});
    }

    const lib = b.addLibrary(.{
        .name = "kline",
        .linkage = .static,
        .root_module = mod,
    });
    b.installArtifact(lib);

    // The C ABI (include/kline.h) as a shared library, for ediabasd's P/Invoke.
    const capi_mod = b.createModule(.{
        .root_source_file = b.path("src/capi.zig"),
        .target = target,
        .optimize = optimize,
        .link_libc = true,
        .imports = &.{.{ .name = "kline", .module = mod }},
    });
    const shared = b.addLibrary(.{
        .name = "kline",
        .linkage = .dynamic,
        .root_module = capi_mod,
    });
    b.installArtifact(shared);

    const exe = b.addExecutable(.{
        .name = "kline",
        .root_module = b.createModule(.{
            .root_source_file = b.path("src/cli.zig"),
            .target = target,
            .optimize = optimize,
            .link_libc = true,
            .imports = &.{.{ .name = "kline", .module = mod }},
        }),
    });
    b.installArtifact(exe);

    const run_cmd = b.addRunArtifact(exe);
    if (b.args) |args| run_cmd.addArgs(args);
    b.step("run", "Run kline").dependOn(&run_cmd.step);

    const tests = b.addTest(.{ .root_module = mod });
    const run_tests = b.addRunArtifact(tests);
    const exe_tests = b.addTest(.{ .root_module = exe.root_module });
    const run_exe_tests = b.addRunArtifact(exe_tests);
    const test_step = b.step("test", "Run unit tests");
    test_step.dependOn(&run_tests.step);
    test_step.dependOn(&run_exe_tests.step);
    const capi_tests = b.addTest(.{ .root_module = capi_mod });
    test_step.dependOn(&b.addRunArtifact(capi_tests).step);

    // Compiles (does not run) the tests for Linux. The constant tests check every hand-declared
    // value at compile time against the kernel headers Zig ships, so this verifies the Linux
    // ioctls from a Mac.
    const check_linux = b.step("check-linux", "Type-check the tests for x86_64 and aarch64 Linux");
    for ([_]std.Target.Cpu.Arch{ .x86_64, .aarch64 }) |arch| {
        const linux_target = b.resolveTargetQuery(.{ .cpu_arch = arch, .os_tag = .linux, .abi = .gnu });
        const linux_mod = b.createModule(.{
            .root_source_file = b.path("src/root.zig"),
            .target = linux_target,
            .optimize = optimize,
            .link_libc = true,
        });
        telegram.attachFor(linux_mod, linux_target, optimize);
        const linux_tests = b.addTest(.{ .root_module = linux_mod });
        check_linux.dependOn(&linux_tests.step);
    }
}

/// The frame gate's rule and allow-list come from the shared telegram rule: gate/src/policy.zig
/// (which re-exports gate/src/rule.zig) and policy/telegrams.json, turned into Zig source at
/// build time by gate's own generator, as XGate32.dll is built. One rule, one list.
const TelegramPolicy = struct {
    b: *std.Build,
    allowlist_zig: std.Build.LazyPath,
    cases: std.Build.LazyPath,

    fn attach(self: TelegramPolicy, m: *std.Build.Module) void {
        self.attachFor(m, m.resolved_target.?, m.optimize.?);
    }

    fn attachFor(self: TelegramPolicy, m: *std.Build.Module, target: std.Build.ResolvedTarget, optimize: std.builtin.OptimizeMode) void {
        const b = self.b;
        const allowlist = b.createModule(.{ .root_source_file = self.allowlist_zig, .target = target, .optimize = optimize });
        const policy = b.createModule(.{
            .root_source_file = b.path("../gate/src/policy.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{.{ .name = "allowlist", .module = allowlist }},
        });
        m.addImport("telegram_policy", policy);
        m.addAnonymousImport("telegram_cases", .{ .root_source_file = self.cases });
    }
};

fn telegramPolicy(b: *std.Build) TelegramPolicy {
    const host = b.graph.host;
    const telegrams_json = b.path("../policy/telegrams.json");
    const allowlist_json = b.createModule(.{ .root_source_file = b.path("../gate/src/allowlist_json.zig"), .target = host });
    allowlist_json.addAnonymousImport("telegrams_json", .{ .root_source_file = telegrams_json });
    const gen = b.addExecutable(.{
        .name = "gen_allowlist",
        .root_module = b.createModule(.{
            .root_source_file = b.path("../gate/tools/gen_allowlist.zig"),
            .target = host,
            .imports = &.{.{ .name = "allowlist_json", .module = allowlist_json }},
        }),
    });
    gen.root_module.addAnonymousImport("telegrams_json", .{ .root_source_file = telegrams_json });
    const run = b.addRunArtifact(gen);
    run.expectExitCode(0);
    return .{ .b = b, .allowlist_zig = run.captureStdOut(.{ .basename = "allowlist.zig" }), .cases = b.path("../policy/telegram-cases.json") };
}
