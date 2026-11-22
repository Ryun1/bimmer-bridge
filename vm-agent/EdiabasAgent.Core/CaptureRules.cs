using System.Globalization;
using System.Text;

namespace EdiabasAgent.Core;

public enum CaptureFormat
{
    Jpeg,
    Png,
}

public enum CaptureTargetKind
{
    Desktop,
    Handle,
    Title,
}

/// <summary>What <c>window</c> asked for: the primary screen, one handle, or a title substring.</summary>
public sealed record CaptureTarget(CaptureTargetKind Kind, uint Handle, string? Title)
{
    public static readonly CaptureTarget Desktop = new(CaptureTargetKind.Desktop, 0, null);
}

public sealed record CaptureRequest(CaptureTarget Target, int MaxEdge, CaptureFormat Format);

/// <summary>A rectangle in screen pixels: left, top, right, bottom (right and bottom exclusive).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>Error details for <c>window_minimized</c>.</summary>
public sealed record WindowDetails(string Handle, string Title);

/// <summary>
/// The decisions behind <c>GET /screen/capture</c> that need no Win32 call: parameters,
/// which window, the output size, headers. docs/api.md "GET /screen/capture".
/// </summary>
public static class CaptureRules
{
    public const int MinMaxEdge = 200;
    public const int MaxMaxEdge = 4000;
    public const int DefaultMaxEdge = 1600;
    public const int MaxWindowParameterLength = 256;
    public const int JpegQuality = 85;

    /// <summary>No source image may be larger than this on either side (memory bound; the agent is 32-bit).</summary>
    public const int MaxSourceEdge = 8192;

    /// <summary>Nor have more pixels than an 8K screen (about 127 MB as 32-bit pixels).</summary>
    public const long MaxSourcePixels = 7680L * 4320;

    public const string DesktopName = "desktop";

    public const string MethodPrintWindow = "printwindow";
    public const string MethodBitBlt = "bitblt";
    public const string MethodScreen = "screen";

    public static Outcome<CaptureRequest> Parse(QueryValues q)
    {
        var target = CaptureTarget.Desktop;
        if (q.Get("window") is { } w)
        {
            if (w.Length is < 1 or > MaxWindowParameterLength || w.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
            {
                return RequestParsing.Bad("window", $"window must be a handle (0x...), part of a window title, or 'desktop', 1 to {MaxWindowParameterLength} characters.");
            }

            if (string.Equals(w, DesktopName, StringComparison.OrdinalIgnoreCase))
            {
                target = CaptureTarget.Desktop;
            }
            else if (w.StartsWith("0x", StringComparison.Ordinal) && w.Length > 2 && w[2..].All(char.IsAsciiHexDigit))
            {
                // HWNDs are 32-bit significant; a longer number is no handle this agent can name.
                if (w.Length > 10 || !uint.TryParse(w[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var h))
                {
                    return RequestParsing.Bad("window", "A window handle is 0x and 1 to 8 hex digits.");
                }

                target = new CaptureTarget(CaptureTargetKind.Handle, h, null);
            }
            else
            {
                target = new CaptureTarget(CaptureTargetKind.Title, 0, w);
            }
        }

        var maxEdge = DefaultMaxEdge;
        if (q.Get("maxEdge") is { } m)
        {
            if (LogText.ParseWhole(m, MinMaxEdge, MaxMaxEdge) is not { } n)
            {
                return RequestParsing.Bad("maxEdge", $"maxEdge must be a whole number from {MinMaxEdge} to {MaxMaxEdge}.");
            }

            maxEdge = (int)n;
        }

        var format = CaptureFormat.Jpeg;
        switch (q.Get("format"))
        {
            case null or "jpeg":
                break;
            case "png":
                format = CaptureFormat.Png;
                break;
            default:
                return RequestParsing.Bad("format", "format must be jpeg or png.");
        }

        return new CaptureRequest(target, maxEdge, format);
    }

    /// <summary>
    /// The window to capture, from the visible windows in z-order (front first, as
    /// <c>GET /windows</c> lists them). A handle must be one of them; a title substring matches
    /// case-insensitively and the front-most match wins. A minimised match is refused: the
    /// agent never restores a window.
    /// </summary>
    public static Outcome<WindowInfo> FindWindow(IReadOnlyList<WindowInfo> windows, CaptureTarget target)
    {
        WindowInfo? match = target.Kind switch
        {
            CaptureTargetKind.Handle => windows.FirstOrDefault(x => string.Equals(x.Handle, ApiFormat.Handle(target.Handle), StringComparison.OrdinalIgnoreCase)),
            CaptureTargetKind.Title => windows.FirstOrDefault(x => x.Title.Contains(target.Title!, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };

        if (match is null)
        {
            return ApiFailure.Of(ApiErrors.WindowNotFound, target.Kind == CaptureTargetKind.Handle
                ? "No visible window has that handle."
                : "No visible window title contains that text.");
        }

        if (match.Minimized)
        {
            return ApiFailure.Of(ApiErrors.WindowMinimized,
                "The window is minimised. The bridge never restores windows; restore it in the VM and try again.",
                new WindowDetails(match.Handle, match.Title));
        }

        return match;
    }

    /// <summary>Parses a handle string as <see cref="ApiFormat.Handle"/> writes it.</summary>
    public static uint? ParseHandle(string handle) =>
        handle.StartsWith("0x", StringComparison.Ordinal) && handle.Length is > 2 and <= 10
        && uint.TryParse(handle[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var h)
            ? h
            : null;

    /// <summary>
    /// Output size: the longer edge at most <paramref name="maxEdge"/>, aspect kept, never
    /// scaled up, never below 1 pixel.
    /// </summary>
    public static (int Width, int Height) Fit(int width, int height, int maxEdge)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "image has no area");
        }

        var longer = Math.Max(width, height);
        if (longer <= maxEdge)
        {
            return (width, height);
        }

        var scale = (double)maxEdge / longer;
        var w = width >= height ? maxEdge : Math.Clamp((int)Math.Round(width * scale, MidpointRounding.AwayFromZero), 1, maxEdge);
        var h = height > width ? maxEdge : Math.Clamp((int)Math.Round(height * scale, MidpointRounding.AwayFromZero), 1, maxEdge);
        return (w, h);
    }

    /// <summary>A source size the agent will capture: some area, and not absurdly large.</summary>
    public static bool UsableSourceSize(int width, int height) =>
        width > 0 && height > 0 && width <= MaxSourceEdge && height <= MaxSourceEdge && (long)width * height <= MaxSourcePixels;

    /// <summary>
    /// The part of a PrintWindow image (which covers <paramref name="windowRect"/>) that is the
    /// visible frame (<paramref name="frame"/>, DWM's extended frame bounds), relative to the
    /// image. Leaves out the invisible resize borders. The whole image when the frame is
    /// unknown or does not lie within the window.
    /// </summary>
    public static PixelRect VisiblePart(PixelRect windowRect, PixelRect? frame)
    {
        var whole = new PixelRect(0, 0, windowRect.Width, windowRect.Height);
        if (frame is not { } f || f.Width <= 0 || f.Height <= 0
            || f.Left < windowRect.Left || f.Top < windowRect.Top || f.Right > windowRect.Right || f.Bottom > windowRect.Bottom)
        {
            return whole;
        }

        return new PixelRect(f.Left - windowRect.Left, f.Top - windowRect.Top, f.Right - windowRect.Left, f.Bottom - windowRect.Top);
    }

    /// <summary>
    /// Whether the input desktop can be captured: an interactive session (not 0) whose input
    /// desktop is <c>Default</c>. Anything else (locked, UAC prompt, sign-in screen, or the
    /// desktop could not be opened, <paramref name="inputDesktopName"/> null) is not.
    /// </summary>
    public static bool DesktopAvailable(int sessionId, string? inputDesktopName) =>
        sessionId != 0 && string.Equals(inputDesktopName, "Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>Only for a locked screen, the secure desktop or session 0: the answer tells the user to unlock the VM.</summary>
    public static ApiFailure DesktopUnavailable(string why) =>
        ApiFailure.Of(ApiErrors.DesktopUnavailable,
            $"The VM's desktop cannot be captured: {why}. Unlock the VM (sign in) and try again.");

    /// <summary>The one-at-a-time capture queue did not free up in time. Nothing was captured.</summary>
    public static ApiFailure Busy() =>
        ApiFailure.Of(ApiErrors.CaptureBusy,
            "The screenshot was not taken: another capture is still running. Try again in a moment.");

    /// <summary>The desktop was available but the GDI copy or the screen size read failed.</summary>
    public static ApiFailure Failed(string why) =>
        ApiFailure.Of(ApiErrors.CaptureFailed, $"The capture failed: {why}.");

    /// <summary>
    /// True when every pixel of a 32-bit BGRX image is black, which is how PrintWindow fails
    /// for some windows while still reporting success.
    /// </summary>
    public static bool IsAllBlack(ReadOnlySpan<byte> bgrx)
    {
        for (var i = 0; i + 3 < bgrx.Length; i += 4)
        {
            if (bgrx[i] != 0 || bgrx[i + 1] != 0 || bgrx[i + 2] != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>X-Window-Title</c>: the title as UTF-8, every byte outside A-Z a-z 0-9 - . _ ~
    /// percent-encoded, so the header is plain ASCII and <c>decodeURIComponent</c> restores it.
    /// A broken surrogate becomes U+FFFD.
    /// </summary>
    public static string PercentEncode(string value)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetBytes(value);
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
                or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }
}
