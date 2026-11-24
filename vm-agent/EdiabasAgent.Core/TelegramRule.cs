using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EdiabasAgent.Core;

/// <summary>The verdict for one telegram: allowed, or refused with one of the
/// <see cref="TelegramRule"/> reasons.</summary>
public sealed record TelegramVerdict(bool Allowed, string? Reason)
{
    public static readonly TelegramVerdict Allow = new(true, null);

    public static TelegramVerdict Refuse(string reason) => new(false, reason);
}

/// <summary>
/// The telegram rule from <c>policy/telegrams.json</c>, evaluated exactly as docs/api.md
/// "Telegram rule" says. Default-deny: only read services by service ID, the default session,
/// the adapter's own queries and reviewed exact telegrams pass. A file that fails to load or
/// validate refuses every telegram (reason <see cref="ReasonUnavailable"/>).
/// </summary>
public sealed class TelegramRule
{
    public const string ResourceName = "EdiabasAgent.Core.telegrams.json";

    /// <summary>The only <c>version</c> this build implements. A file of another version may
    /// mean something this build does not know, so it does not load.</summary>
    public const int SupportedVersion = 1;

    public const string ReasonUnavailable = "rule_unavailable";
    public const string ReasonNoConcept = "no_concept";
    public const string ReasonUnknownConcept = "unknown_concept";
    public const string ReasonDs2 = "ds2";
    public const string ReasonMalformed = "malformed";
    public const string ReasonAdapter = "adapter";
    public const string ReasonService = "service";

    // Concepts (protocols) as set with SETECUCOMM / ifhSetParameter.
    public const ushort ConceptDs2 = 0x0006;
    public const ushort ConceptKwp2000Bmw = 0x010C;
    public const ushort ConceptKwp2000Star = 0x010D;
    public const ushort ConceptBmwFast = 0x010F;
    public const ushort ConceptDcan = 0x0110;

    private const byte Tester = 0xF1;
    private const byte SessionService = 0x10;

    private static readonly Lazy<TelegramRule> EmbeddedRule = new(LoadEmbedded);

    // Every key the agent understands; anything else makes the file invalid.
    private static readonly string[] TopKeys = ["version", "readServices", "defaultSession", "adapterReads", "exact"];
    private static readonly string[] ExactKeys = ["address", "data", "note"];

    private readonly HashSet<byte> _readServices = [];
    private readonly HashSet<byte> _adapterReads = [];
    private readonly List<(byte Address, byte[] Data)> _exact = [];
    private byte _defaultSession;

    private TelegramRule(string? error, int? version)
    {
        LoadError = error;
        Version = version;
    }

    /// <summary>The rule built into this assembly (linked from policy/telegrams.json).</summary>
    public static TelegramRule Embedded => EmbeddedRule.Value;

    public bool Loaded => LoadError is null;

    /// <summary>Why the rule is unusable; null when it loaded.</summary>
    public string? LoadError { get; }

    /// <summary>The file's <c>version</c>; null when it did not load.</summary>
    public int? Version { get; }

    /// <summary>Lower-case hex SHA-256 of the exact bytes of the embedded file; null for a rule
    /// parsed from text, or when it did not load.</summary>
    public string? Sha256 { get; private set; }

    /// <summary>Parses and validates telegrams JSON. Never throws.</summary>
    public static TelegramRule Parse(string json)
    {
        if (HasLoneSurrogate(json))
        {
            return new TelegramRule("telegrams.json: it has text that is not valid Unicode (a lone surrogate)", null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            return Build(doc.RootElement);
        }
        catch (JsonException ex)
        {
            return new TelegramRule($"telegrams.json is not valid JSON: {ex.Message}", null);
        }
        catch (RuleFormatException ex)
        {
            return new TelegramRule($"telegrams.json: {ex.Message}", null);
        }
        catch (InvalidOperationException ex)
        {
            return new TelegramRule($"telegrams.json: it has text that cannot be read: {ex.Message}", null);
        }
    }

    private static TelegramRule LoadEmbedded()
    {
        try
        {
            using var stream = typeof(TelegramRule).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return new TelegramRule("telegrams.json is not embedded in the agent", null);
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            var rule = Parse(new UTF8Encoding(false, true).GetString(bytes));
            if (rule.Loaded)
            {
                rule.Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            }

            return rule;
        }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException)
        {
            return new TelegramRule($"telegrams.json cannot be read: {ex.GetType().Name}: {ex.Message}", null);
        }
    }

    private static TelegramRule Build(JsonElement root)
    {
        var top = Obj(root, "the file", TopKeys);

        var versionEl = top["version"];
        if (versionEl.ValueKind != JsonValueKind.Number || !versionEl.TryGetInt32(out var version) || version < 1)
        {
            throw new RuleFormatException("version must be a positive integer");
        }

        if (version != SupportedVersion)
        {
            throw new RuleFormatException($"version {version} is not supported; this agent implements version {SupportedVersion}");
        }

        var rule = new TelegramRule(null, version);
        foreach (var b in ByteArray(top["readServices"], "readServices"))
        {
            if (!rule._readServices.Add(b))
            {
                throw new RuleFormatException($"readServices lists {b:X2} twice");
            }
        }

        rule._defaultSession = Byte(top["defaultSession"], "defaultSession");

        foreach (var b in ByteArray(top["adapterReads"], "adapterReads"))
        {
            if (!rule._adapterReads.Add(b))
            {
                throw new RuleFormatException($"adapterReads lists {b:X2} twice");
            }
        }

        var exactEl = top["exact"];
        if (exactEl.ValueKind != JsonValueKind.Array)
        {
            throw new RuleFormatException("exact must be an array");
        }

        var i = 0;
        foreach (var entryEl in exactEl.EnumerateArray())
        {
            var where = $"exact[{i++}]";
            var entry = Obj(entryEl, where, ExactKeys);
            var address = Byte(entry["address"], where + ".address");
            var data = Bytes(entry["data"], where + ".data");
            if (data.Length == 0)
            {
                throw new RuleFormatException($"{where}.data must hold at least one byte");
            }

            if (entry["note"].ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry["note"].GetString()))
            {
                throw new RuleFormatException($"{where}.note must say what the telegram is and who reviewed it");
            }

            if (rule._exact.Any(e => e.Address == address && e.Data.AsSpan().SequenceEqual(data)))
            {
                throw new RuleFormatException($"{where} is listed twice");
            }

            rule._exact.Add((address, data));
        }

        return rule;
    }

    /// <summary>
    /// Judges one telegram as sent, under the concept last set for the interface (null when none
    /// was set). docs/api.md "Telegram rule", steps 1 to 6.
    /// </summary>
    public TelegramVerdict Evaluate(ushort? concept, ReadOnlySpan<byte> frame)
    {
        if (!Loaded)
        {
            return TelegramVerdict.Refuse(ReasonUnavailable);
        }

        // 1. Concept.
        bool fastFraming;
        switch (concept)
        {
            case null:
                return TelegramVerdict.Refuse(ReasonNoConcept);
            case ConceptBmwFast or ConceptKwp2000Bmw or ConceptDcan:
                fastFraming = true;
                break;
            case ConceptKwp2000Star:
                fastFraming = false;
                break;
            case ConceptDs2:
                return TelegramVerdict.Refuse(ReasonDs2);
            default:
                return TelegramVerdict.Refuse(ReasonUnknownConcept);
        }

        // 2. Frame.
        if (!TryData(frame, fastFraming, out var data))
        {
            return TelegramVerdict.Refuse(ReasonMalformed);
        }

        var target = frame[1];
        var service = data[0];

        // 3. Adapter: the cable itself.
        if (target == Tester)
        {
            return _adapterReads.Contains(service) ? TelegramVerdict.Allow : TelegramVerdict.Refuse(ReasonAdapter);
        }

        // 4. Read services, and the default session exactly.
        if (_readServices.Contains(service))
        {
            return TelegramVerdict.Allow;
        }

        if (data.Length == 2 && service == SessionService && data[1] == _defaultSession)
        {
            return TelegramVerdict.Allow;
        }

        // 5. Exact, reviewed telegrams (address and data; the checksum is not part of the data).
        foreach (var (address, bytes) in _exact)
        {
            if (address == target && data.SequenceEqual(bytes))
            {
                return TelegramVerdict.Allow;
            }
        }

        // 6.
        return TelegramVerdict.Refuse(ReasonService);
    }

    /// <summary>The data bytes of a well-formed frame: byte 0 addressing <c>10</c> or <c>11</c>,
    /// source <c>F1</c>, a non-zero length, and the frame ending with the data or one checksum
    /// byte after it.</summary>
    private static bool TryData(ReadOnlySpan<byte> frame, bool fastFraming, out ReadOnlySpan<byte> data)
    {
        data = default;
        if (frame.Length < 3 || (frame[0] & 0x80) == 0 || frame[2] != Tester)
        {
            return false;
        }

        int length;
        int start;
        if (!fastFraming)
        {
            // KWP2000*: the length is always byte 3.
            if (frame.Length < 4)
            {
                return false;
            }

            length = frame[3];
            start = 4;
        }
        else if ((frame[0] & 0x3F) != 0)
        {
            length = frame[0] & 0x3F;
            start = 3;
        }
        else if (frame.Length < 4)
        {
            return false;
        }
        else if (frame[3] != 0)
        {
            length = frame[3];
            start = 4;
        }
        else
        {
            if (frame.Length < 6)
            {
                return false;
            }

            length = (frame[4] << 8) | frame[5];
            start = 6;
        }

        var end = start + length;
        if (length == 0 || (frame.Length != end && frame.Length != end + 1))
        {
            return false;
        }

        data = frame.Slice(start, length);
        return true;
    }

    /// <summary>Space-separated hex bytes (<c>"82 12 F1"</c>, any case) to bytes; false for
    /// anything else. Used for the shared cases and for trace text.</summary>
    public static bool TryParseHex(string? text, out byte[] bytes)
    {
        bytes = [];
        if (text is null)
        {
            return false;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length != 2 || !byte.TryParse(parts[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out list[i]))
            {
                return false;
            }
        }

        bytes = list;
        return true;
    }

    /// <summary>Bytes as upper-case hex separated by spaces, as the trace and the rule file write them.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes) => string.Join(' ', bytes.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>A concept as four upper-case hex digits (<c>010F</c>), or null.</summary>
    public static string? ConceptHex(ushort? concept) => concept?.ToString("X4", CultureInfo.InvariantCulture);

    private static Dictionary<string, JsonElement> Obj(JsonElement el, string where, string[] keys)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            throw new RuleFormatException($"{where} must be an object");
        }

        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in el.EnumerateObject())
        {
            if (Array.IndexOf(keys, p.Name) < 0)
            {
                throw new RuleFormatException($"{where} has an unknown key '{p.Name}'; this agent does not understand it");
            }

            if (!map.TryAdd(p.Name, p.Value))
            {
                throw new RuleFormatException($"{where} has a duplicate key '{p.Name}'");
            }
        }

        foreach (var k in keys)
        {
            if (!map.ContainsKey(k))
            {
                throw new RuleFormatException($"{where} is missing '{k}'");
            }
        }

        return map;
    }

    /// <summary>One byte written as exactly two upper-case hex digits, as the file writes them.</summary>
    private static byte Byte(JsonElement el, string where)
    {
        var s = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        if (s is null || !IsUpperHexByte(s))
        {
            throw new RuleFormatException($"{where} must be one byte as two upper-case hex digits, such as \"1A\"");
        }

        return byte.Parse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    private static List<byte> ByteArray(JsonElement el, string where)
    {
        if (el.ValueKind != JsonValueKind.Array)
        {
            throw new RuleFormatException($"{where} must be an array of bytes");
        }

        return el.EnumerateArray().Select(x => Byte(x, where + " entry")).ToList();
    }

    /// <summary>Bytes as two upper-case hex digits each, separated by single spaces.</summary>
    private static byte[] Bytes(JsonElement el, string where)
    {
        var s = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        var parts = s?.Split(' ');
        if (s is null || parts!.Any(p => !IsUpperHexByte(p)))
        {
            throw new RuleFormatException($"{where} must be bytes as two upper-case hex digits separated by single spaces, such as \"30 91 01 01\"");
        }

        return parts!.Select(p => byte.Parse(p, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)).ToArray();
    }

    private static bool IsUpperHexByte(string s) =>
        s.Length == 2 && s.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'F'));

    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(s[i]))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class RuleFormatException(string message) : Exception(message);
}
