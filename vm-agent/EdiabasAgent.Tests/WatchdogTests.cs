using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// <summary>Issue #15: when the agent's watchdog ends the process, and when it must not.</summary>
public class WatchdogTests
{
    private const long Stall = 190_000;
    private static string Where() => "inside apiState for 190 s";

    /// <summary>Ticks every <see cref="WatchdogState.TickMs"/> from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static List<WatchdogCheck> Run(WatchdogState s, long from, long to, long? busySince, bool listening = true)
    {
        var checks = new List<WatchdogCheck>();
        for (var t = from; t <= to; t += WatchdogState.TickMs)
        {
            checks.Add(s.Tick(t, busySince, listening, Where));
        }

        return checks;
    }

    [Fact]
    public void StallLimit_CoversBudgetGraceAndMargin()
    {
        Assert.Equal(60_000 + EdiabasService.BreakGraceMs + 120_000, WatchdogState.RunnerStallMsFor(60));
        Assert.Equal(1_000 + EdiabasService.BreakGraceMs + 120_000, WatchdogState.RunnerStallMsFor(0));
    }

    [Fact]
    public void IdleOrShortRequests_NeverExit()
    {
        var s = new WatchdogState(Stall, 0);
        Assert.All(Run(s, 5_000, 600_000, null), c => Assert.Null(c.ExitReason));

        // A long but legal request.
        Assert.All(Run(s, 605_000, 600_000 + Stall - 5_000, 600_000), c => Assert.Null(c.ExitReason));
    }

    [Fact]
    public void RunnerHeldPastTheLimit_Exits_SayingWhere()
    {
        var s = new WatchdogState(Stall, 0);
        var checks = Run(s, 5_000, 300_000, 10_000);

        var first = checks.First(c => c.ExitReason is not null);
        Assert.Contains("190 s", first.ExitReason);
        Assert.Contains("inside apiState", first.ExitReason);
        Assert.Equal(Stall, checks.IndexOf(first) * WatchdogState.TickMs + 5_000 - 10_000);
    }

    [Fact]
    public void PauseBetweenTicks_RestartsTheClocks()
    {
        // A request was running when the VM paused for five hours.
        var s = new WatchdogState(Stall, 0);
        Run(s, 5_000, 60_000, 10_000);
        var wake = s.Tick(60_000 + 5 * 3600_000, 10_000, true, Where);

        Assert.Null(wake.ExitReason);
        Assert.Contains("passed between two checks", wake.Note);

        // Counted again from the wake-up: still stuck after the limit means stuck.
        var after = Run(s, 60_000 + 5 * 3600_000 + 5_000, 60_000 + 5 * 3600_000 + Stall + 5_000, 10_000);
        Assert.Contains(after, c => c.ExitReason is not null);
    }

    [Fact]
    public void Probe_IsDueEveryMinute_OnlyWhileListening()
    {
        var s = new WatchdogState(Stall, 0);
        var due = Run(s, 5_000, 180_000, null).Count(c => c.ProbeDue);
        Assert.Equal(3, due);

        var notListening = new WatchdogState(Stall, 0);
        Assert.DoesNotContain(Run(notListening, 5_000, 180_000, null, listening: false), c => c.ProbeDue);
    }

    [Fact]
    public void ThreeMissedProbesInARow_Exit_AnAnswerResets()
    {
        var s = new WatchdogState(Stall, 0);
        Assert.Null(s.ProbeResult(false, "no answer within 10 s"));
        Assert.Null(s.ProbeResult(false, "no answer within 10 s"));
        Assert.Null(s.ProbeResult(true, null));
        Assert.Null(s.ProbeResult(false, "x"));
        Assert.Null(s.ProbeResult(false, "x"));
        var exit = s.ProbeResult(false, "socket error ConnectionRefused");

        Assert.NotNull(exit);
        Assert.Contains("3 checks in a row", exit);
        Assert.Contains("ConnectionRefused", exit);
    }

    [Fact]
    public void Rebinding_ForgetsEarlierMisses()
    {
        var s = new WatchdogState(Stall, 0);
        s.ProbeResult(false, "x");
        s.ProbeResult(false, "x");
        s.Tick(5_000, null, listening: false, Where);

        Assert.Equal(0, s.ProbeMisses);
        Assert.Null(s.ProbeResult(false, "x"));
    }

    [Fact]
    public void NativeCallTracker_SaysWhereTheRunnerIs()
    {
        long now = 0;
        var t = new NativeCallTracker(() => now);
        Assert.Equal("no api32 call made yet", t.Describe());

        t.Enter("apiState");
        now = 183_000;
        Assert.Equal("inside apiState for 183 s", t.Describe());

        t.Exit();
        now = 195_000;
        Assert.Equal("last api32 call apiState, returned 12 s ago", t.Describe());
    }
}
