using System.Text;

namespace EdiabasAgent.Core;

/// <summary>
/// EDIABAS strings are Windows-1252 C strings, not UTF-8. Encoding never uses best-fit
/// mappings: a character 1252 cannot hold becomes '?', so a look-alike can never turn into a
/// different ASCII name on the way to the DLL.
/// </summary>
public static class Win1252
{
    public static Encoding Encoding { get; } = Create();

    /// <summary>The bytes of <paramref name="s"/> plus a terminating NUL. Embedded NULs are refused.</summary>
    public static byte[] ToCString(string s)
    {
        if (s.Contains('\0'))
        {
            throw new ArgumentException("string contains NUL", nameof(s));
        }

        var bytes = new byte[Encoding.GetByteCount(s) + 1];
        Encoding.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }

    /// <summary>Text up to the first NUL (or the whole buffer if there is none).</summary>
    public static string FromCString(ReadOnlySpan<byte> buffer)
    {
        var end = buffer.IndexOf((byte)0);
        return Encoding.GetString(end < 0 ? buffer : buffer[..end]);
    }

    private static Encoding Create()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));
    }
}
