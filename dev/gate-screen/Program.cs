using System.Text.Json;
using EdiabasAgent.Core;
using EdiabasLib;
using EdiabasLib.Native;

namespace GateScreen;

/// <summary>
/// <code>
/// gate-screen --ecu ~/ediabas-ecu/Ecu --out ~/m2/gate-screen.json [--refused dev/m2/gate-refused.json] [--with-args SGBD:JOB]... SGBD...
/// </code>
/// M2 task 7b. For each SGBD: every job the policy allows (by name) that takes no argument is
/// run through EdiabasLib and KlineInterface on an empty libkline replay, so nothing reaches a
/// device. The first telegram the job sends meets libkline's frame gate: refused, or let
/// through (the empty replay then fails the job), or nothing was sent. EdiabasNet runs the
/// SGBD's INITIALISIERUNG before the first job; when that sends, every job's first telegram is
/// INITIALISIERUNG's and the SGBD is reported as not screened past it. A group name is
/// screened by its identification only. Output: a JSON report, and optionally the refused
/// (ECU, job) pairs for dev/m2/gate-refused.json. Names and service bytes only, no SGBD content.
/// </summary>
public static class Program
{
    sealed record Probe(string Outcome, string? Frame, string? Error);

    sealed record JobRow(string Job, string Outcome, string? Frame, string? Service);

    sealed record SgbdReport(
        string Sgbd,
        int Jobs,
        int Allowed,
        int WithArguments,
        Probe? Init,
        bool InitSends,
        string? Error,
        List<JobRow> Screened);

    public static int Main(string[] args)
    {
        string? ecu = null, outPath = null, refusedPath = null;
        var sgbds = new List<string>();
        var extra = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ecu": ecu = Expand(args[++i]); break;
                case "--out": outPath = Expand(args[++i]); break;
                case "--refused": refusedPath = args[++i]; break;
                case "--with-args": extra.Add(args[++i]); break;
                default: sgbds.Add(args[i]); break;
            }
        }

        if (ecu == null || outPath == null || sgbds.Count == 0)
        {
            Console.Error.WriteLine("usage: gate-screen --ecu DIR --out FILE.json [--refused FILE.json] [--with-args SGBD:JOB]... SGBD...");
            return 2;
        }

        var empty = Path.Combine(Path.GetTempPath(), $"gate-screen-empty-{Environment.ProcessId}.jsonl");
        File.WriteAllText(empty, "");
        try
        {
            var policy = JobPolicy.Embedded;
            var reports = sgbds.Select(s => Screen(ecu, s, policy, empty, extra)).ToList();
            var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            File.WriteAllText(outPath, JsonSerializer.Serialize(new { ecuPath = ecu, policyVersion = policy.Version, sgbds = reports }, json));
            foreach (var r in reports)
            {
                var refused = r.Screened.Count(j => j.Outcome == "refused");
                Console.WriteLine($"{r.Sgbd}: {r.Jobs} jobs, {r.Allowed} allowed, {r.WithArguments} with arguments, {r.Screened.Count} run, {refused} refused" +
                    (r.Error != null ? $"; {r.Error}" : r.InitSends ? $"; INITIALISIERUNG sends first ({r.Init?.Outcome})" : ""));
            }

            if (refusedPath != null)
            {
                var refused = reports
                    .Where(r => !r.InitSends)
                    .SelectMany(r => r.Screened.Where(j => j.Outcome == "refused").Select(j => new { ecu = r.Sgbd, job = j.Job, service = j.Service }))
                    .ToList();
                var unscreened = reports
                    .Where(r => r.InitSends || r.Error != null)
                    .Select(r => new { ecu = r.Sgbd, reason = r.Error ?? $"INITIALISIERUNG sends first (gate: {r.Init?.Outcome}, frame {r.Init?.Frame})" })
                    .ToList();
                File.WriteAllText(refusedPath, JsonSerializer.Serialize(new { policyVersion = policy.Version, refused, unscreened }, json) + "\n");
            }

            return 0;
        }
        finally
        {
            File.Delete(empty);
        }
    }

    static string Expand(string p) => p.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..])
        : p;

    /// <param name="extra">SGBD:JOB pairs to run with no arguments even though they declare some
    /// (optional arguments, as the E60 functional jobs have).</param>
    static SgbdReport Screen(string ecuPath, string sgbd, JobPolicy policy, string empty, ISet<string> extra)
    {
        List<string> jobs;
        try
        {
            jobs = VirtualList(ecuPath, sgbd, "_JOBS", null, "JOBNAME", empty);
        }
        catch (Exception ex)
        {
            // A group file's _JOBS needs its variant, which needs the module: screen its identification.
            var ident = Run(ecuPath, sgbd, null, empty);
            return new SgbdReport(sgbd, 0, 0, 0, ident, ident.Outcome != "nothing_sent", $"group or unreadable: _JOBS failed ({Short(ex)}); its identification telegram: {ident.Outcome}", []);
        }

        var hasInit = jobs.Any(j => j.Equals("INITIALISIERUNG", StringComparison.OrdinalIgnoreCase));
        var init = hasInit ? Run(ecuPath, sgbd, "INITIALISIERUNG", empty) : null;
        var initSends = init != null && init.Outcome != "nothing_sent";
        int allowed = 0, withArgs = 0;
        var rows = new List<JobRow>();
        foreach (var job in jobs)
        {
            if (job.StartsWith('_') || job.Equals("INITIALISIERUNG", StringComparison.OrdinalIgnoreCase)) continue;
            if (!policy.Evaluate(sgbd, job).Allowed) continue;
            allowed++;
            List<string> argNames;
            try
            {
                argNames = VirtualList(ecuPath, sgbd, "_ARGUMENTS", job, "ARG", empty);
            }
            catch
            {
                argNames = ["(unknown)"];
            }

            if (argNames.Count > 0 && !extra.Contains($"{sgbd}:{job}"))
            {
                withArgs++;
                continue;
            }

            if (initSends) continue;
            var p = Run(ecuPath, sgbd, job, empty);
            rows.Add(new JobRow(job, p.Outcome, p.Frame, ServiceOf(p.Frame)));
        }

        return new SgbdReport(sgbd, jobs.Count(j => !j.StartsWith('_')), allowed, withArgs, init, initSends, null, rows);
    }

    /// A virtual job's result column, one value per set (set 0 is the system set).
    static List<string> VirtualList(string ecuPath, string sgbd, string vjob, string? arg, string column, string empty)
    {
        using var t = new Capture(empty);
        using var ed = EdiabasFactory.Create(ecuPath, t.Iface, noInitForVJobs: true);
        EdiabasFactory.Activate(ed);
        ed.ArgBinary = null;
        ed.ArgBinaryStd = null;
        if (arg != null) ed.ArgString = arg;
        ed.ResolveSgbdFile(sgbd);
        ed.ExecuteJob(vjob);
        if (t.FirstFrame != null) throw new InvalidOperationException($"{vjob} sent a telegram");
        return ed.ResultSets.Skip(1)
            .Select(set => set.TryGetValue(column, out var rd) ? rd.OpData as string : null)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToList();
    }

    /// One job (or, with job null, only the SGBD's resolution) on a fresh instance.
    static Probe Run(string ecuPath, string sgbd, string? job, string empty)
    {
        var t = new Capture(empty);
        string? error = null;
        using (var ed = EdiabasFactory.Create(ecuPath, t.Iface, noInitForVJobs: true))
        {
            try
            {
                EdiabasFactory.Activate(ed);
                ed.ArgBinary = null;
                ed.ArgBinaryStd = null;
                ed.ResolveSgbdFile(sgbd);
                if (job != null) ed.ExecuteJob(job);
            }
            catch (Exception ex)
            {
                error = Short(ex);
            }

            var reason = t.Iface.Transport.LastReason;
            var outcome = reason == KlineReason.GateRefused ? "refused"
                : t.FirstFrame != null ? "let_through"
                : "nothing_sent";
            return new Probe(outcome, t.FirstFrame == null ? null : Convert.ToHexString(t.FirstFrame), error);
        }
    }

    /// The service byte of a KWP2000-style frame (format byte, target, source, [length], service).
    static string? ServiceOf(string? frameHex)
    {
        if (frameHex == null) return null;
        var b = Convert.FromHexString(frameHex);
        if (b.Length < 4 || (b[0] & 0x80) == 0) return null;
        var i = (b[0] & 0x3F) == 0 ? 4 : 3;
        return i < b.Length ? b[i].ToString("X2") : null;
    }

    static string Short(Exception ex) => ex.Message.Split('\n')[0];

    /// KlineInterface on an empty replay, keeping the first telegram EdiabasLib hands to libkline.
    sealed class Capture : IDisposable
    {
        public Capture(string empty)
        {
            Iface = new KlineInterface(KlineTransport.Replay(empty)) { ComPort = "replay" };
            var send = Iface.InterfaceSendDataFunc;
            Iface.InterfaceSendDataFunc = (data, length, setDtr, corr) =>
            {
                FirstFrame ??= data.Take(length).ToArray();
                return send(data, length, setDtr, corr);
            };
        }

        public KlineInterface Iface { get; }

        public byte[]? FirstFrame { get; private set; }

        // The interface is disposed with its EdiabasNet; this only closes the transport.
        public void Dispose() => Iface.Transport.Dispose();
    }
}
