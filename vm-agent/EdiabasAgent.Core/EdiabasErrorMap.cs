namespace EdiabasAgent.Core;

/// <summary>
/// The EDIABAS error codes the agent reports with a code of their own. Every code not listed
/// here is reported as <c>ediabas_error</c>.
/// </summary>
/// <summary>Codes the native engine reports in place of EDIABAS's own when libkline gave the
/// reason (all above EDIABAS's range). docs/api.md, "ediabasd stdio".</summary>
public static class NativeErrorCodes
{
    public const int NoDevice = 9001;
    public const int Locked = 9002;
    public const int NotFtdi = 9003;
    public const int Permission = 9004;
    public const int GateRefused = 9005;
    public const int NeedsCar = 9006;
    public const int EchoMismatch = 9007;
}

public static class EdiabasErrorMap
{
    // Settled at the desk, 2026-11-21, cable unplugged: loading a real SGBD gives error 28,
    // "IFH-0018: INITIALIZATION ERROR". Which code a busy port gives is not known yet.
    private static readonly Dictionary<int, (string Code, string Message)> Table = new()
    {
        [28] = (ApiErrors.CableNotFound,
                "EDIABAS could not initialise the interface; the cable is probably not connected to the VM."),

        // The native engine's own codes (NativeErrorCodes): libkline gave the reason.
        [NativeErrorCodes.NoDevice] = (ApiErrors.CableNotFound,
                "No K+DCAN cable was found. Plug it in (on a Mac, detach it from the VM first)."),
        [NativeErrorCodes.Locked] = (ApiErrors.CableInUse,
                "Another program is using the cable. Close it and try again."),
        [NativeErrorCodes.NotFtdi] = (ApiErrors.CableUnsupported,
                "The serial device is not an FTDI FT232R K+DCAN cable (USB 0403:6001); only that cable is supported."),
        [NativeErrorCodes.Permission] = (ApiErrors.CablePermission,
                "No permission to open the cable. On Linux, install the udev rule from kline/README.md and replug it."),
        [NativeErrorCodes.GateRefused] = (ApiErrors.FrameRefused,
                "The cable layer refused a telegram that could write to the car or reconfigure the cable; nothing more was sent in this request."),
        [NativeErrorCodes.NeedsCar] = (ApiErrors.NeedsCar,
                "This is a group file: which variant it means is found only by asking the car. Run a job on it first, then describe it."),
        [NativeErrorCodes.EchoMismatch] = (ApiErrors.EdiabasError,
                "No valid answer on the K-line (IFH-0003). Check the cable switch is on K-line and the ignition is on."),
    };

    /// <summary>The failure for an EDIABAS error code and text, mapped through the table.</summary>
    public static ApiFailure Failure(int code, string text) =>
        Table.TryGetValue(code, out var m)
            ? ApiFailure.Of(m.Code, $"{m.Message} (EDIABAS error {code}: {text})", new EdiabasErrorDetails(code, text))
            : ApiFailure.Of(ApiErrors.EdiabasError, $"EDIABAS reported error {code}: {text}", new EdiabasErrorDetails(code, text));
}
