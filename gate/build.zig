//! zig build             XGate32.dll (x86-windows-gnu) in zig-out/bin, then check its exports
//! zig build test        the tests, natively (rule cases, gate, ini, allow-list parser)
//! zig build check-exports   only the export-table check
//!
//! The allow-list's single source is ../policy/telegrams.json: a build-time generator
//! validates it strictly and turns it into Zig source compiled into the DLL. A bad file fails
//! the build.

const std = @import("std");

pub fn build(b: *std.Build) void {
    const optimize = b.option(std.builtin.OptimizeMode, "optimize", "DLL optimisation (default ReleaseSafe: safety checks stay on)") orelse .ReleaseSafe;
    const host = b.graph.host;

    const telegrams_json = b.path("../policy/telegrams.json");
    const cases_json = b.path("../policy/telegram-cases.json");

    // Build-time generator: policy/telegrams.json → allowlist.zig.
    const allowlist_json_mod = b.createModule(.{
        .root_source_file = b.path("src/allowlist_json.zig"),
        .target = host,
    });
    allowlist_json_mod.addAnonymousImport("telegrams_json", .{ .root_source_file = telegrams_json });
    const gen = b.addExecutable(.{
        .name = "gen_allowlist",
        .root_module = b.createModule(.{
            .root_source_file = b.path("tools/gen_allowlist.zig"),
            .target = host,
            .imports = &.{.{ .name = "allowlist_json", .module = allowlist_json_mod }},
        }),
    });
    gen.root_module.addAnonymousImport("telegrams_json", .{ .root_source_file = telegrams_json });
    const gen_run = b.addRunArtifact(gen);
    gen_run.expectExitCode(0);
    const allowlist_zig = gen_run.captureStdOut(.{ .basename = "allowlist.zig" });

    // The DLL.
    const x86_windows = b.resolveTargetQuery(.{ .cpu_arch = .x86, .os_tag = .windows, .abi = .gnu });
    const dll_mod = b.createModule(.{
        .root_source_file = b.path("src/dll.zig"),
        .target = x86_windows,
        .optimize = optimize,
        .strip = true,
    });
    dll_mod.addAnonymousImport("allowlist", .{ .root_source_file = allowlist_zig });
    const dll = b.addLibrary(.{
        .name = "XGate32",
        .linkage = .dynamic,
        .root_module = dll_mod,
    });
    const install_dll = b.addInstallArtifact(dll, .{ .implib_dir = .disabled, .pdb_dir = .disabled });
    b.getInstallStep().dependOn(&install_dll.step);

    // Export-table check, run natively on the built DLL.
    const check_mod = b.createModule(.{
        .root_source_file = b.path("tools/check_exports.zig"),
        .target = host,
    });
    check_mod.addAnonymousImport("xgate_dll", .{ .root_source_file = dll.getEmittedBin() });
    const check = b.addRunArtifact(b.addExecutable(.{ .name = "check_exports", .root_module = check_mod }));
    check.has_side_effects = true;
    const check_step = b.step("check-exports", "Check the export table of XGate32.dll");
    check_step.dependOn(&check.step);
    b.getInstallStep().dependOn(&check.step);

    // Native tests.
    const test_mod = b.createModule(.{
        .root_source_file = b.path("src/tests.zig"),
        .target = host,
        .optimize = .Debug,
    });
    test_mod.addAnonymousImport("allowlist", .{ .root_source_file = allowlist_zig });
    test_mod.addAnonymousImport("telegram_cases_json", .{ .root_source_file = cases_json });
    test_mod.addAnonymousImport("telegrams_parse_cases_json", .{ .root_source_file = b.path("../policy/telegrams-parse-cases.json") });
    test_mod.addAnonymousImport("telegrams_json", .{ .root_source_file = telegrams_json });
    const tests = b.addRunArtifact(b.addTest(.{ .root_module = test_mod }));
    tests.has_side_effects = true;
    const test_step = b.step("test", "Run the tests natively");
    test_step.dependOn(&tests.step);

    // The DLL must also compile with the tests (it is not run).
    test_step.dependOn(&dll.step);
}
