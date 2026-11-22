using EdiabasLib;
using EdiabasLib.Native;

namespace Ediabasd;

/// The live instance's interface and what libkline says about it.
public interface ILiveCable : IDisposable
{
    EdInterfaceBase Interface { get; }

    /// libkline's reason for the first failure since connect (GateRefused once latched).
    KlineReason Reason { get; }

    /// Makes a send or receive blocked on the cable return now.
    void Cancel();
}

/// KlineInterface on the cable (or on a replayed recording, for tests and the coverage runner).
public sealed class LiveCable : ILiveCable
{
    readonly KlineInterface _iface;

    LiveCable(KlineTransport transport, string port)
    {
        _iface = new KlineInterface(transport) { ComPort = port };
    }

    public static LiveCable Device(string port, string? recordDir) => new(KlineTransport.Device(recordDir), port);

    public static LiveCable Replay(string recordingPath) => new(KlineTransport.Replay(recordingPath), "replay");

    public EdInterfaceBase Interface => _iface;

    public KlineReason Reason => _iface.Transport.LastReason;

    public void Cancel() => _iface.Transport.Cancel();

    // The interface itself is disposed with its EdiabasNet (EdInterfaceClass), which also
    // closes the transport; this only makes sure the port is closed.
    public void Dispose() => _iface.Transport.Dispose();
}
