using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace EdiabasLib.Native
{
    /// Makes EdiabasLib find job descriptions whatever the case of the name in the SGBD.
    ///
    /// EdiabasLib stores each job's description block under the name exactly as the block
    /// writes it ("JOBNAME:initialisierung") and looks it up in upper case, in _JOBCOMMENTS,
    /// _ARGUMENTS and _RESULTS (EdiabasNet.cs ReadDescriptions, VJobJobComments). Many BMW
    /// SGBDs write some names in lower or mixed case (initialisierung, Start_Bus_Communication,
    /// FS_LESEN_sl), so those jobs came back with no comment, arguments or results, where real
    /// EDIABAS finds them (M1 describe comparison, 83 SGBDs). Upstream is unmodified: this
    /// reaches the private _descriptionInfos, _sgbdFs and ReadDescriptions by reflection.
    public static class JobDescriptions
    {
        /// Loads the current SGBD's descriptions if they are not yet loaded and rekeys them
        /// case-insensitively. Call after ResolveSgbdFile, before ExecuteJob. When two names
        /// differ only in case, the one already in upper case (the one EdiabasLib finds today)
        /// is kept, otherwise the first in the file.
        public static void IgnoreCase(EdiabasNet ed)
        {
            if (ed == null) throw new ArgumentNullException(nameof(ed));
            var m = Members();

            // ExecuteJob's own first step (EdiabasNet.cs ExecuteJobPrivate): close the file if
            // the SGBD changed, then open it. Doing it here means ExecuteJob finds the file
            // open and keeps the descriptions rekeyed below; otherwise it reopens the file
            // and resets them.
            if ((bool)m.CloseFlag.GetValue(ed))
            {
                m.CloseFlag.SetValue(ed, false);
                m.Close.Invoke(ed, null);
            }

            if (!(bool)m.Open.Invoke(ed, null)) return;

            var infos = m.Infos.GetValue(ed);
            if (infos == null)
            {
                if (!(m.SgbdFs.GetValue(ed) is Stream fs)) return;
                var position = fs.Position;
                try
                {
                    infos = m.Read.Invoke(ed, new object[] { fs });
                }
                finally
                {
                    fs.Position = position;
                }

                if (infos == null) return;
                m.Infos.SetValue(ed, infos);
            }

            if (!(m.JobComments.GetValue(infos) is Dictionary<string, List<string>> comments)) return;
            if (ReferenceEquals(comments.Comparer, StringComparer.OrdinalIgnoreCase)) return;

            var folded = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in comments)
            {
                if (!folded.ContainsKey(kv.Key) || IsUpper(kv.Key))
                {
                    folded[kv.Key] = kv.Value;
                }
            }

            m.JobComments.SetValue(infos, folded);
        }

        /// Throws, naming what is missing, if IgnoreCase cannot work against this EdiabasLib.
        public static void CheckSupported() => Members();

        static bool IsUpper(string s) => string.Equals(s, s.ToUpperInvariant(), StringComparison.Ordinal);

        sealed class Reflected
        {
            public FieldInfo Infos;
            public FieldInfo SgbdFs;
            public FieldInfo CloseFlag;
            public MethodInfo Open;
            public MethodInfo Close;
            public MethodInfo Read;
            public PropertyInfo JobComments;
        }

        static Reflected _members;

        static Reflected Members()
        {
            var cached = _members;
            if (cached != null) return cached;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic;
            var t = typeof(EdiabasNet);
            var infos = t.GetField("_descriptionInfos", inst) ?? throw Missing("_descriptionInfos");
            var fs = t.GetField("_sgbdFs", inst) ?? throw Missing("_sgbdFs");
            var closeFlag = t.GetField("_closeSgbdFs", inst);
            if (closeFlag == null || closeFlag.FieldType != typeof(bool)) throw Missing("_closeSgbdFs (bool)");
            var open = t.GetMethod("OpenSgbdFs", inst, null, Type.EmptyTypes, null);
            if (open == null || open.ReturnType != typeof(bool)) throw Missing("OpenSgbdFs() returning bool");
            var close = t.GetMethod("CloseSgbdFs", inst, null, Type.EmptyTypes, null) ?? throw Missing("CloseSgbdFs()");
            var read = t.GetMethod("ReadDescriptions", inst, null, new[] { typeof(Stream) }, null) ?? throw Missing("ReadDescriptions(Stream)");
            var comments = infos.FieldType.GetProperty("JobComments", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (comments == null || comments.PropertyType != typeof(Dictionary<string, List<string>>) || !comments.CanWrite)
            {
                throw Missing("DescriptionInfos.JobComments (a settable Dictionary<string, List<string>>)");
            }

            _members = new Reflected { Infos = infos, SgbdFs = fs, CloseFlag = closeFlag, Open = open, Close = close, Read = read, JobComments = comments };
            return _members;
        }

        static InvalidOperationException Missing(string what) =>
            new InvalidOperationException($"EdiabasNet.{what} not found; JobDescriptions.IgnoreCase needs it (check the EdiabasLib pin)");
    }
}
