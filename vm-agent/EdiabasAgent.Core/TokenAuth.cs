using System.Security.Cryptography;
using System.Text;

namespace EdiabasAgent.Core;

/// <summary>Result of reading the token file. <see cref="Error"/> never contains token material.</summary>
public sealed record TokenFileResult(bool Ok, string? Token, string? Error);

/// <summary>
/// Bearer-token check. The configured token is held only as its SHA-256; presented tokens are
/// hashed and compared with <see cref="CryptographicOperations.FixedTimeEquals"/>, so neither the
/// content nor the length of the real token leaks through timing.
/// </summary>
public sealed class TokenAuth
{
    public const int MinTokenLength = 32;
    private const int MaxPresentedTokenLength = 4096;

    private readonly byte[]? _expectedHash;

    public TokenAuth(string? configuredToken)
    {
        // An empty or whitespace configured token never authenticates anyone.
        _expectedHash = string.IsNullOrWhiteSpace(configuredToken)
            ? null
            : SHA256.HashData(Encoding.UTF8.GetBytes(configuredToken));
    }

    /// <summary>
    /// Parses an Authorization header of the exact form <c>Bearer &lt;token&gt;</c>: scheme
    /// case-insensitive, one single space, one token of visible ASCII, nothing before or after.
    /// </summary>
    public static bool TryParseBearer(string? header, out string token)
    {
        token = "";
        const string scheme = "Bearer";
        if (header is null || header.Length < scheme.Length + 2 || header.Length > scheme.Length + 1 + MaxPresentedTokenLength)
        {
            return false;
        }

        if (!header.AsSpan(0, scheme.Length).Equals(scheme, StringComparison.OrdinalIgnoreCase) || header[scheme.Length] != ' ')
        {
            return false;
        }

        var candidate = header[(scheme.Length + 1)..];
        foreach (var c in candidate)
        {
            // Visible ASCII only: rejects spaces (second token, double space, trailing space),
            // tabs, CR/LF, NUL and any non-ASCII.
            if (c < 0x21 || c > 0x7E)
            {
                return false;
            }
        }

        token = candidate;
        return token.Length > 0;
    }

    /// <summary>True only for a well-formed Bearer header carrying the configured token.</summary>
    public bool IsAuthorized(string? authorizationHeader)
    {
        if (!TryParseBearer(authorizationHeader, out var presented))
        {
            // Still do the same hashing work so a malformed header costs about the same.
            _ = Matches("");
            return false;
        }

        return Matches(presented);
    }

    /// <summary>Constant-time comparison of a presented token with the configured one.</summary>
    public bool Matches(string? presented)
    {
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented ?? ""));
        if (_expectedHash is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(presentedHash, _expectedHash);
    }

    /// <summary>The one route that needs no token (docs/api.md, "`GET /hello`"): exactly <c>GET /hello</c>.</summary>
    public const string HelloPath = "/hello";

    private const string HelloContext = "ediabas-bridge-hello-v1:";

    /// <summary>
    /// Whether a request must carry the token. Only <c>GET /hello</c> is exempt: an exact path
    /// (case-insensitive, like routing), never a prefix, and only for GET. The source-address check,
    /// routing, the method check and the audit line apply to every request regardless.
    /// </summary>
    public static bool NeedsToken(string method, string? path) =>
        !(HttpMethodIs(method, "GET") && string.Equals(path, HelloPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The <c>/hello</c> proof (decision D5): lowercase hex
    /// HMAC-SHA256(key = SHA-256(UTF-8 token), message = UTF-8 "ediabas-bridge-hello-v1:" + nonce).
    /// The key is the stored hash, so the token itself is not needed; the proof shows the agent
    /// holds it without revealing anything usable about it. The caller validates the nonce.
    /// </summary>
    public string HelloProof(string nonce)
    {
        if (_expectedHash is null)
        {
            throw new InvalidOperationException("No token is configured.");
        }

        return Convert.ToHexStringLower(HMACSHA256.HashData(_expectedHash, Encoding.UTF8.GetBytes(HelloContext + nonce)));
    }

    private static bool HttpMethodIs(string method, string expected) =>
        string.Equals(method, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates the token file's content: trimmed (including a BOM), at least
    /// <see cref="MinTokenLength"/> characters, no inner whitespace or control characters.
    /// </summary>
    public static TokenFileResult ParseTokenFile(string? content)
    {
        if (content is null)
        {
            return new TokenFileResult(false, null, "token file is empty");
        }

        var token = content.Trim().Trim('\uFEFF').Trim();
        if (token.Length == 0)
        {
            return new TokenFileResult(false, null, "token file is empty");
        }

        if (token.Length < MinTokenLength)
        {
            return new TokenFileResult(false, null,
                $"token is too short ({token.Length} characters; at least {MinTokenLength} required)");
        }

        if (token.Any(c => c < 0x21 || c > 0x7E))
        {
            return new TokenFileResult(false, null,
                "token contains whitespace, control or non-ASCII characters; it must be one line of visible ASCII");
        }

        return new TokenFileResult(true, token, null);
    }
}
