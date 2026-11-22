using System.Runtime.CompilerServices;
using System.Text;

namespace EdiabasLib.Native
{
    /// Creates EdiabasNet instances configured the same way everywhere.
    ///
    /// Only one EdiabasNet may interpret at a time per process: its constructor rebinds the
    /// static register table to the newest instance (EdiabasNet.cs:3224-3227), so an older
    /// instance's registers then read and write the newer one's. Create, use, dispose, then
    /// create the next.
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

        /// EdiabasNet's constructor fills a static dictionary (_trapBitDict, EdiabasNet.cs:3178)
        /// without a lock, so two first constructions on different threads can collide with
        /// "An item with the same key has already been added". Construction is serialised here.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static EdiabasNet CreateCore(string ecuPath, EdInterfaceBase iface, bool noInitForVJobs)
        {
            EdiabasNet ed;
            lock (Gate)
            {
                ed = new EdiabasNet();
            }
            ed.EdInterfaceClass = iface;
            ed.NoInitForVJobs = noInitForVJobs;
            ed.SetConfigProperty("EcuPath", ecuPath);
            return ed;
        }
    }
}
