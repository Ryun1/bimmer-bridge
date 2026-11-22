using System.Text;
using EdiabasAgent.Core;
using EdiabasLib;
using EdiabasLib.Native;

namespace Ediabasd;

/// Whether the current request may reach the car (job, jobs, vehicle) or must not (ecus,
/// describe). Only a live request may resolve a group file at the car.
public enum RequestMode
{
    Offline,
    Live,
}

/// <summary>
/// IEdiabasApi over two EdiabasNet instances (M1 plan, decisions 1-5):
/// <list type="bullet">
/// <item>jobs whose name starts with <c>_</c> run on the <b>offline</b> instance, whose interface
/// opens nothing and which never runs INITIALISIERUNG for them (<c>NoInitForVJobs = true</c>);</item>
/// <item>every other job runs on the <b>live</b> instance on libkline, created fresh for each
/// request, so EdiabasNet runs the SGBD's INITIALISIERUNG before the first job, as real
/// EDIABAS does. The port opens only when the SGBD's own xconnect asks for it.</item>
/// </list>
/// One job at a time, on its own worker thread; <see cref="EdiabasFactory.Activate"/> binds
/// EdiabasNet's static register table to the instance about to run.
/// libkline's reasons replace EDIABAS's error codes with <see cref="NativeErrorCodes"/>.
/// </summary>
public sealed class NativeEdiabasApi : IEdiabasApi, IDisposable
{
    static readonly Encoding Win1252 = CreateWin1252();

    readonly string _ecuPath;
    readonly Func<ILiveCable> _liveCable;
    readonly IVariantCache _variants;
    readonly string? _traceDir;

    EdiabasNet? _offline;
    EdiabasNet? _live;
    ILiveCable? _cable;

    // Job state, written by the worker and read by the caller's thread.
    volatile int _state = ApiState.Ready;
    volatile bool _abort;
    Thread? _worker;
    List<Dictionary<string, EdiabasNet.ResultData>>? _results;
    int _errorCode;
    string _errorText = "";

    public NativeEdiabasApi(string ecuPath, Func<ILiveCable> liveCable, IVariantCache variants, string? traceDir)
    {
        _ecuPath = ecuPath;
        _liveCable = liveCable;
        _variants = variants;
        _traceDir = traceDir;
    }

    /// Set by the front for each request; see <see cref="RequestMode"/>.
    public RequestMode Mode { get; set; } = RequestMode.Offline;

    internal bool LiveNoInitForVJobs => _live?.NoInitForVJobs ?? false;

    internal bool OfflineNoInitForVJobs => _offline?.NoInitForVJobs ?? true;

    // ------------------------------------------------------------ lifecycle

    public bool TryLoad(out string? reason)
    {
        try
        {
            KlineLibrary.Version();
            EdiabasFactory.CheckActivateSupported();
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            reason = "libkline could not be loaded: " + ex.Message;
            return false;
        }
    }

    /// Nothing to unload: libkline stays loaded for the life of the process.
    public void Unload()
    {
    }

    /// Starts a request. The live instance is created at the request's first live job, so a
    /// describe never creates it.
    public bool Init()
    {
        EnsureIdle();
        DisposeLive();
        return true;
    }

    /// Ends a request: the live instance and its port are closed.
    public void End()
    {
        EnsureIdle();
        DisposeLive();
    }

    public void Dispose()
    {
        if (_worker is { IsAlive: true })
        {
            // A job that never stopped: leave its instance alone (the host exits).
            return;
        }

        DisposeLive();
        _offline?.Dispose();
        _offline = null;
    }

    void DisposeLive()
    {
        _live?.Dispose();
        _live = null;
        _cable?.Dispose();
        _cable = null;
    }

    void EnsureIdle()
    {
        if (_state == ApiState.Busy)
        {
            throw new InvalidOperationException("an EDIABAS job is still running");
        }
    }

    EdiabasNet Offline()
    {
        if (_offline == null)
        {
            _offline = EdiabasFactory.Create(_ecuPath, new OfflineInterface(), noInitForVJobs: true);
            _offline.AbortJobFunc = () => _abort;
        }

        return _offline;
    }

    EdiabasNet Live()
    {
        if (_live == null)
        {
            _cable = _liveCable();
            _live = EdiabasFactory.Create(_ecuPath, _cable.Interface, noInitForVJobs: false);
            _live.AbortJobFunc = () => _abort;
            if (_traceDir != null)
            {
                _live.SetConfigProperty("TracePath", _traceDir);
                _live.SetConfigProperty("IfhTrace", "3");
                _live.SetConfigProperty("ApiTrace", "1");
            }
        }

        return _live;
    }

    // ------------------------------------------------------------ jobs

    public void Job(string ecu, string job, string parameters, string results)
    {
        EnsureIdle();
        var offline = job.StartsWith('_');
        var ed = offline ? Offline() : Live();
        var cable = offline ? null : _cable;

        _results = null;
        _errorCode = 0;
        _errorText = "";
        _abort = false;
        _state = ApiState.Busy;
        _worker = new Thread(() => Run(ed, cable, ecu, job, parameters, results))
        {
            IsBackground = true,
            Name = "ediabasd job",
        };
        _worker.Start();
    }

    void Run(EdiabasNet ed, ILiveCable? cable, string ecu, string job, string parameters, string results)
    {
        var state = ApiState.Ready;
        try
        {
            EdiabasFactory.Activate(ed);
            ed.ArgBinary = Win1252.GetBytes(parameters ?? "");
            ed.ArgBinaryStd = null;
            ed.ResultsRequests = results ?? "";
            ed.ResolveSgbdFile(ecu);
            ed.ExecuteJob(job);
            _results = ed.ResultSets;
        }
        catch (Exception ex)
        {
            if (_abort)
            {
                state = ApiState.Break;
            }
            else
            {
                state = ApiState.Error;
                var code = ed.ErrorCodeLast != EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE
                    ? ed.ErrorCodeLast
                    : ex is EdiabasNet.EdiabasNetException ene ? ene.ErrorCode : EdiabasNet.ErrorCodes.EDIABAS_SYS_0000;
                SetError((int)code, EdiabasNet.GetErrorDescription(code) ?? ex.Message);
            }
        }

        if (cable != null)
        {
            state = ApplyCableReason(state, cable.Reason);
        }

        _state = state;
    }

    int ApplyCableReason(int state, KlineReason reason)
    {
        var m = MapCableReason(state, _errorCode, _errorText, reason);
        if (m.State == ApiState.Error && state != ApiState.Error)
        {
            _results = null;
        }

        SetError(m.Code, m.Text);
        return m.State;
    }

    /// libkline's reason decides the error whenever it names one: a gate refusal fails the
    /// job whatever the SGBD made of it (its trap mask can swallow the send error); a connect
    /// reason replaces EDIABAS's IFH-0018; IFH-0003 with no reason is an echo mismatch, which
    /// on a K+DCAN cable usually means the switch is not on K-line.
    internal static (int State, int Code, string Text) MapCableReason(int state, int code, string text, KlineReason reason)
    {
        if (reason == KlineReason.GateRefused)
        {
            return (ApiState.Error, NativeErrorCodes.GateRefused, WithReason(text, reason));
        }

        if (state != ApiState.Error)
        {
            return (state, code, text);
        }

        var native = reason switch
        {
            KlineReason.NoDevice => NativeErrorCodes.NoDevice,
            KlineReason.Locked => NativeErrorCodes.Locked,
            KlineReason.NotFtdi => NativeErrorCodes.NotFtdi,
            KlineReason.Permission => NativeErrorCodes.Permission,
            _ => 0,
        };
        if (native != 0)
        {
            return (state, native, WithReason(text, reason));
        }

        if (code == (int)EdiabasNet.ErrorCodes.EDIABAS_IFH_0003 && reason == KlineReason.Ok)
        {
            return (state, NativeErrorCodes.EchoMismatch, text);
        }

        return (state, code, text);
    }

    static string WithReason(string text, KlineReason reason) =>
        (text.Length > 0 ? text + " " : "") + "(libkline: " + Snake(reason) + ")";

    static string Snake(KlineReason r) => r switch
    {
        KlineReason.NoDevice => "no_device",
        KlineReason.NotFtdi => "not_ftdi",
        KlineReason.GateRefused => "gate_refused",
        KlineReason.ReplayMismatch => "replay_mismatch",
        _ => r.ToString().ToLowerInvariant(),
    };

    void SetError(int code, string text)
    {
        _errorCode = code;
        _errorText = text;
    }

    public int State() => _state;

    /// Stops the running job: EdiabasNet's abort (checked between opcodes) and libkline's
    /// transmit cancel (which ends a send or receive blocked on the cable).
    public void Break()
    {
        if (_state != ApiState.Busy)
        {
            return;
        }

        _abort = true;
        _cable?.Cancel();
    }

    public int ErrorCode() => _errorCode;

    public string ErrorText() => _errorText;

    // ------------------------------------------------------------ results

    public bool ResultSets(out int sets)
    {
        sets = 0;
        if (_state != ApiState.Ready || _results is not { Count: >= 1 })
        {
            return false;
        }

        sets = _results.Count - 1;
        return true;
    }

    public bool ResultNumber(int set, out int count)
    {
        count = 0;
        var dict = Set(set);
        if (dict == null)
        {
            return false;
        }

        count = dict.Count;
        return true;
    }

    public bool ResultName(int index, int set, out string name)
    {
        name = "";
        var dict = Set(set);
        if (dict == null || index < 1 || index > dict.Count)
        {
            return false;
        }

        name = dict.Values.ElementAt(index - 1).Name;
        return true;
    }

    public bool ResultFormat(string name, int set, out int format)
    {
        format = 0;
        var rd = Data(name, set);
        if (rd == null)
        {
            return false;
        }

        switch (rd.ResType)
        {
            case EdiabasNet.ResultType.TypeB: format = ApiResultFormat.Byte; return true;
            case EdiabasNet.ResultType.TypeW: format = ApiResultFormat.Word; return true;
            case EdiabasNet.ResultType.TypeD: format = ApiResultFormat.DWord; return true;
            case EdiabasNet.ResultType.TypeC: format = ApiResultFormat.Char; return true;
            case EdiabasNet.ResultType.TypeI: format = ApiResultFormat.Integer; return true;
            case EdiabasNet.ResultType.TypeL: format = ApiResultFormat.Long; return true;
            case EdiabasNet.ResultType.TypeR: format = ApiResultFormat.Real; return true;
            case EdiabasNet.ResultType.TypeS: format = ApiResultFormat.Text; return true;
            case EdiabasNet.ResultType.TypeY: format = ApiResultFormat.Binary; return true;
            default: return false; // 64-bit types: EDIABAS 7.3's API has no format for them.
        }
    }

    public bool ResultChar(string name, int set, out sbyte value)
    {
        var ok = Int64(name, set, out var v);
        value = (sbyte)v;
        return ok;
    }

    public bool ResultByte(string name, int set, out byte value)
    {
        var ok = Int64(name, set, out var v);
        value = (byte)v;
        return ok;
    }

    public bool ResultInt(string name, int set, out short value)
    {
        var ok = Int64(name, set, out var v);
        value = (short)v;
        return ok;
    }

    public bool ResultWord(string name, int set, out ushort value)
    {
        var ok = Int64(name, set, out var v);
        value = (ushort)v;
        return ok;
    }

    public bool ResultLong(string name, int set, out int value)
    {
        var ok = Int64(name, set, out var v);
        value = (int)v;
        return ok;
    }

    public bool ResultDWord(string name, int set, out uint value)
    {
        var ok = Int64(name, set, out var v);
        value = (uint)v;
        return ok;
    }

    public bool ResultReal(string name, int set, out double value)
    {
        value = 0;
        var rd = Data(name, set);
        switch (rd?.OpData)
        {
            case long l: value = l; return true;
            case double d: value = d; return true;
            case string s: value = EdiabasNet.StringToFloat(s); return true;
            default: return false;
        }
    }

    public bool ResultText(string name, int set, out string value)
    {
        value = "";
        var rd = Data(name, set);
        if (rd == null)
        {
            return false;
        }

        var text = rd.OpData as string ?? EdiabasNet.FormatResult(rd, "");
        if (text == null)
        {
            return false;
        }

        value = text;
        return true;
    }

    public bool ResultBinary(string name, int set, out byte[] value)
    {
        value = [];
        var rd = Data(name, set);
        if (rd is not { ResType: EdiabasNet.ResultType.TypeY, OpData: byte[] bytes })
        {
            return false;
        }

        value = (byte[])bytes.Clone();
        return true;
    }

    Dictionary<string, EdiabasNet.ResultData>? Set(int set) =>
        _state == ApiState.Ready && _results != null && set >= 0 && set < _results.Count ? _results[set] : null;

    EdiabasNet.ResultData? Data(string name, int set)
    {
        var dict = Set(set);
        return dict != null && dict.TryGetValue(name.ToUpperInvariant(), out var rd) ? rd : null;
    }

    /// As upstream's ApiInternal getResultInt64: integers, reals truncated, text parsed.
    bool Int64(string name, int set, out long value)
    {
        value = 0;
        switch (Data(name, set)?.OpData)
        {
            case long l: value = l; return true;
            case double d: value = (long)d; return true;
            case string s: value = EdiabasNet.StringToValue(s); return true;
            default: return false;
        }
    }

    static Encoding CreateWin1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }
}
