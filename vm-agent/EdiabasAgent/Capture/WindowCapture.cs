using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using EdiabasAgent.Core;
using EdiabasAgent.Windows;

namespace EdiabasAgent.Capture;

/// <summary>An encoded screenshot and the facts the response headers carry.</summary>
internal sealed record CapturedImage(byte[] Bytes, string ContentType, string Title, string Handle, string Method, int Width, int Height);

/// <summary>
/// Screenshots (docs/api.md "GET /screen/capture"). A window: <c>PrintWindow</c> with
/// PW_RENDERFULLCONTENT into the agent's own bitmap, falling back to a <c>BitBlt</c> of the
/// window's rectangle from the screen. <c>desktop</c>: the primary screen. Never restores,
/// focuses, moves or resizes a window, never sends input, never touches the clipboard. Every
/// GDI object is freed on every path. Decisions are in <see cref="CaptureRules"/> (Core).
/// </summary>
internal static class WindowCapture
{
    public static Outcome<CapturedImage> Capture(CaptureRequest request, int sessionId)
    {
        if (!CaptureRules.DesktopAvailable(sessionId, sessionId == 0 ? null : InputDesktopName()))
        {
            return CaptureRules.DesktopUnavailable(sessionId == 0
                ? "the agent is not running in an interactive session (session 0)"
                : "the screen is locked or a secure desktop (sign-in, UAC prompt) is showing");
        }

        return request.Target.Kind == CaptureTargetKind.Desktop
            ? CaptureDesktop(request)
            : CaptureWindow(request);
    }

    private static Outcome<CapturedImage> CaptureDesktop(CaptureRequest request)
    {
        // Physical pixels of the primary screen: the process is per-monitor DPI aware (app.manifest).
        var width = CaptureNative.GetSystemMetrics(CaptureNative.SM_CXSCREEN);
        var height = CaptureNative.GetSystemMetrics(CaptureNative.SM_CYSCREEN);
        if (!CaptureRules.UsableSourceSize(width, height))
        {
            return CaptureRules.Failed("the screen size could not be read");
        }

        var pixels = Grab(width, height, (mem, screen) =>
            CaptureNative.BitBlt(mem, 0, 0, width, height, screen, 0, 0, CaptureNative.SRCCOPY | CaptureNative.CAPTUREBLT));
        if (pixels is null)
        {
            return CaptureRules.Failed("the screen could not be read");
        }

        return Encode(pixels, width, height, new PixelRect(0, 0, width, height), request,
            CaptureRules.DesktopName, CaptureRules.DesktopName, CaptureRules.MethodScreen);
    }

    private static Outcome<CapturedImage> CaptureWindow(CaptureRequest request)
    {
        var found = CaptureRules.FindWindow(WindowEnumerator.List(), request.Target);
        if (!found.Ok)
        {
            return found.Failure!;
        }

        var window = found.Value!;
        if (CaptureRules.ParseHandle(window.Handle) is not { } raw)
        {
            return NotFound();
        }

        var hwnd = new IntPtr(unchecked((int)raw));
        if (!CaptureNative.IsWindow(hwnd))
        {
            return NotFound();
        }

        if (CaptureNative.IsIconic(hwnd))
        {
            return Minimized(window);
        }

        if (!CaptureNative.GetWindowRect(hwnd, out var r))
        {
            return NotFound();
        }

        var rect = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
        if (!CaptureRules.UsableSourceSize(rect.Width, rect.Height))
        {
            return ApiFailure.Of(ApiErrors.WindowNotFound, "The window has no capturable area.");
        }

        PixelRect? frame = CaptureNative.DwmGetWindowAttribute(hwnd, CaptureNative.DWMWA_EXTENDED_FRAME_BOUNDS, out var f, Marshal.SizeOf<CaptureNative.RECT>()) == 0
            ? new PixelRect(f.Left, f.Top, f.Right, f.Bottom)
            : null;

        // PrintWindow waits for the window to draw itself; a hung window would hold the request
        // forever, so it goes straight to the screen copy.
        byte[]? pixels = null;
        if (!CaptureNative.IsHungAppWindow(hwnd))
        {
            pixels = Grab(rect.Width, rect.Height, (mem, _) => CaptureNative.PrintWindow(hwnd, mem, CaptureNative.PW_RENDERFULLCONTENT));
            if (pixels is not null && CaptureRules.IsAllBlack(pixels))
            {
                pixels = null;
            }
        }

        Outcome<CapturedImage> result;
        if (pixels is not null)
        {
            result = Encode(pixels, rect.Width, rect.Height, CaptureRules.VisiblePart(rect, frame), request,
                window.Title, window.Handle, CaptureRules.MethodPrintWindow);
        }
        else
        {
            // What is on screen inside the window's visible frame, overlapping windows included.
            var visible = frame is { } v && CaptureRules.UsableSourceSize(v.Width, v.Height) ? v : rect;
            var shot = Grab(visible.Width, visible.Height, (mem, screen) =>
                CaptureNative.BitBlt(mem, 0, 0, visible.Width, visible.Height, screen, visible.Left, visible.Top,
                    CaptureNative.SRCCOPY | CaptureNative.CAPTUREBLT));
            if (shot is null)
            {
                return CaptureRules.Failed("the window could not be read from the screen");
            }

            result = Encode(shot, visible.Width, visible.Height, new PixelRect(0, 0, visible.Width, visible.Height), request,
                window.Title, window.Handle, CaptureRules.MethodBitBlt);
        }

        // Minimised while it was being captured: the image would be of nothing useful.
        return CaptureNative.IsIconic(hwnd) ? Minimized(window) : result;
    }

    private static ApiFailure NotFound() => ApiFailure.Of(ApiErrors.WindowNotFound, "The window is gone.");

    private static ApiFailure Minimized(WindowInfo window) =>
        ApiFailure.Of(ApiErrors.WindowMinimized,
            "The window is minimised. The bridge never restores windows; restore it in the VM and try again.",
            new WindowDetails(window.Handle, window.Title));

    /// <summary>
    /// Draws into a 32-bit top-down DIB of the agent's own and copies the pixels out (BGRX).
    /// Null when any GDI step fails. Frees the DCs and the bitmap on every path.
    /// </summary>
    private static byte[]? Grab(int width, int height, Func<IntPtr, IntPtr, bool> draw)
    {
        var screen = CaptureNative.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return null;
        }

        var mem = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            mem = CaptureNative.CreateCompatibleDC(screen);
            if (mem == IntPtr.Zero)
            {
                return null;
            }

            var header = new CaptureNative.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<CaptureNative.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = CaptureNative.BI_RGB,
            };
            dib = CaptureNative.CreateDIBSection(screen, ref header, CaptureNative.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            previous = CaptureNative.SelectObject(mem, dib);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
            {
                previous = IntPtr.Zero;
                return null;
            }

            if (!draw(mem, screen))
            {
                return null;
            }

            CaptureNative.GdiFlush();
            var pixels = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                CaptureNative.SelectObject(mem, previous);
            }

            if (dib != IntPtr.Zero)
            {
                CaptureNative.DeleteObject(dib);
            }

            if (mem != IntPtr.Zero)
            {
                CaptureNative.DeleteDC(mem);
            }

            CaptureNative.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Scales <paramref name="part"/> of the BGRX pixels down to fit and encodes it.</summary>
    private static CapturedImage Encode(byte[] pixels, int width, int height, PixelRect part, CaptureRequest request,
        string title, string handle, string method)
    {
        var (outW, outH) = CaptureRules.Fit(part.Width, part.Height, request.MaxEdge);
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var source = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, pin.AddrOfPinnedObject());
            using var target = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(target))
            using (var attributes = new ImageAttributes())
            {
                var scaled = outW != part.Width || outH != part.Height;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = scaled ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.None;

                // Mirrored edges, so the filter does not darken the border pixels.
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(source, new Rectangle(0, 0, outW, outH), part.Left, part.Top, part.Width, part.Height, GraphicsUnit.Pixel, attributes);
            }

            using var ms = new MemoryStream();
            string contentType;
            if (request.Format == CaptureFormat.Png)
            {
                target.Save(ms, ImageFormat.Png);
                contentType = "image/png";
            }
            else
            {
                var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)CaptureRules.JpegQuality);
                target.Save(ms, jpeg, parameters);
                contentType = "image/jpeg";
            }

            return new CapturedImage(ms.ToArray(), contentType, title, handle, method, outW, outH);
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>Name of the desktop that receives input (<c>Default</c> when unlocked), else null.</summary>
    private static string? InputDesktopName()
    {
        var desktop = CaptureNative.OpenInputDesktop(0, false, CaptureNative.DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[256];
            return CaptureNative.GetUserObjectInformationW(desktop, CaptureNative.UOI_NAME, buffer, (uint)(buffer.Length * sizeof(char)), out _)
                ? new string(buffer).TrimEnd('\0')
                : null;
        }
        finally
        {
            CaptureNative.CloseDesktop(desktop);
        }
    }
}
