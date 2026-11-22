const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    const mod = b.addModule("kline", .{
        .root_source_file = b.path("src/root.zig"),
        .target = target,
        .optimize = optimize,
        .link_libc = true,
    });
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
        const linux_tests = b.addTest(.{ .root_module = linux_mod });
        check_linux.dependOn(&linux_tests.step);
    }
}
