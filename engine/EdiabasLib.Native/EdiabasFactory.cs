using System;
using System.Reflection;
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
            CheckActivateSupported();
            return CreateCore(ecuPath, iface, noInitForVJobs);
        }

        /// Binds EdiabasNet's static register table to ed, the instance about to run a job.
        /// Call it before every ResolveSgbdFile/ExecuteJob when more than one instance exists.
        /// Upstream is unmodified: this reaches the private RegisterList and
        /// Register.SetEdiabas (EdiabasNet.cs:1586-1599, 1865, 3224-3227) by reflection.
        public static void Activate(EdiabasNet ed)
        {
            if (ed == null) throw new ArgumentNullException(nameof(ed));
            var (list, setEdiabas) = RegisterMembers();
            lock (Gate)
            {
                foreach (var reg in (Array)list.GetValue(null))
                {
                    setEdiabas.Invoke(reg, new object[] { ed });
                }
            }
        }

        /// Throws, naming what is missing, if Activate cannot work against this EdiabasLib.
        public static void CheckActivateSupported() => RegisterMembers();

        static (FieldInfo List, MethodInfo SetEdiabas) _registerMembers;

        static (FieldInfo, MethodInfo) RegisterMembers()
        {
            var cached = _registerMembers;
            if (cached.List != null) return cached;
            var list = typeof(EdiabasNet).GetField("RegisterList", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("EdiabasNet.RegisterList not found; EdiabasFactory.Activate needs it (check the EdiabasLib pin)");
            var register = list.FieldType.GetElementType()
                ?? throw new InvalidOperationException("EdiabasNet.RegisterList is not an array; EdiabasFactory.Activate needs it");
            var set = register.GetMethod("SetEdiabas", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(EdiabasNet) }, null)
                ?? throw new InvalidOperationException("EdiabasNet.Register.SetEdiabas not found; EdiabasFactory.Activate needs it (check the EdiabasLib pin)");
            _registerMembers = (list, set);
            return _registerMembers;
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
