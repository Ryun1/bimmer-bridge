using System.Runtime.InteropServices;
using EdiabasAgent.Core;

namespace EdiabasAgent.Ediabas;

/// <summary>
/// P/Invoke over EDIABAS's <c>api32.dll</c> (32-bit, stdcall).
/// <para>The public prototypes in <c>Api.h</c> (EDIABAS 7, <c>C:\EDIABAS\Api\WIN32\Api.h</c>) take
/// no handle: they belong to the static client library. The DLL itself exports the same functions
/// as <c>__api…</c> with an <c>unsigned int</c> handle first, which <c>__apiInit</c> fills in; this is
/// how pydiabas calls the same DLL. Every other parameter follows Api.h. In this 32-bit process C
/// <c>long</c> is 32 bits, <c>APIWORD</c> is <c>ushort</c>, <c>APIBOOL</c> and
/// <c>APIRESULTFORMAT</c> are 4-byte enums.</para>
/// <para>Only what read jobs need is declared. apiSetConfig, apiSwitchDevice, apiInitExt,
/// apiJobData, apiJobExt, apiTrace, apiCallBack and apiErrorHandler are deliberately absent and
/// must not be added.</para>
/// <para>The DLL is loaded from the configured full path only, never by bare name or search path,
/// and only when an EDIABAS request needs it (never by /health). EdiabasService unloads it again
/// after the idle time (<see cref="Unload"/>) and the next request loads it afresh.</para>
/// </summary>
internal sealed class Api32 : IEdiabasApi
{
    // Buffer sizes from Api.h.
    private const int APIMAXRESULT = 256;   // "maximal result length incl. '\0'" (result names)
    private const int APIMAXTEXT = 1024;    // "maximal text length incl. '\0'"
    private const int APIMAXBINARY = 1024;  // "maximal binary buffer length"

    // Extra room after every out-buffer, in case the DLL writes one byte too many.
    private const int Slack = 256;

    /// <summary>Every export this class uses. All must resolve before anything is called.</summary>
    public static readonly string[] Exports =
    [
        "__apiInit", "__apiEnd", "__apiJob", "__apiState", "__apiBreak",
        "__apiErrorCode", "__apiErrorText",
        "__apiResultSets", "__apiResultNumber", "__apiResultName", "__apiResultFormat",
        "__apiResultChar", "__apiResultByte", "__apiResultInt", "__apiResultWord",
        "__apiResultLong", "__apiResultDWord", "__apiResultReal", "__apiResultText", "__apiResultBinary",
    ];

    // ---- delegates: handle first, then the Api.h parameters ----

    // APIBOOL apiInit(void);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitFn(out uint handle);

    // void apiEnd(void);  void apiBreak(void);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void HandleFn(uint handle);

    // int apiState(void);  int apiErrorCode(void);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int HandleIntFn(uint handle);

    // const char *apiErrorText(void);  (the DLL export fills a caller buffer of the given length)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr ErrorTextFn(uint handle, [In, Out] byte[] buffer, int length);

    // void apiJob(const char *ecu, const char *job, const char *para, const char *result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void JobFn(uint handle, byte[] ecu, byte[] job, byte[] para, byte[] result);

    // APIBOOL apiResultSets(APIWORD *);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResultSetsFn(uint handle, out ushort sets);

    // APIBOOL apiResultNumber(APIWORD *, APIWORD set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResultNumberFn(uint handle, out ushort count, ushort set);

    // APIBOOL apiResultName(char *, APIWORD index, APIWORD set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResultNameFn(uint handle, [In, Out] byte[] name, ushort index, ushort set);

    // APIBOOL apiResultFormat(APIRESULTFORMAT *, const char *name, APIWORD set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResultFormatFn(uint handle, out int format, byte[] name, ushort set);

    // APIBOOL apiResultChar(APICHAR *, const char *, APIWORD);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResultCharFn(uint handle, out sbyte value, byte[] name, ushort set);

    // APIBOOL apiResultByte(APIBYTE *, const char *, APIWORD);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResultByteFn(uint handle, out byte value, byte[] name, ushort set);

    // APIBOOL apiResultInt(APIINTEGER *, const char *, APIWORD);   APIINTEGER = short
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResultIntFn(uint handle, out short value, byte[] name, ushort set);

    // APIBOOL apiResultWord(APIWORD *, const char *, APIWORD);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResultWordFn(uint handle, out ushort value, byte[] name, ushort set);

    // APIBOOL apiResultLong(APILONG *, const char *, APIWORD);     APILONG = long = 32 bits here
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResultLongFn(uint handle, out int value, byte[] name, ushort set);

    // APIBOOL apiResultDWord(APIDWORD *, const char *, APIWORD);   APIDWORD = unsigned long = 32 bits
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResultDWordFn(uint handle, out uint value, byte[] name, ushort set);

    // APIBOOL apiResultReal(APIREAL *, const char *, APIWORD);     APIREAL = double
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResultRealFn(uint handle, out double value, byte[] name, ushort set);

    // APIBOOL apiResultText(APITEXT *, const char *name, APIWORD set, const char *format);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResultTextFn(uint handle, [In, Out] byte[] buffer, byte[] name, ushort set, byte[] format);

    // APIBOOL apiResultBinary(APIBINARY *, APIWORD *length, const char *name, APIWORD set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResultBinaryFn(uint handle, [In, Out] byte[] buffer, out ushort length, byte[] name, ushort set);

    private static readonly byte[] EmptyCString = [0];

    private readonly string _dllPath;
    private IntPtr _lib;
    private Functions? _f;
    private uint _handle;
    private bool _haveHandle;
    private int _ownerThread;

    public Api32(string dllPath)
    {
        _dllPath = dllPath;
    }

    /// <summary>
    /// For --selftest: loads the DLL, resolves every export, frees it again. Calls nothing in it.
    /// Null on success, else the reason.
    /// </summary>
    public static string? Probe(string dllPath)
    {
        var reason = CheckPath(dllPath);
        if (reason is not null)
        {
            return reason;
        }

        if (!NativeLibrary.TryLoad(dllPath, out var lib))
        {
            return $"{dllPath} could not be loaded (is it the 32-bit api32.dll?)";
        }

        try
        {
            var missing = Exports.Where(e => !NativeLibrary.TryGetExport(lib, e, out _)).ToList();
            return missing.Count == 0 ? null : $"{dllPath} lacks export(s): {string.Join(", ", missing)}";
        }
        finally
        {
            NativeLibrary.Free(lib);
        }
    }

    public bool TryLoad(out string? reason)
    {
        if (_f is not null)
        {
            reason = null;
            return true;
        }

        _ownerThread = Environment.CurrentManagedThreadId;
        reason = CheckPath(_dllPath);
        if (reason is not null)
        {
            return false;
        }

        if (!NativeLibrary.TryLoad(_dllPath, out var lib))
        {
            reason = $"{_dllPath} could not be loaded (is it the 32-bit api32.dll?)";
            return false;
        }

        // Resolve everything first; a missing export means nothing is called at all.
        var addresses = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var e in Exports)
        {
            if (NativeLibrary.TryGetExport(lib, e, out var p) && p != IntPtr.Zero)
            {
                addresses[e] = p;
            }
            else
            {
                missing.Add(e);
            }
        }

        if (missing.Count > 0)
        {
            NativeLibrary.Free(lib);
            reason = $"{_dllPath} lacks export(s): {string.Join(", ", missing)}";
            return false;
        }

        _lib = lib;
        _f = new Functions(addresses);
        return true;
    }

    public void Unload()
    {
        if (_lib == IntPtr.Zero)
        {
            _f = null;
            return;
        }

        if (Environment.CurrentManagedThreadId != _ownerThread)
        {
            throw new InvalidOperationException("api32.dll not unloaded: called from a thread other than the EDIABAS runner thread");
        }

        if (_haveHandle)
        {
            throw new InvalidOperationException("api32.dll not unloaded: a handle from apiInit is still open");
        }

        // Drop every delegate before the code they point at goes away; from here on Ready()
        // refuses, and TryLoad loads and resolves the DLL again.
        var lib = _lib;
        _f = null;
        _lib = IntPtr.Zero;
        NativeLibrary.Free(lib);

        // FreeLibrary only lowers a reference count. Say so if the DLL is still mapped, because
        // then ebas32.exe will not exit either.
        if (IsMapped())
        {
            throw new InvalidOperationException($"FreeLibrary returned, but {Path.GetFileName(_dllPath)} is still mapped in the agent process");
        }
    }

    public bool Init()
    {
        var f = Ready();
        _handle = 0;
        _haveHandle = false;
        var ok = f.Init(out var h) != 0;
        _handle = h;

        // After a failed init a zero handle means there is nothing to end or ask about.
        _haveHandle = ok || h != 0;
        return ok;
    }

    public void Job(string ecu, string job, string parameters, string results) =>
        Ready().Job(Handle(), Win1252.ToCString(ecu), Win1252.ToCString(job), Win1252.ToCString(parameters), Win1252.ToCString(results));

    public int State() => Ready().State(Handle());

    public void Break() => Ready().Break(Handle());

    public void End()
    {
        var f = Ready();
        if (!_haveHandle)
        {
            return;
        }

        _haveHandle = false;
        f.End(_handle);
    }

    public int ErrorCode() => _haveHandle ? Ready().ErrorCode(_handle) : -1;

    public string ErrorText()
    {
        if (!_haveHandle)
        {
            return "apiInit failed and returned no handle";
        }

        var buffer = new byte[APIMAXTEXT + Slack];
        Ready().ErrorText(_handle, buffer, APIMAXTEXT);
        return Win1252.FromCString(buffer.AsSpan(0, APIMAXTEXT));
    }

    public bool ResultSets(out int sets)
    {
        var ok = Ready().ResultSets(Handle(), out var n) != 0;
        sets = n;
        return ok;
    }

    public bool ResultNumber(int set, out int count)
    {
        count = 0;
        if (!Word(set, out var s))
        {
            return false;
        }

        var ok = Ready().ResultNumber(Handle(), out var n, s) != 0;
        count = n;
        return ok;
    }

    public bool ResultName(int index, int set, out string name)
    {
        name = "";
        if (!Word(index, out var i) || !Word(set, out var s))
        {
            return false;
        }

        var buffer = new byte[APIMAXRESULT + Slack];
        if (Ready().ResultName(Handle(), buffer, i, s) == 0)
        {
            return false;
        }

        name = Win1252.FromCString(buffer.AsSpan(0, APIMAXRESULT));
        return true;
    }

    public bool ResultFormat(string name, int set, out int format)
    {
        format = -1;
        return Word(set, out var s) && Ready().ResultFormat(Handle(), out format, Win1252.ToCString(name), s) != 0;
    }

    public bool ResultChar(string name, int set, out sbyte value)
    {
        value = 0;
        return Word(set, out var s) && Ready().ResultChar(Handle(), out value, Win1252.ToCString(name), s) != 0;
    }

    public bool ResultByte(string name, int set, out byte value)
    {
        value = 0;
        return Word(set, out var s) && Ready().ResultByte(Handle(), out value, Win1252.ToCString(name), s) != 0;
    }

    public bool ResultInt(string name, int set, out short value)
    {
        value = 0;
        return Word(set, out var s) && Ready().ResultInt(Handle(), out value, Win1252.ToCString(name), s) != 0;
    }

    public bool ResultWord(string name, int set, out ushort value)
    {
        value = 0;
        return Word(set, out var s) && Ready().ResultWord(Handle(), out value, Win1252.ToCString(name), s) != 0;
    }

    public bool ResultLong(string name, int set, out int value)
    {
        value = 0;
        return Word(set, out var s) && Ready().ResultLong(Handle(), out value, Win1252.ToCString(name), s) != 0;
    }

    public bool ResultDWord(string name, int set, out uint value)
    {
        value = 0;
        return Word(set, out var s) && Ready().ResultDWord(Handle(), out value, Win1252.ToCString(name), s) != 0;
    }

    public bool ResultReal(string name, int set, out double value)
    {
        value = 0;
        return Word(set, out var s) && Ready().ResultReal(Handle(), out value, Win1252.ToCString(name), s) != 0;
    }

    public bool ResultText(string name, int set, out string value)
    {
        value = "";
        if (!Word(set, out var s))
        {
            return false;
        }

        var buffer = new byte[APIMAXTEXT + Slack];

        // Empty format: the text result as it is.
        if (Ready().ResultText(Handle(), buffer, Win1252.ToCString(name), s, EmptyCString) == 0)
        {
            return false;
        }

        value = Win1252.FromCString(buffer.AsSpan(0, APIMAXTEXT));
        return true;
    }

    public bool ResultBinary(string name, int set, out byte[] value)
    {
        value = [];
        if (!Word(set, out var s))
        {
            return false;
        }

        var buffer = new byte[APIMAXBINARY + Slack];
        if (Ready().ResultBinary(Handle(), buffer, out var length, Win1252.ToCString(name), s) == 0)
        {
            return false;
        }

        value = buffer.AsSpan(0, Math.Min((int)length, APIMAXBINARY)).ToArray();
        return true;
    }

    private static string? CheckPath(string path)
    {
        // Full path only: never a bare name that the loader would look up on the search path.
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return $"ediabasDll '{path}' is not a full path";
        }

        return File.Exists(path) ? null : $"{path} does not exist";
    }

    /// <summary>Whether a module with the DLL's file name is mapped in this process. Best effort.</summary>
    private bool IsMapped()
    {
        var name = Path.GetFileName(_dllPath);
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            foreach (System.Diagnostics.ProcessModule m in self.Modules)
            {
                using (m)
                {
                    if (string.Equals(m.ModuleName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Cannot tell; the release is still reported as done.
        }

        return false;
    }

    private static bool Word(int value, out ushort word)
    {
        word = 0;
        if (value is < 0 or > ushort.MaxValue)
        {
            return false;
        }

        word = (ushort)value;
        return true;
    }

    private Functions Ready()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
        {
            throw new InvalidOperationException("api32.dll called from a thread other than the EDIABAS runner thread");
        }

        return _f ?? throw new InvalidOperationException("api32.dll is not loaded");
    }

    private uint Handle() =>
        _haveHandle ? _handle : throw new InvalidOperationException("EDIABAS call without a handle from apiInit");

    /// <summary>Delegates for every export, created at each load and dropped before each unload.</summary>
    private sealed class Functions(Dictionary<string, IntPtr> a)
    {
        public InitFn Init { get; } = Get<InitFn>(a, "__apiInit");
        public HandleFn End { get; } = Get<HandleFn>(a, "__apiEnd");
        public JobFn Job { get; } = Get<JobFn>(a, "__apiJob");
        public HandleIntFn State { get; } = Get<HandleIntFn>(a, "__apiState");
        public HandleFn Break { get; } = Get<HandleFn>(a, "__apiBreak");
        public HandleIntFn ErrorCode { get; } = Get<HandleIntFn>(a, "__apiErrorCode");
        public ErrorTextFn ErrorText { get; } = Get<ErrorTextFn>(a, "__apiErrorText");
        public ResultSetsFn ResultSets { get; } = Get<ResultSetsFn>(a, "__apiResultSets");
        public ResultNumberFn ResultNumber { get; } = Get<ResultNumberFn>(a, "__apiResultNumber");
        public ResultNameFn ResultName { get; } = Get<ResultNameFn>(a, "__apiResultName");
        public ResultFormatFn ResultFormat { get; } = Get<ResultFormatFn>(a, "__apiResultFormat");
        public ResultCharFn ResultChar { get; } = Get<ResultCharFn>(a, "__apiResultChar");
        public ResultByteFn ResultByte { get; } = Get<ResultByteFn>(a, "__apiResultByte");
        public ResultIntFn ResultInt { get; } = Get<ResultIntFn>(a, "__apiResultInt");
        public ResultWordFn ResultWord { get; } = Get<ResultWordFn>(a, "__apiResultWord");
        public ResultLongFn ResultLong { get; } = Get<ResultLongFn>(a, "__apiResultLong");
        public ResultDWordFn ResultDWord { get; } = Get<ResultDWordFn>(a, "__apiResultDWord");
        public ResultRealFn ResultReal { get; } = Get<ResultRealFn>(a, "__apiResultReal");
        public ResultTextFn ResultText { get; } = Get<ResultTextFn>(a, "__apiResultText");
        public ResultBinaryFn ResultBinary { get; } = Get<ResultBinaryFn>(a, "__apiResultBinary");

        private static T Get<T>(Dictionary<string, IntPtr> a, string name)
            where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(a[name]);
    }
}
