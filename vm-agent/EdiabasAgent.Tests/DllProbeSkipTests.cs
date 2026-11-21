using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class DllProbeSkipTests
{
    [Fact]
    public void OnlyTheAgent_IsNotCalledABmwTool()
    {
        var reason = DllProbeSkip.Reason([new ConflictingProcess("EdiabasAgent", 4711)], 20);

        Assert.DoesNotContain("BMW tool", reason, StringComparison.Ordinal);
        Assert.Contains("the running agent has api32.dll loaded itself (EdiabasAgent pid 4711)", reason, StringComparison.Ordinal);
        Assert.Contains("after 20 s without an EDIABAS request", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentNameMatch_IsCaseInsensitive()
    {
        var reason = DllProbeSkip.Reason([new ConflictingProcess("EDIABASAGENT", 1)], 30);
        Assert.Contains("running agent", reason, StringComparison.Ordinal);
        Assert.Contains("after 30 s", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ABmwTool_IsNamed_WithoutTheAgent()
    {
        var reason = DllProbeSkip.Reason(
            [new ConflictingProcess("EdiabasAgent", 1), new ConflictingProcess("INPALOAD", 4312)], 20);

        Assert.Equal("not loaded because a BMW tool is running (INPALOAD pid 4312); close it and run the self-test again", reason);
    }

    [Fact]
    public void OnlyBmwTools_KeepsTheOldWording()
    {
        var reason = DllProbeSkip.Reason([new ConflictingProcess("INPALOAD", 4312), new ConflictingProcess("TOOL32", 9)], 20);
        Assert.Equal("not loaded because a BMW tool is running (INPALOAD pid 4312, TOOL32 pid 9); close it and run the self-test again", reason);
    }
}
