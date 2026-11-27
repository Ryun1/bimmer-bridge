// Not upstream code. Adapted from the ediabaslib spike's EdInterfaceOffline.
//
// An EdInterfaceBase that never opens anything. It exists so EdiabasNet has an
// interface object (some BEST opcodes, e.g. xconnect, throw "No communication class
// present" when EdInterfaceClass is null) while guaranteeing that no serial port,
// USB device, socket or simulation file is ever touched.
//
// - Connected / IsEcuConnected are always false, so every opcode that checks
//   "interfaceClass == null || !interfaceClass.Connected" (xsend, xsetpar, xawlen,
//   xraw, xreps, xrequf, xsendf, xstopf, xtype, xvers) raises EDIABAS_IFH_0056
//   inside EdiabasNet itself (EdOperations.cs) before it reaches this class.
// - Everything else that would talk to hardware (transmit, reset, boot, ports,
//   voltages, key bytes, state, loop test, prog voltage, SI relay) throws
//   OfflineInterfaceException. Those paths are mostly unreachable anyway because
//   EdOperations checks Connected first, but some (xbatt, xignit, xkeyb, xstate,
//   xloopt, xgetport/xsetport, xprog, xsireset, xreset, xboot) only check for null.
// - InterfaceConnect/InterfaceDisconnect deliberately do NOT call the base
//   implementation, because the base loads simulation files.

using System;

namespace EdiabasLib.Native
{
    public sealed class OfflineInterfaceException : InvalidOperationException
    {
        public OfflineInterfaceException(string what)
            : base("offline interface: '" + what + "' would need the vehicle or cable; refused")
        {
        }
    }

    public sealed class OfflineInterface : EdInterfaceBase
    {
        private static void Refuse(string what)
        {
            throw new OfflineInterfaceException(what);
        }

        public override bool IsValidInterfaceName(string name)
        {
            return string.Compare(name, "OFFLINE", StringComparison.OrdinalIgnoreCase) == 0;
        }

        // No mutex: InterfaceLock() returns false when InterfaceMutex is null (base default).

        // BEST "xconnect" ignores the return value (EdOperations.cs OpXconnect). Returning false
        // without opening anything leaves Connected == false, so EdiabasNet itself raises
        // EDIABAS_IFH_0056 at the first xsetpar/xsend/xawlen/... - the most faithful offline error.
        public override bool InterfaceConnect() { return false; }
        public override bool InterfaceDisconnect() { return true; }   // nothing to close
        public override bool IsSimulationMode() { return false; }
        public override bool LoadInterfaceSimFile() { return false; }
        public override bool LoadSgbdSimFile(string fileName) { return false; }

        public override bool InterfaceReset() { Refuse("InterfaceReset"); return false; }
        public override bool InterfaceBoot() { Refuse("InterfaceBoot"); return false; }
        public override bool TransmitData(byte[] sendData, out byte[] receiveData) { receiveData = null; Refuse("TransmitData"); return false; }
        public override bool TransmitFrequent(byte[] sendData) { Refuse("TransmitFrequent"); return false; }
        public override bool ReceiveFrequent(out byte[] receiveData) { receiveData = null; Refuse("ReceiveFrequent"); return false; }
        public override bool StopFrequent() { Refuse("StopFrequent"); return false; }
        public override bool RawData(byte[] sendData, out byte[] receiveData) { receiveData = null; Refuse("RawData"); return false; }
        // Called by EdiabasNet at the start of every job and on abort; harmless, so allowed.
        public override bool TransmitCancel(bool cancel) { return true; }

        public override bool BmwFastProtocol { get { return false; } }
        public override string InterfaceType { get { return "OFFLINE"; } }
        public override UInt32 InterfaceVersion { get { return 0; } }
        public override string InterfaceName { get { return "OFFLINE"; } }
        public override string InterfaceVerName { get { return "offline interface (no IFH)"; } }

        public override byte[] KeyBytes { get { Refuse("KeyBytes"); return null; } }
        public override byte[] State { get { Refuse("State"); return null; } }
        public override UInt32 LoopTest { get { Refuse("LoopTest"); return 0; } }
        public override Int64 BatteryVoltage { get { Refuse("BatteryVoltage"); return -1; } }
        public override Int64 IgnitionVoltage { get { Refuse("IgnitionVoltage"); return -1; } }
        public override int? AdapterVersion { get { return null; } }
        public override byte[] AdapterSerial { get { return null; } }
        public override double? AdapterVoltage { get { return null; } }

        public override Int64 GetPort(UInt32 index) { Refuse("GetPort"); return -1; }
        public override void SetPort(UInt32 index, UInt32 value) { Refuse("SetPort"); }
        public override void SetProgramVoltage(UInt32 voltage) { Refuse("SetProgramVoltage"); }
        public override void SwitchSiRelais(UInt32 time) { Refuse("SwitchSiRelais"); }

        public override bool Connected { get { return false; } }
        public override bool IsEcuConnected { get { return false; } }
    }
}
