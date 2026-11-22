// Not upstream code. Binds EdInterfaceObd's public transport delegates to libkline.

using System;
using System.Threading;

namespace EdiabasLib.Native
{
    /// The cable (or a replayed recording) behind one EdInterfaceObd. EdiabasLib opens it on
    /// the SGBD's xconnect and closes it on disconnect. Every failure keeps libkline's reason,
    /// because EdiabasLib reduces them all to IFH-0018 (connect) and IFH-0003 (send).
    public sealed unsafe class KlineTransport : IDisposable
    {
        readonly string _recordDir;
        readonly string _replayPath;
        readonly object _handleLock = new object();
        IntPtr _handle;
        uint[] _concept;
        KlineReason _first = KlineReason.Ok;
        bool _latched;

        KlineTransport(string recordDir, string replayPath)
        {
            _recordDir = recordDir;
            _replayPath = replayPath;
        }

        /// The cable at EdInterfaceObd's ComPort (ObdComPort), recorded to recordDir if set.
        public static KlineTransport Device(string recordDir) => new KlineTransport(recordDir, null);

        /// A recording in place of the cable; the port name is ignored.
        public static KlineTransport Replay(string recordingPath) => new KlineTransport(null, recordingPath);

        public bool IsOpen => _handle != IntPtr.Zero;

        /// The first failure since this transport was made (receive timeouts aside, which are
        /// routine), and GateRefused from the first refusal on, whatever followed.
        public KlineReason LastReason => _latched ? KlineReason.GateRefused : _first;

        /// Makes a blocked send or receive return now. EdiabasNet clears it through
        /// TransmitCancel(false) when the next job starts.
        public void Cancel()
        {
            lock (_handleLock)
            {
                if (_handle != IntPtr.Zero) KlineNative.kline_cancel(_handle, 1);
            }
        }

        /// The SGBD's communication parameters (CommParameter); [0] is the concept the frame
        /// gate checks. Kept and declared again at connect if set before the port opened.
        public void SetConcept(uint[] parameters)
        {
            _concept = parameters == null ? null : (uint[])parameters.Clone();
            if (_handle != IntPtr.Zero) Declare();
        }

        void Declare()
        {
            var p = _concept ?? Array.Empty<uint>();
            fixed (uint* ptr = p)
            {
                Check(KlineNative.kline_set_concept(_handle, ptr, (UIntPtr)p.Length));
            }
        }

        bool Check(int rc)
        {
            var reason = (KlineReason)rc;
            if (reason == KlineReason.Ok) return true;
            if (reason == KlineReason.GateRefused) _latched = true;
            if (reason != KlineReason.Timeout && _first == KlineReason.Ok) _first = reason;
            return false;
        }

        public void Bind(EdInterfaceObd iface)
        {
            iface.InterfaceConnectFunc = Connect;
            iface.InterfaceDisconnectFunc = Disconnect;
            iface.InterfaceTransmitCancelFunc = TransmitCancel;
            iface.InterfaceSetConfigFunc = SetConfig;
            iface.InterfaceSetDtrFunc = on => Check(KlineNative.kline_set_dtr(_handle, on ? 1 : 0));
            iface.InterfaceSetRtsFunc = on => Check(KlineNative.kline_set_rts(_handle, on ? 1 : 0));
            iface.InterfaceGetDsrFunc = GetDsr;
            iface.InterfaceSetBreakFunc = on => Check(KlineNative.kline_set_break(_handle, on ? 1 : 0));
            iface.InterfaceSetInterByteTimeFunc = ms => Check(KlineNative.kline_set_interbyte(_handle, (uint)Math.Max(0, ms)));
            iface.InterfacePurgeInBufferFunc = () => Check(KlineNative.kline_purge(_handle));
            iface.InterfaceAdapterEchoFunc = () => true;
            iface.InterfaceSendDataFunc = SendData;
            iface.InterfaceReceiveDataFunc = ReceiveData;
            // Left unset on purpose: no send-pulse (5-baud init), and no ignition query (it
            // would send an F1 F1 FA frame; ignition is read from DSR instead).
        }

        /// Opens the port, or keeps it open: every SGBD's INIT runs xconnect, and upstream's own
        /// transports stay connected when already open (EdInterfaceObd.cs:1572). A gate refusal
        /// stays latched for the life of this transport (one request), across reconnects.
        bool Connect(string port, object parameter)
        {
            if (_handle != IntPtr.Zero)
            {
                return true;
            }

            IntPtr h;
            var rc = _replayPath != null
                ? KlineNative.kline_open_replay(_replayPath, out h)
                : KlineNative.kline_open(port ?? "", _recordDir, out h);
            if (!Check(rc)) return false;
            lock (_handleLock) _handle = h;
            if (_concept != null) Declare();
            return true;
        }

        bool Disconnect()
        {
            lock (_handleLock)
            {
                if (_handle != IntPtr.Zero) KlineNative.kline_close(_handle);
                _handle = IntPtr.Zero;
            }
            return true;
        }

        bool TransmitCancel(bool cancel)
        {
            lock (_handleLock)
            {
                if (_handle != IntPtr.Zero) KlineNative.kline_cancel(_handle, cancel ? 1 : 0);
            }
            return true;
        }

        EdInterfaceObd.InterfaceErrorResult SetConfig(EdInterfaceObd.Protocol protocol, int baudRate, int dataBits, EdInterfaceObd.SerialParity parity, bool allowBitBang)
        {
            if (protocol != EdInterfaceObd.Protocol.Uart || baudRate <= 0 || dataBits < 5 || dataBits > 8)
                return EdInterfaceObd.InterfaceErrorResult.ConfigError;
            byte par;
            switch (parity)
            {
                case EdInterfaceObd.SerialParity.None: par = 0; break;
                case EdInterfaceObd.SerialParity.Even: par = 1; break;
                case EdInterfaceObd.SerialParity.Odd: par = 2; break;
                default: return EdInterfaceObd.InterfaceErrorResult.ConfigError;
            }
            return Check(KlineNative.kline_set_line(_handle, (uint)baudRate, (byte)dataBits, par))
                ? EdInterfaceObd.InterfaceErrorResult.NoError
                : EdInterfaceObd.InterfaceErrorResult.ConfigError;
        }

        bool GetDsr(out bool dsr)
        {
            var ok = Check(KlineNative.kline_get_dsr(_handle, out var v));
            dsr = v != 0;
            return ok;
        }

        bool SendData(byte[] sendData, int length, bool setDtr, double dtrTimeCorr)
        {
            // Only concepts 1-3 raise DTR during a send on an echoing cable
            // (EdInterfaceObd.cs:555/627/665); they are out of scope.
            if (setDtr)
            {
                Check((int)KlineReason.Unsupported);
                return false;
            }
            if (_latched) return Check((int)KlineReason.GateRefused);
            if (length < 0 || length > sendData.Length) return Check((int)KlineReason.Io);
            fixed (byte* p = sendData)
            {
                return Check(KlineNative.kline_send(_handle, p, (UIntPtr)length));
            }
        }

        bool ReceiveData(byte[] receiveData, int offset, int length, int timeout, int timeoutTelEnd, EdiabasNet ediabasLog)
        {
            if (length <= 0) return true;
            if (offset < 0 || offset + length > receiveData.Length) return Check((int)KlineReason.Io);
            fixed (byte* p = receiveData)
            {
                return Check(KlineNative.kline_receive(_handle, p + offset, (UIntPtr)length,
                    (uint)Math.Max(0, timeout), (uint)Math.Max(0, timeoutTelEnd)));
            }
        }

        public void Dispose() => Disconnect();
    }
}
