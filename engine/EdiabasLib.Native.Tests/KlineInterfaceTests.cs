using EdiabasLib.Native;

namespace EdiabasLib.Native.Tests;

/// EdInterfaceObd driven through its public transport setters, bound to libkline. Every test
/// uses a replay recording or a device path that does not exist: nothing opens a real cable.
public class KlineInterfaceTests : IDisposable
{
    static readonly byte[] Ident = [0x82, 0x12, 0xF1, 0x1A, 0x80];
    static readonly byte[] IdentResponse = [0x82, 0xF1, 0x12, 0x5A, 0x80];
    static readonly byte[] ClearFaults = [0x83, 0x12, 0xF1, 0x14, 0xFF, 0xFF];

    readonly Recordings _rec = new();

    public void Dispose() => _rec.Dispose();

    static (EdiabasNet Ed, KlineInterface Iface) Create(KlineTransport transport, string port = "replay")
    {
        var iface = new KlineInterface(transport) { ComPort = port };
        var ed = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, iface, noInitForVJobs: true);
        return (ed, iface);
    }

    /// TransmitData as BEST's xsend calls it: room for the checksum, which EdiabasLib fills in.
    static bool Transmit(KlineInterface iface, byte[] request, out byte[] response)
    {
        var buf = new byte[request.Length + 1];
        request.CopyTo(buf, 0);
        try
        {
            return iface.TransmitData(buf, out response);
        }
        catch (EdiabasNet.EdiabasNetException)
        {
            response = null;
            return false;
        }
    }

    [Fact]
    public void TransmitData_BmwFastIdent_ReturnsResponse()
    {
        var (ed, iface) = Create(KlineTransport.Replay(_rec.BmwFastExchange(Ident, IdentResponse)));
        using (ed)
        {
            Assert.True(iface.InterfaceConnect());
            iface.CommParameter = (uint[])EdInterfaceBase.CommParameterBmwFast.Clone();
            Assert.True(Transmit(iface, Ident, out var response), "TransmitData failed: " + iface.Transport.LastReason);
            Assert.Equal(Recordings.WithChecksum(IdentResponse), response.Take(IdentResponse.Length + 1).ToArray());
            Assert.Equal(KlineReason.Ok, iface.Transport.LastReason);
        }
    }

    [Fact]
    public void TransmitData_ClearFaults_RefusedAndLatched()
    {
        var (ed, iface) = Create(KlineTransport.Replay(_rec.BmwFastExchange(Ident, IdentResponse)));
        using (ed)
        {
            Assert.True(iface.InterfaceConnect());
            iface.CommParameter = (uint[])EdInterfaceBase.CommParameterBmwFast.Clone();
            Assert.False(Transmit(iface, ClearFaults, out _));
            Assert.Equal(KlineReason.GateRefused, iface.Transport.LastReason);
            Assert.False(Transmit(iface, Ident, out _));
            Assert.Equal(KlineReason.GateRefused, iface.Transport.LastReason);
        }
    }

    [Fact]
    public void TransmitData_NoConcept_Refused()
    {
        // A plain EdInterfaceObd bound to the transport never forwards the SGBD's concept, so
        // libkline has none declared and refuses the send, whatever EdiabasLib was told.
        var transport = KlineTransport.Replay(_rec.BmwFastExchange(Ident, IdentResponse));
        var iface = new EdInterfaceObd { ComPort = "replay" };
        transport.Bind(iface);
        using var ed = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, iface, noInitForVJobs: true);
        Assert.True(iface.InterfaceConnect());
        iface.CommParameter = (uint[])EdInterfaceBase.CommParameterBmwFast.Clone();
        var buf = new byte[Ident.Length + 1];
        Ident.CopyTo(buf, 0);
        bool ok;
        try { ok = iface.TransmitData(buf, out _); }
        catch (EdiabasNet.EdiabasNetException) { ok = false; }
        Assert.False(ok);
        Assert.Equal(KlineReason.GateRefused, transport.LastReason);
    }

    [Fact]
    public void Connect_MissingDevice_ReasonNoDevice()
    {
        var (ed, iface) = Create(KlineTransport.Device(null), "/dev/does-not-exist");
        using (ed)
        {
            bool connected;
            try { connected = iface.InterfaceConnect(); }
            catch (EdiabasNet.EdiabasNetException) { connected = false; }
            Assert.False(connected);
            Assert.Equal(KlineReason.NoDevice, iface.Transport.LastReason);
        }
    }

    [Fact]
    public void Cancel_FailsTheSend()
    {
        var (ed, iface) = Create(KlineTransport.Replay(_rec.BmwFastExchange(Ident, null)));
        using (ed)
        {
            Assert.True(iface.InterfaceConnect());
            iface.CommParameter = (uint[])EdInterfaceBase.CommParameterBmwFast.Clone();
            iface.Transport.Cancel();
            var start = DateTime.UtcNow;
            Assert.False(Transmit(iface, Ident, out _));
            Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(1));
            Assert.Equal(KlineReason.Cancelled, iface.Transport.LastReason);
        }
    }
}
