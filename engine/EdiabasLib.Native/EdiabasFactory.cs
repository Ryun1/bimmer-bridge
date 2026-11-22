using System.Runtime.CompilerServices;
using System.Text;

namespace EdiabasLib.Native
{
    /// Creates EdiabasNet instances configured the same way everywhere.
    public static class EdiabasFactory
    {
        static readonly object Gate = new object();
        static bool _encodingRegistered;

        /// EdiabasNet has "static readonly Encoding Encoding = Encoding.GetEncoding(1252)".
        /// Code page 1252 exists on .NET only after CodePagesEncodingProvider is registered, and
        /// that must happen before EdiabasNet's type initializer runs. Hence the split below:
        /// this method touches no EdiabasNet type, and CreateCore is never inlined into it.
        public static EdiabasNet Create(string ecuPath, EdInterfaceBase iface, bool noInitForVJobs)
        {
            EnsureEncoding();
            return CreateCore(ecuPath, iface, noInitForVJobs);
        }

        internal static void EnsureEncoding()
        {
            lock (Gate)
            {
                if (_encodingRegistered) return;
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _encodingRegistered = true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static EdiabasNet CreateCore(string ecuPath, EdInterfaceBase iface, bool noInitForVJobs)
        {
            var ed = new EdiabasNet();
            ed.EdInterfaceClass = iface;
            ed.NoInitForVJobs = noInitForVJobs;
            ed.SetConfigProperty("EcuPath", ecuPath);
            return ed;
        }
    }
}
