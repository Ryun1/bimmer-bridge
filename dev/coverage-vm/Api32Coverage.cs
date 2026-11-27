using System.Runtime.InteropServices;
using EdiabasAgent.Core;

namespace CoverageVm;

/// <summary>
/// The api32.dll calls the coverage run needs, through its handle-taking <c>__api…</c> exports
/// (as the VM agent's Api32.cs; signatures from Api.h / upstream's Api32/ApiDll.cpp).
/// 32-bit stdcall. Loaded from a full path only.
/// </summary>
internal sealed class Api32Coverage : IDisposable
{
    const int APIMAXRESULT = 256;
    const int APIMAXTEXT = 1024;
    const int APIMAXBINARY = 1024;
    const int Slack = 256;

    public const int Busy = 0;
    public const int Ready = 1;
    public const int Break = 2;
    public const int Error = 3;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int InitExtFn(out uint h, byte[] ifh, byte[] unit, byte[] app, byte[] config);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void HandleFn(uint h);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int HandleIntFn(uint h);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetConfigFn(uint h, byte[] name, byte[] value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr ErrorTextFn(uint h, [In, Out] byte[] buffer, int length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void JobFn(uint h, byte[] ecu, byte[] job, byte[] para, byte[] result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void JobDataFn(uint h, byte[] ecu, byte[] job, byte[] para, int paralen, byte[] result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void JobExtFn(uint h, byte[] ecu, byte[] job, byte[] std, int stdlen, byte[] para, int paralen, byte[] result, int reserved);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResultSetsFn(uint h, out ushort sets);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResultNumberFn(uint h, out ushort count, ushort set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResultNameFn(uint h, [In, Out] byte[] name, ushort index, ushort set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResultFormatFn(uint h, out int format, byte[] name, ushort set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResultCharFn(uint h, out sbyte v, byte[] name, ushort set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResultByteFn(uint h, out byte v, byte[] name, ushort set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResultIntFn(uint h, out short v, byte[] name, ushort set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResultWordFn(uint h, out ushort v, byte[] name, ushort set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResultLongFn(uint h, out int v, byte[] name, ushort set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResultDWordFn(uint h, out uint v, byte[] name, ushort set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResultRealFn(uint h, out double v, byte[] name, ushort set);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResultTextFn(uint h, [In, Out] byte[] buffer, byte[] name, ushort set, byte[] format);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResultBinaryFn(uint h, [In, Out] byte[] buffer, out ushort length, byte[] name, ushort set);

    readonly IntPtr _lib;
    readonly InitExtFn _initExt;
    readonly HandleFn _end;
    readonly HandleIntFn _state;
    readonly HandleIntFn _errorCode;
    readonly ErrorTextFn _errorText;
    readonly SetConfigFn _setConfig;
    readonly JobFn _job;
    readonly JobDataFn _jobData;
    readonly JobExtFn _jobExt;
    readonly ResultSetsFn _sets;
    readonly ResultNumberFn _number;
    readonly ResultNameFn _name;
    readonly ResultFormatFn _format;
    readonly ResultCharFn _char;
    readonly ResultByteFn _byte;
    readonly ResultIntFn _int;
    readonly ResultWordFn _word;
    readonly ResultLongFn _long;
    readonly ResultDWordFn _dword;
    readonly ResultRealFn _real;
    readonly ResultTextFn _text;
    readonly ResultBinaryFn _binary;
    uint _h;
    bool _open;

    public Api32Coverage(string dllPath)
    {
        if (!Path.IsPathFullyQualified(dllPath)) throw new ArgumentException("api32.dll must be given by its full path");
        _lib = NativeLibrary.Load(dllPath);
        T F<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));
        _initExt = F<InitExtFn>("__apiInitExt");
        _end = F<HandleFn>("__apiEnd");
        _state = F<HandleIntFn>("__apiState");
        _errorCode = F<HandleIntFn>("__apiErrorCode");
        _errorText = F<ErrorTextFn>("__apiErrorText");
        _setConfig = F<SetConfigFn>("__apiSetConfig");
        _job = F<JobFn>("__apiJob");
        _jobData = F<JobDataFn>("__apiJobData");
        _jobExt = F<JobExtFn>("__apiJobExt");
        _sets = F<ResultSetsFn>("__apiResultSets");
        _number = F<ResultNumberFn>("__apiResultNumber");
        _name = F<ResultNameFn>("__apiResultName");
        _format = F<ResultFormatFn>("__apiResultFormat");
        _char = F<ResultCharFn>("__apiResultChar");
        _byte = F<ResultByteFn>("__apiResultByte");
        _int = F<ResultIntFn>("__apiResultInt");
        _word = F<ResultWordFn>("__apiResultWord");
        _long = F<ResultLongFn>("__apiResultLong");
        _dword = F<ResultDWordFn>("__apiResultDWord");
        _real = F<ResultRealFn>("__apiResultReal");
        _text = F<ResultTextFn>("__apiResultText");
        _binary = F<ResultBinaryFn>("__apiResultBinary");
    }

    static byte[] C(string s) => Win1252.ToCString(s);

    public bool InitExt(string ifh, string config)
    {
        var ok = _initExt(out _h, C(ifh), C(""), C("bimmer-bridge-coverage"), C(config)) != 0;
        _open = true;
        return ok;
    }

    public bool SetConfig(string name, string value) => _setConfig(_h, C(name), C(value)) != 0;

    public void End()
    {
        if (_open) _end(_h);
        _open = false;
    }

    public int State() => _state(_h);

    public int ErrorCode() => _errorCode(_h);

    public string ErrorText()
    {
        var b = new byte[APIMAXTEXT + Slack];
        _errorText(_h, b, APIMAXTEXT);
        return Win1252.FromCString(b.AsSpan(0, APIMAXTEXT));
    }

    public void Job(string ecu, string job, string para, string result) => _job(_h, C(ecu), C(job), C(para), C(result));

    public void JobData(string ecu, string job, byte[] para, string result) => _jobData(_h, C(ecu), C(job), para, para.Length, C(result));

    public void JobExt(string ecu, string job, byte[] std, byte[] para, string result) =>
        _jobExt(_h, C(ecu), C(job), std, std.Length, para, para.Length, C(result), 0);

    public bool ResultSets(out int n)
    {
        var ok = _sets(_h, out var s) != 0;
        n = s;
        return ok;
    }

    public bool ResultNumber(int set, out int n)
    {
        var ok = _number(_h, out var c, (ushort)set) != 0;
        n = c;
        return ok;
    }

    public bool ResultName(int index, int set, out string name)
    {
        var b = new byte[APIMAXRESULT + Slack];
        var ok = _name(_h, b, (ushort)index, (ushort)set) != 0;
        name = ok ? Win1252.FromCString(b.AsSpan(0, APIMAXRESULT)) : "";
        return ok;
    }

    public bool ResultFormat(string name, int set, out int format) => _format(_h, out format, C(name), (ushort)set) != 0;

    /// The value for its API format, as a JSON-ready object (numbers, text, hex), or null.
    public object? Value(string name, int set, int format)
    {
        var n = C(name);
        var s = (ushort)set;
        switch (format)
        {
            case 0: return _char(_h, out var c, n, s) != 0 ? c : null;
            case 1: return _byte(_h, out var b, n, s) != 0 ? b : null;
            case 2: return _int(_h, out var i, n, s) != 0 ? i : null;
            case 3: return _word(_h, out var w, n, s) != 0 ? w : null;
            case 4: return _long(_h, out var l, n, s) != 0 ? l : null;
            case 5: return _dword(_h, out var d, n, s) != 0 ? d : null;
            case 6: return Text(name, set, "");
            case 7:
            {
                var buf = new byte[APIMAXBINARY + Slack];
                return _binary(_h, buf, out var len, n, s) != 0 ? Coverage.Hex.Of(buf.AsSpan(0, Math.Min((int)len, APIMAXBINARY)).ToArray()) : null;
            }
            case 8: return _real(_h, out var r, n, s) != 0 && double.IsFinite(r) ? r : null;
            default: return null;
        }
    }

    public string? Text(string name, int set, string format)
    {
        var b = new byte[APIMAXTEXT + Slack];
        return _text(_h, b, C(name), (ushort)set, C(format)) != 0 ? Win1252.FromCString(b.AsSpan(0, APIMAXTEXT)) : null;
    }

    public void Dispose()
    {
        End();
        NativeLibrary.Free(_lib);
    }
}
