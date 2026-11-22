using EdiabasLib;
using EdiabasLib.Native;
using Ediabasd;

namespace Ediabasd.Tests;

/// An interface that opens nothing. TransmitData either blocks until the cable is cancelled
/// (then fails) or fails at once; everything else answers harmlessly.
public sealed class FakeInterface : EdInterfaceBase
{
    public ManualResetEventSlim Cancelled { get; } = new(false);
    public bool BlockOnTransmit { get; set; }
    bool _connected;

    public override bool IsValidInterfaceName(string name) => name == "FAKE";
    public override bool InterfaceConnect() { _connected = true; return true; }
    public override bool InterfaceDisconnect() { _connected = false; return true; }
    public override bool IsSimulationMode() => false;
    public override bool LoadInterfaceSimFile() => false;
    public override bool LoadSgbdSimFile(string fileName) => false;
    public override bool InterfaceReset() => true;
    public override bool InterfaceBoot() => true;

    public override bool TransmitData(byte[] sendData, out byte[] receiveData)
    {
        receiveData = Array.Empty<byte>();
        if (BlockOnTransmit) Cancelled.Wait(TimeSpan.FromSeconds(30));
        Ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0003);
        return false;
    }

    public override bool TransmitFrequent(byte[] sendData) => false;
    public override bool ReceiveFrequent(out byte[] receiveData) { receiveData = Array.Empty<byte>(); return false; }
    public override bool StopFrequent() => true;
    public override bool RawData(byte[] sendData, out byte[] receiveData) { receiveData = Array.Empty<byte>(); return false; }
    public override bool TransmitCancel(bool cancel) => true;
    public override bool BmwFastProtocol => false;
    public override string InterfaceType => "FAKE";
    public override uint InterfaceVersion => 0;
    public override string InterfaceName => "FAKE";
    public override string InterfaceVerName => "fake";
    public override byte[] KeyBytes => Array.Empty<byte>();
    public override byte[] State => Array.Empty<byte>();
    public override uint LoopTest => 0;
    public override long BatteryVoltage => 12000;
    public override long IgnitionVoltage => 12000;
    public override int? AdapterVersion => null;
    public override byte[]? AdapterSerial => null;
    public override double? AdapterVoltage => null;
    public override long GetPort(uint index) => 0;
    public override void SetPort(uint index, uint value) { }
    public override void SetProgramVoltage(uint voltage) { }
    public override void SwitchSiRelais(uint time) { }
    public override bool Connected => _connected;
    public override bool IsEcuConnected => false;
}

public sealed class FakeCable : ILiveCable
{
    public FakeCable(EdInterfaceBase iface) => Interface = iface;
    public EdInterfaceBase Interface { get; }
    public KlineReason Reason { get; set; }
    public int CancelCount { get; private set; }
    public bool Disposed { get; private set; }

    public void Cancel()
    {
        CancelCount++;
        (Interface as FakeInterface)?.Cancelled.Set();
    }

    public void Dispose() => Disposed = true;
}

/// Counts how often the live cable is created, so tests can show describe never creates it.
public sealed class CableFactory
{
    readonly Func<ILiveCable> _make;
    public CableFactory(Func<ILiveCable> make) => _make = make;
    public int Calls { get; private set; }
    public ILiveCable? Last { get; private set; }

    public ILiveCable Make()
    {
        Calls++;
        Last = _make();
        return Last;
    }
}
