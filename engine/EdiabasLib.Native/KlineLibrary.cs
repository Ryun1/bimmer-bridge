// Not upstream code.

namespace EdiabasLib.Native
{
    /// libkline calls that need no open cable.
    public static class KlineLibrary
    {
        /// libkline's version; throws if the library cannot be loaded.
        public static string Version() => KlineNative.Version();

        /// The single FTDI 0403:6001 cable's device path; null when there are none or several
        /// (count says which). Opens nothing.
        public static string FindCable(out int count) => KlineNative.FindCable(out count);
    }
}
