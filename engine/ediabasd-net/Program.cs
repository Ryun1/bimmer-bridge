using System.Text;

namespace Ediabasd;

public static class Program
{
    public static int Main(string[] args)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };
        var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        try
        {
            return Cli.Run(args, stdin, stdout, Console.Error, NativeConfig.ProcessEnv());
        }
        finally
        {
            stdout.Flush();
        }
    }
}
