using System.Globalization;
using System.Text.RegularExpressions;

namespace EdiabasAgent.Core;

/// <summary>
/// One interface-handler call from EDIABAS's IFH trace.
/// <para><see cref="Input"/> is the call's INPUT bytes, continuation lines included; empty for
/// <c>INPUT: --</c>; null when the trace has no INPUT line for the call.
/// <see cref="Concept"/> is the protocol in force when the call was made (null when none is
/// known). <see cref="Error"/> is the text of its <c>ERROR:</c> line, else null.
/// <see cref="Line"/> is the 1-based line of the call in the text parsed.</para>
/// </summary>
public sealed record IfhEvent(string Name, byte[]? Input, ushort? Concept, string? Error, int Line);

/// <summary>A line the parser could not read, so whatever it held cannot be checked.</summary>
public sealed record IfhTraceProblem(int Line, string Text, string Why);

/// <summary>What <see cref="IfhTrace.Parse"/> found: the calls in order, the lines it could not
/// read, how many trace lines (other than metadata) there were, and the concept in force at the
/// end (for the next segment of the same file).</summary>
public sealed record IfhTraceSegment(
    IReadOnlyList<IfhEvent> Events,
    IReadOnlyList<IfhTraceProblem> Problems,
    int TraceLines,
    ushort? EndConcept);

/// <summary>
/// Parser for the interface trace EDIABAS writes to <c>IFH.trc</c> with <c>IfhTrace</c> 3
/// (seen with EDIABAS 7.3.0). Pure: text in, calls out. Lines, CRLF or LF:
/// <code>
///         #@13;P00002600h;#                      metadata (tab-indented): ignored
/// _STD      >>>> Interface Communication Trace from … &lt;&lt;&lt;&lt;   header: ignored
/// _STD     [        16] SETECUCOMM               a call: interface prefix, [ms], name
/// _STD     [        16]  INPUT: 0F 01 00 00 …    a field of the call (INPUT, OUTPUT, ERROR, I -> D, D -> I)
/// _STD                          02 00 00 00 …    continuation of the field above: more bytes
/// </code>
/// The concept (protocol) is taken from each <c>SETECUCOMM</c>: its first two INPUT bytes,
/// little-endian (<c>0F 01</c> is 0x010F). It is forgotten (null) at <c>OPEN</c> (a new interface
/// session), and after a <c>SETECUCOMM</c> that did not end with <c>ERROR: NO ERROR</c> or whose
/// INPUT is shorter than two bytes, because then which protocol is in force is not known. Anything
/// the parser does not recognise is a <see cref="IfhTraceProblem"/>, never skipped silently.
/// </summary>
public static class IfhTrace
{
    public const string Open = "OPEN";
    public const string SetEcuComm = "SETECUCOMM";
    public const string SendEcuTel = "SENDECUTEL";
    public const string NoError = "NO ERROR";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // "<prefix> [   ms] <content>": the prefix is the interface ("_STD"), the content a call or a field.
    private static readonly Regex Stamped = new(@"^(\S+)[ ]+\[[ ]*[0-9]+\][ ](.*)$", RegexOptions.CultureInvariant, RegexTimeout);

    // "<prefix>      >>>> Interface Communication Trace from … <<<<"
    private static readonly Regex Header = new(@"^\S+[ ]+>>>> .* <<<<$", RegexOptions.CultureInvariant, RegexTimeout);

    // "<prefix>             02 00 00 00 88 13 00 00 "
    private static readonly Regex Continuation = new(@"^\S+[ ]+((?:[0-9A-Fa-f]{2}[ ]?)+)$", RegexOptions.CultureInvariant, RegexTimeout);

    // A call: an upper-case name, optionally " /" and a date (OPEN /Sun Nov 22 19:48:32 2026).
    private static readonly Regex Call = new(@"^([A-Z][A-Z0-9_]*)(?: /.*)?$", RegexOptions.CultureInvariant, RegexTimeout);

    // A field of the call: INPUT, OUTPUT, ERROR, I -> D, D -> I.
    private static readonly Regex Field = new(@"^[ ]*(INPUT|OUTPUT|ERROR|I -> D|D -> I):[ ]?(.*)$", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>Parses a trace segment. <paramref name="initialConcept"/> is the concept in force
    /// before the segment's first line (null when not known).</summary>
    public static IfhTraceSegment Parse(string text, ushort? initialConcept = null)
    {
        var events = new List<IfhEvent>();
        var problems = new List<IfhTraceProblem>();
        var traceLines = 0;
        var concept = initialConcept;

        Pending? call = null;
        string? field = null;  // the last field seen, for continuation lines

        void Close()
        {
            if (call is null)
            {
                return;
            }

            events.Add(new IfhEvent(call.Name, call.Input?.ToArray(), call.ConceptBefore, call.Error, call.Line));
            if (call.Name == Open)
            {
                concept = null;
            }
            else if (call.Name == SetEcuComm)
            {
                concept = call.Error == NoError && call.InputReadable && call.Input is { Count: >= 2 } input
                    ? (ushort)(input[0] | (input[1] << 8))
                    : null;
            }

            call = null;
            field = null;
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var line = lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i];
            if (line.Trim().Length == 0)
            {
                continue;
            }

            // Metadata lines ("\t\t…#@13;P00002600h;#", "#'Sun Nov 22 …;V7.3.0;#").
            var trimmed = line.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '#' && trimmed[^1] == '#' && (line[0] == '\t' || line[0] == '#'))
            {
                continue;
            }

            traceLines++;
            if (Header.IsMatch(line))
            {
                Close();
                continue;
            }

            var stamped = Stamped.Match(line);
            if (stamped.Success)
            {
                var content = stamped.Groups[2].Value;
                var f = Field.Match(content);
                if (f.Success)
                {
                    var name = f.Groups[1].Value;
                    var value = f.Groups[2].Value.Trim();
                    if (call is null)
                    {
                        problems.Add(new IfhTraceProblem(lineNo, Short(line), $"{name} without a call before it"));
                        field = null;
                        continue;
                    }

                    field = name;
                    switch (name)
                    {
                        case "INPUT":
                            if (call.Input is not null)
                            {
                                problems.Add(new IfhTraceProblem(lineNo, Short(line), $"a second INPUT for {call.Name}"));
                                call.InputReadable = false;
                            }

                            call.Input = [];
                            if (value != "--")
                            {
                                if (TelegramRule.TryParseHex(value, out var bytes) && bytes.Length > 0)
                                {
                                    call.Input.AddRange(bytes);
                                }
                                else
                                {
                                    problems.Add(new IfhTraceProblem(lineNo, Short(line), $"the INPUT of {call.Name} is not hex bytes"));
                                    call.InputReadable = false;
                                }
                            }

                            break;
                        case "ERROR":
                            call.Error = value;
                            break;
                        default:
                            // OUTPUT and the device traffic (I -> D, D -> I) are not judged: the
                            // INPUT is what the SGBD asked the interface to send.
                            break;
                    }

                    continue;
                }

                var c = Call.Match(content);
                if (c.Success)
                {
                    Close();
                    call = new Pending(c.Groups[1].Value, concept, lineNo);
                    continue;
                }

                problems.Add(new IfhTraceProblem(lineNo, Short(line), "not a call or a field the parser knows"));
                continue;
            }

            var cont = Continuation.Match(line);
            if (cont.Success && field is not null && call is not null)
            {
                if (field == "INPUT")
                {
                    if (call.Input is { Count: > 0 } && TelegramRule.TryParseHex(cont.Groups[1].Value, out var more))
                    {
                        call.Input.AddRange(more);
                    }
                    else
                    {
                        // More INPUT after "INPUT: --", or bytes that do not read.
                        problems.Add(new IfhTraceProblem(lineNo, Short(line), $"an INPUT continuation of {call.Name} that does not fit"));
                        call.InputReadable = false;
                    }
                }

                continue;
            }

            problems.Add(new IfhTraceProblem(lineNo, Short(line), cont.Success ? "a continuation line without a field before it" : "not a trace line"));
        }

        Close();
        return new IfhTraceSegment(events, problems, traceLines, concept);
    }

    private static string Short(string line)
    {
        var s = new string(line.Select(ch => char.IsControl(ch) ? ' ' : ch).Take(120).ToArray());
        return line.Length > 120 ? s + "..." : s;
    }

    private sealed class Pending(string name, ushort? conceptBefore, int line)
    {
        public string Name { get; } = name;
        public ushort? ConceptBefore { get; } = conceptBefore;
        public int Line { get; } = line;
        public List<byte>? Input { get; set; }
        public bool InputReadable { get; set; } = true;
        public string? Error { get; set; }
    }
}
