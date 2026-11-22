using System.Text.Json;
using System.Text.Json.Nodes;
using EdiabasAgent.Core;

namespace Ediabasd;

/// <summary>
/// <code>
/// ediabasd serve                                   JSON lines on stdin/stdout (the MCP server's way in)
/// ediabasd ecus [FILTER]
/// ediabasd describe ECU [JOB] [--filter X] [--no-comments]
/// ediabasd job ECU JOB [--args A] [--results R] [--timeout N]
/// ediabasd vehicle
/// ediabasd health
///   --config PATH on any command (default $XDG_CONFIG_HOME/bimmer-bridge/config.json)
/// </code>
/// Prints the response JSON indented. Exit 0 ok, 1 error response, 2 usage, 3 a job did not
/// stop (stuck), 78 config error. Every command goes through the same service and policy.
/// </summary>
public static class Cli
{
    public const int Ok = 0;
    public const int ErrorResponse = 1;
    public const int Usage = 2;
    public const int ConfigError = 78;

    const string UsageText =
        "usage: ediabasd serve | ecus [FILTER] | describe ECU [JOB] [--filter X] [--no-comments] |\n" +
        "       job ECU JOB [--args A] [--results R] [--timeout N] | vehicle | health   [--config PATH]";

    static readonly JsonSerializerOptions Pretty = new(ApiJson.Options) { WriteIndented = true };

    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error, IReadOnlyDictionary<string, string?> env)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--no-comments")
            {
                options[a] = null;
            }
            else if (a is "--config" or "--filter" or "--args" or "--results" or "--timeout")
            {
                if (i + 1 >= args.Length)
                {
                    return UsageError(error, $"{a} needs a value");
                }

                options[a] = args[++i];
            }
            else if (a.StartsWith("--", StringComparison.Ordinal))
            {
                return UsageError(error, $"unknown option {a}");
            }
            else
            {
                positional.Add(a);
            }
        }

        if (positional.Count == 0)
        {
            return UsageError(error, "no command");
        }

        var command = positional[0];
        var rest = positional.Skip(1).ToList();
        string method;
        var p = new JsonObject();
        switch (command)
        {
            case "serve":
                if (rest.Count != 0) return UsageError(error, "serve takes no arguments");
                method = "serve";
                break;
            case "ecus":
                if (rest.Count > 1) return UsageError(error, "ecus takes at most a filter");
                method = "ecus";
                if (rest.Count == 1) p["filter"] = rest[0];
                break;
            case "describe":
                if (rest.Count is < 1 or > 2) return UsageError(error, "describe needs ECU and optionally JOB");
                method = "describe";
                p["ecu"] = rest[0];
                if (rest.Count == 2) p["job"] = rest[1];
                if (options.TryGetValue("--filter", out var f)) p["filter"] = f;
                if (options.ContainsKey("--no-comments")) p["comments"] = false;
                break;
            case "job":
                if (rest.Count != 2) return UsageError(error, "job needs ECU and JOB");
                method = "job";
                p["ecu"] = rest[0];
                p["job"] = rest[1];
                if (options.TryGetValue("--args", out var ar)) p["args"] = ar;
                if (options.TryGetValue("--results", out var re)) p["results"] = re;
                if (options.TryGetValue("--timeout", out var t))
                {
                    if (!int.TryParse(t, out var seconds)) return UsageError(error, "--timeout needs a whole number of seconds");
                    p["timeoutSeconds"] = seconds;
                }

                break;
            case "vehicle" or "health":
                if (rest.Count != 0) return UsageError(error, $"{command} takes no arguments");
                method = command;
                break;
            default:
                return UsageError(error, $"unknown command {command}");
        }

        var (config, errors) = NativeConfig.Load(options.GetValueOrDefault("--config"), env);
        if (config is null)
        {
            foreach (var e in errors)
            {
                error.WriteLine("ediabasd: " + e);
            }

            return ConfigError;
        }

        using var host = new EdiabasdHost(config);
        if (method == "serve")
        {
            var server = new StdioServer(new Front(host, new AuditLog(config.AuditPath, "stdio")), host);
            return server.Run(input, output);
        }

        var front = new Front(host, new AuditLog(config.AuditPath, "cli"));
        using var doc = JsonDocument.Parse(p.ToJsonString());
        var r = front.Handle(method, doc.RootElement);
        output.WriteLine(r.Ok
            ? JsonSerializer.Serialize(r.Result, r.Result?.GetType() ?? typeof(object), Pretty)
            : JsonSerializer.Serialize(new ErrorBody(r.Error!), Pretty));
        output.Flush();
        if (host.Stuck)
        {
            return StdioServer.StuckExitCode;
        }

        return r.Ok ? Ok : ErrorResponse;
    }

    static int UsageError(TextWriter error, string message)
    {
        error.WriteLine("ediabasd: " + message);
        error.WriteLine(UsageText);
        return Usage;
    }
}
