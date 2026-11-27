using System.Text;

namespace EdiabasLib.Native.Tests;

/// Builds libkline replay recordings (JSON lines) in a temp directory.
public sealed class Recordings : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("kline-rec-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }

    /// A BMW-FAST exchange as the cable sees it: the request sent, its echo, then the response.
    /// Both telegrams get their checksum (sum of the bytes) appended.
    public string BmwFastExchange(byte[] request, byte[] response)
    {
        var req = WithChecksum(request);
        var lines = new StringBuilder();
        lines.Append("{\"t_us\":0,\"op\":\"send\",\"hex\":\"").Append(Convert.ToHexString(req)).Append("\"}\n");
        lines.Append("{\"t_us\":1,\"op\":\"recv\",\"hex\":\"").Append(Convert.ToHexString(req)).Append("\"}\n");
        if (response != null)
            lines.Append("{\"t_us\":2,\"op\":\"recv\",\"hex\":\"").Append(Convert.ToHexString(WithChecksum(response))).Append("\"}\n");
        return Write(lines.ToString());
    }

    public string Empty() => Write("");

    public string Write(string content)
    {
        var path = Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    public static byte[] WithChecksum(byte[] telegram)
    {
        var sum = 0;
        foreach (var b in telegram) sum += b;
        return [.. telegram, (byte)sum];
    }
}
