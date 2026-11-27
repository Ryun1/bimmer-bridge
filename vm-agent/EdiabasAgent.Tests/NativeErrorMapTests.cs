using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// The native engine (ediabasd) reports libkline's reasons as reserved codes 9001-9007.
public class NativeErrorMapTests
{
    [Theory]
    [InlineData(NativeErrorCodes.NoDevice, ApiErrors.CableNotFound, 502)]
    [InlineData(NativeErrorCodes.Locked, ApiErrors.CableInUse, 409)]
    [InlineData(NativeErrorCodes.NotFtdi, ApiErrors.CableUnsupported, 502)]
    [InlineData(NativeErrorCodes.Permission, ApiErrors.CablePermission, 502)]
    [InlineData(NativeErrorCodes.GateRefused, ApiErrors.FrameRefused, 403)]
    [InlineData(NativeErrorCodes.NeedsCar, ApiErrors.NeedsCar, 409)]
    [InlineData(NativeErrorCodes.EchoMismatch, ApiErrors.EdiabasError, 502)]
    public void NativeCode_MapsToBridgeError(int code, string expected, int status)
    {
        var f = EdiabasErrorMap.Failure(code, "x");
        Assert.Equal(expected, f.Error.Code);
        Assert.Equal(status, ApiErrors.Status(f.Error.Code));
        var details = Assert.IsType<EdiabasErrorDetails>(f.Error.Details);
        Assert.Equal(code, details.ErrorCode);
    }

    [Fact]
    public void NativeCodes_AreTheDocumentedNumbers()
    {
        Assert.Equal(9001, NativeErrorCodes.NoDevice);
        Assert.Equal(9002, NativeErrorCodes.Locked);
        Assert.Equal(9003, NativeErrorCodes.NotFtdi);
        Assert.Equal(9004, NativeErrorCodes.Permission);
        Assert.Equal(9005, NativeErrorCodes.GateRefused);
        Assert.Equal(9006, NativeErrorCodes.NeedsCar);
        Assert.Equal(9007, NativeErrorCodes.EchoMismatch);
    }

    [Fact]
    public void EchoMismatch_TellsTheUserToCheckTheSwitch()
    {
        Assert.Contains("K-line", EdiabasErrorMap.Failure(NativeErrorCodes.EchoMismatch, "IFH-0003").Error.Message);
    }

    [Fact]
    public void Ifh0018_StillCableNotFound()
    {
        Assert.Equal(ApiErrors.CableNotFound, EdiabasErrorMap.Failure(28, "IFH-0018").Error.Code);
    }
}
