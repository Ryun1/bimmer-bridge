// Not upstream code. P/Invoke over libkline's C ABI (kline/include/kline.h).

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EdiabasLib.Native
{
    /// Why a libkline call failed; the numbers are kline.h's kline_reason.
    public enum KlineReason
    {
        Ok = 0,
        NoDevice = 1,
        Locked = 2,
        NotFtdi = 3,
        Permission = 4,
        GateRefused = 5,
        Io = 6,
        Timeout = 7,
        Cancelled = 8,
        ReplayMismatch = 9,
        Unsupported = 10,
    }

    internal static unsafe class KlineNative
    {
        const string Lib = "kline";

        static KlineNative()
        {
            NativeLibrary.SetDllImportResolver(typeof(KlineNative).Assembly, Resolve);
        }

        /// $KLINE_LIB if set, else libkline beside this assembly. Never a search path.
        static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
        {
            if (name != Lib) return IntPtr.Zero;
            var env = Environment.GetEnvironmentVariable("KLINE_LIB");
            var file = !string.IsNullOrEmpty(env)
                ? env
                : Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsMacOS() ? "libkline.dylib" : "libkline.so");
            return NativeLibrary.Load(file);
        }

        [DllImport(Lib)] public static extern int kline_open(string path, string recordDir, out IntPtr k);
        [DllImport(Lib)] public static extern int kline_open_ex(string path, string recordDir, uint latencyMs, out IntPtr k);
        [DllImport(Lib)] public static extern IntPtr kline_latency(IntPtr k);
        [DllImport(Lib)] public static extern int kline_open_replay(string recordingPath, out IntPtr k);
        [DllImport(Lib)] public static extern void kline_close(IntPtr k);
        [DllImport(Lib)] public static extern int kline_set_line(IntPtr k, uint baud, byte dataBits, byte parity);
        [DllImport(Lib)] public static extern int kline_set_dtr(IntPtr k, int on);
        [DllImport(Lib)] public static extern int kline_set_rts(IntPtr k, int on);
        [DllImport(Lib)] public static extern int kline_get_dsr(IntPtr k, out int dsr);
        [DllImport(Lib)] public static extern int kline_set_break(IntPtr k, int on);
        [DllImport(Lib)] public static extern int kline_set_concept(IntPtr k, uint* parameters, UIntPtr count);
        [DllImport(Lib)] public static extern int kline_set_interbyte(IntPtr k, uint ms);
        [DllImport(Lib)] public static extern int kline_send(IntPtr k, byte* data, UIntPtr len);
        [DllImport(Lib)] public static extern int kline_receive(IntPtr k, byte* buf, UIntPtr len, uint timeoutMs, uint telEndMs);
        [DllImport(Lib)] public static extern int kline_purge(IntPtr k);
        [DllImport(Lib)] public static extern void kline_cancel(IntPtr k, int on);
        [DllImport(Lib)] public static extern int kline_last_error(IntPtr k);
        [DllImport(Lib)] public static extern int kline_find_cable(byte* path, UIntPtr pathLen);
        [DllImport(Lib)] public static extern int kline_cable_state(string path);
        [DllImport(Lib)] public static extern IntPtr kline_version();

        public static string Version() => Marshal.PtrToStringUTF8(kline_version());

        /// The single FTDI cable's path, or null with the count of cables found (0 or more than 1).
        public static string FindCable(out int count)
        {
            var buf = new byte[1024];
            fixed (byte* p = buf)
            {
                count = kline_find_cable(p, (UIntPtr)buf.Length);
            }
            if (count != 1) return null;
            var end = Array.IndexOf(buf, (byte)0);
            return System.Text.Encoding.UTF8.GetString(buf, 0, end < 0 ? buf.Length : end);
        }
    }
}
