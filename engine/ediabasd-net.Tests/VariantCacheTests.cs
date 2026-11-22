using System.Security.Cryptography;
using EdiabasAgent.Core;
using EdiabasLib.Native.Tests;

namespace Ediabasd.Tests;

public sealed class VariantCacheTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("variants-").FullName;
    readonly Recordings _rec = new();

    public void Dispose()
    {
        _rec.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    string CachePath => Path.Combine(_dir, "state", "variants.json");

    [Fact]
    public void StoreThenLookup()
    {
        var c = new VariantCache(CachePath);
        c.Store("d_motor", "aa", "N62_TUE2");
        Assert.Equal("N62_TUE2", c.Lookup("D_MOTOR", "aa"));
    }

    [Fact]
    public void LookupWithAnotherHash_IsNull()
    {
        var c = new VariantCache(CachePath);
        c.Store("D_MOTOR", "aa", "N62_TUE2");
        Assert.Null(c.Lookup("D_MOTOR", "bb"));
    }

    [Fact]
    public void Reload_FromDisk()
    {
        new VariantCache(CachePath).Store("D_MOTOR", "aa", "N62_TUE2");
        Assert.Equal("N62_TUE2", new VariantCache(CachePath).Lookup("D_MOTOR", "aa"));
    }

    [Fact]
    public void CorruptFile_ReadsEmpty_AndIsReplacedOnStore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, "{not json");
        var c = new VariantCache(CachePath);
        Assert.Null(c.Lookup("D_MOTOR", "aa"));
        c.Store("D_MOTOR", "aa", "N62_TUE2");
        Assert.Equal("N62_TUE2", new VariantCache(CachePath).Lookup("D_MOTOR", "aa"));
    }

    static string GrpHash() =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(TestPaths.UpstreamTestEcu, "cmd_ident.grp")))).ToLowerInvariant();

    static int Wait(NativeEdiabasApi api)
    {
        var until = Environment.TickCount64 + 10_000;
        int s;
        while ((s = api.State()) == ApiState.Busy)
        {
            if (Environment.TickCount64 > until) throw new TimeoutException();
            Thread.Sleep(5);
        }
        return s;
    }

    [Fact]
    public void GroupDescribe_NoEntry_NeedsCar_NoCable()
    {
        var cables = new CableFactory(() => LiveCable.Replay(_rec.Empty()));
        using var api = new NativeEdiabasApi(TestPaths.UpstreamTestEcu, cables.Make, new VariantCache(CachePath), null) { Mode = RequestMode.Offline };
        Assert.True(api.Init());
        api.Job("cmd_ident", "_JOBS", "", "");
        Assert.Equal(ApiState.Error, Wait(api));
        Assert.Equal(NativeErrorCodes.NeedsCar, api.ErrorCode());
        Assert.Equal(0, cables.Calls);
    }

    [Fact]
    public void GroupDescribe_WithEntry_UsesVariant()
    {
        var cache = new VariantCache(CachePath);
        // Deliberately not what the group really resolves to (cmd_test1), to show the cache decides.
        cache.Store("CMD_IDENT", GrpHash(), "cmd_test2");
        var cables = new CableFactory(() => LiveCable.Replay(_rec.Empty()));
        using var api = new NativeEdiabasApi(TestPaths.UpstreamTestEcu, cables.Make, cache, null) { Mode = RequestMode.Offline };
        Assert.True(api.Init());
        api.Job("cmd_ident", "_JOBS", "", "");
        Assert.Equal(ApiState.Ready, Wait(api));
        Assert.True(api.ResultSets(out var n));
        var names = Enumerable.Range(1, n).Select(s => api.ResultText("JOBNAME", s, out var j) ? j : null).ToList();
        Assert.Contains("TEST_IFACE_FLAGS", names);
        Assert.Equal(0, cables.Calls);
    }

    [Fact]
    public void GroupEntry_WithChangedHash_NeedsCar()
    {
        var cache = new VariantCache(CachePath);
        cache.Store("CMD_IDENT", "0000", "cmd_test2");
        using var api = new NativeEdiabasApi(TestPaths.UpstreamTestEcu, () => LiveCable.Replay(_rec.Empty()), cache, null);
        Assert.True(api.Init());
        api.Job("cmd_ident", "_JOBS", "", "");
        Assert.Equal(ApiState.Error, Wait(api));
        Assert.Equal(NativeErrorCodes.NeedsCar, api.ErrorCode());
    }

    [Fact]
    public void GroupLive_ResolvesAndStores()
    {
        var cache = new VariantCache(CachePath);
        var cables = new CableFactory(() => LiveCable.Replay(_rec.Empty()));
        using var api = new NativeEdiabasApi(TestPaths.UpstreamTestEcu, cables.Make, cache, null) { Mode = RequestMode.Live };
        Assert.True(api.Init());
        api.Job("cmd_ident", "_JOBS", "", "");
        Assert.Equal(ApiState.Ready, Wait(api));
        Assert.Equal(1, cables.Calls);
        Assert.Equal("cmd_test1", cache.Lookup("CMD_IDENT", GrpHash()));
    }

    [Fact]
    public void LiveJobOnGroup_StoresVariant()
    {
        var cache = new VariantCache(CachePath);
        using var api = new NativeEdiabasApi(TestPaths.UpstreamTestEcu, () => LiveCable.Replay(_rec.Empty()), cache, null) { Mode = RequestMode.Live };
        Assert.True(api.Init());
        api.Job("cmd_ident", "TEST_MATH", "", "");
        Assert.Equal(ApiState.Ready, Wait(api));
        Assert.Equal("cmd_test1", cache.Lookup("CMD_IDENT", GrpHash()));
    }

    [Fact]
    public void PrgName_IsNotAGroup()
    {
        using var api = new NativeEdiabasApi(TestPaths.UpstreamTestEcu, () => LiveCable.Replay(_rec.Empty()), new VariantCache(CachePath), null);
        Assert.True(api.Init());
        api.Job("CMD_TEST2", "_JOBS", "", "");
        Assert.Equal(ApiState.Ready, Wait(api));
    }
}
