namespace EdiabasAgent.Core;

/// <summary>
/// The EDIABAS error codes the agent reports with a code of their own. Every code not listed
/// here is reported as <c>ediabas_error</c>.
/// </summary>
public static class EdiabasErrorMap
{
    // Settled at the desk, 2026-11-21, cable unplugged: loading a real SGBD gives error 28,
    // "IFH-0018: INITIALIZATION ERROR". Which code a busy port gives is not known yet.
    private static readonly Dictionary<int, (string Code, string Message)> Table = new()
    {
        [28] = (ApiErrors.CableNotFound,
                "EDIABAS could not initialise the interface; the cable is probably not connected to the VM."),
    };

    /// <summary>The failure for an EDIABAS error code and text, mapped through the table.</summary>
    public static ApiFailure Failure(int code, string text) =>
        Table.TryGetValue(code, out var m)
            ? ApiFailure.Of(m.Code, $"{m.Message} (EDIABAS error {code}: {text})", new EdiabasErrorDetails(code, text))
            : ApiFailure.Of(ApiErrors.EdiabasError, $"EDIABAS reported error {code}: {text}", new EdiabasErrorDetails(code, text));
}
