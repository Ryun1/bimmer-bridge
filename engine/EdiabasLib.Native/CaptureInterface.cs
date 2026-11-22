// Not upstream code.
//
// An in-memory EdInterfaceBase for the concept census. It opens nothing. It reports itself
// connected after InterfaceConnect so that EdiabasNet lets "xsetpar" through (OpXsetpar
// raises IFH-0056 on an unconnected interface), records the parameters the SGBD sets, and
// stops the job with CaptureStopException at the first call that would reach a car: any
// transmit, reset, boot, port, programming voltage or SI relay. Battery and ignition voltage
// read as 12000 mV; key bytes and state read as empty. All of these are recorded in Calls.

using System;
using System.Collections.Generic;

namespace EdiabasLib.Native
{
    public sealed class CaptureStopException : InvalidOperationException
    {
        public CaptureStopException(string what)
            : base("capture interface: stopped at '" + what + "', which would reach the vehicle")
        {
        }
    }

    public sealed class CaptureInterface : EdInterfaceBase
    {
        public const long VoltageMillivolts = 12000;

        readonly List<string> _calls = new List<string>();
        bool _connected;
        uint[] _commParameter;

        /// A copy of the last parameter array the SGBD set with xsetpar, or null.
        public uint[] LastCommParameter { get; private set; }

        /// How many times the SGBD set the parameters.
        public int ParameterSets { get; private set; }

        /// Interface calls in the order they were made.
        public IReadOnlyList<string> Calls => _calls;

        void Record(string what) => _calls.Add(what);

        void Stop(string what)
        {
            Record(what);
            throw new CaptureStopException(what);
        }

        public override uint[] CommParameter
        {
            get => _commParameter;
            set
            {
                _commParameter = value;
                LastCommParameter = value == null ? null : (uint[])value.Clone();
                ParameterSets++;
            }
        }

        public override bool IsValidInterfaceName(string name) =>
            string.Equals(name, "CAPTURE", StringComparison.OrdinalIgnoreCase);

        // The base implementations load simulation files; this class touches no file.
        public override bool InterfaceConnect() { _connected = true; return true; }
        public override bool InterfaceDisconnect() { _connected = false; return true; }
        public override bool IsSimulationMode() => false;
        public override bool LoadInterfaceSimFile() => false;
        public override bool LoadSgbdSimFile(string fileName) => false;

        public override bool InterfaceReset() { Stop("InterfaceReset"); return false; }
        public override bool InterfaceBoot() { Stop("InterfaceBoot"); return false; }
        public override bool TransmitData(byte[] sendData, out byte[] receiveData) { receiveData = null; Stop("TransmitData"); return false; }
        public override bool TransmitFrequent(byte[] sendData) { Stop("TransmitFrequent"); return false; }
        public override bool ReceiveFrequent(out byte[] receiveData) { receiveData = null; Stop("ReceiveFrequent"); return false; }
        public override bool StopFrequent() { Stop("StopFrequent"); return false; }
        public override bool RawData(byte[] sendData, out byte[] receiveData) { receiveData = null; Stop("RawData"); return false; }
        // EdiabasNet calls this at the start of every job and on abort.
        public override bool TransmitCancel(bool cancel) => true;

        public override bool BmwFastProtocol => false;
        public override string InterfaceType => "CAPTURE";
        public override uint InterfaceVersion => 0;
        public override string InterfaceName => "CAPTURE";
        public override string InterfaceVerName => "census capture interface (no IFH)";

        public override byte[] KeyBytes { get { Record("KeyBytes"); return Array.Empty<byte>(); } }
        public override byte[] State { get { Record("State"); return Array.Empty<byte>(); } }
        public override uint LoopTest { get { Stop("LoopTest"); return 0; } }
        public override long BatteryVoltage { get { Record("BatteryVoltage"); return VoltageMillivolts; } }
        public override long IgnitionVoltage { get { Record("IgnitionVoltage"); return VoltageMillivolts; } }
        public override int? AdapterVersion => null;
        public override byte[] AdapterSerial => null;
        public override double? AdapterVoltage => null;

        public override long GetPort(uint index) { Stop("GetPort"); return -1; }
        public override void SetPort(uint index, uint value) => Stop("SetPort");
        public override void SetProgramVoltage(uint voltage) => Stop("SetProgramVoltage");
        public override void SwitchSiRelais(uint time) => Stop("SwitchSiRelais");

        public override bool Connected => _connected;
        public override bool IsEcuConnected => false;
    }
}
