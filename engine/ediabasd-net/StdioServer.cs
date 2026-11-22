using System.Text.Json;
using System.Text.Json.Nodes;
using EdiabasAgent.Core;

namespace Ediabasd;

/// <summary>
/// JSON lines on stdin/stdout: request <c>{"id", "method", "params"}</c>, response
/// <c>{"id", "ok", "result" | "error"}</c>. One request at a time, never resent. A line that is
/// not a request gets a <c>bad_request</c> with <c>"id": null</c> and the server carries on.
/// After a request whose job did not stop (the host is stuck) the response is written and Run
/// returns 3: the process exits and the MCP server starts a fresh one for the next request.
/// </summary>
public sealed class StdioServer(Front front, EdiabasdHost host)
{
    public const int StuckExitCode = 3;

    public int Run(TextReader input, TextWriter output)
    {
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var (id, response) = HandleLine(line);
            Write(output, id, response);
            if (host.Stuck)
            {
                host.Log.Warn("a job did not stop after Break; exiting so a fresh ediabasd is started");
                return StuckExitCode;
            }
        }

        return 0;
    }

    (JsonNode? Id, FrontResponse Response) HandleLine(string line)
    {
        if (line.Length > RequestParsing.MaxBodyBytes)
        {
            return (null, Bad($"a request line must be at most {RequestParsing.MaxBodyBytes} bytes."));
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return (null, Bad("each line must be one JSON object: {\"id\", \"method\", \"params\"}."));
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, Bad("each line must be one JSON object: {\"id\", \"method\", \"params\"}."));
            }

            JsonNode? id = root.TryGetProperty("id", out var idEl) ? JsonNode.Parse(idEl.GetRawText()) : null;
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name is not ("id" or "method" or "params"))
                {
                    return (id, Bad($"unknown key \"{AuditLine.Neutralise(p.Name)}\" in the request."));
                }
            }

            if (!root.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String)
            {
                return (id, Bad("method is required (a string)."));
            }

            JsonElement? parameters = root.TryGetProperty("params", out var pe) ? pe.Clone() : null;
            return (id, front.Handle(m.GetString(), parameters));
        }
    }

    static FrontResponse Bad(string message) => FrontResponse.Failure(RequestParsing.Bad("request", message));

    static void Write(TextWriter output, JsonNode? id, FrontResponse r)
    {
        var obj = new JsonObject { ["id"] = id, ["ok"] = r.Ok };
        if (r.Ok)
        {
            obj["result"] = JsonSerializer.SerializeToNode(r.Result, r.Result?.GetType() ?? typeof(object), ApiJson.Options);
        }
        else
        {
            obj["error"] = JsonSerializer.SerializeToNode(r.Error, ApiJson.Options);
        }

        output.Write(obj.ToJsonString(ApiJson.Options));
        output.Write('\n');
        output.Flush();
    }
}
