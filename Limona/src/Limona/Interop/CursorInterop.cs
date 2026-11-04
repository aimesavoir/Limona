using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace Limona.Interop;

/// <summary>
/// Custom-cursor plumbing for WinUI 3:
/// - builds a native HCURSOR (via GDI CreateIconIndirect) from an ARGB pixel buffer,
/// - wraps it into a Microsoft.UI.Input.InputCursor using the Windows App SDK
///   IInputCursorStaticsInterop::CreateFromHCursor factory,
/// - assigns it to any UIElement through the protected cursor property.
/// </summary>
internal static class CursorInterop
{
    /// <summary>Sets (or resets to default with null) the cursor drawn over an element.</summary>
    public static void SetCursor(UIElement element, InputCursor? cursor)
    {
        try
        {
            typeof(UIElement).InvokeMember(
                "ProtectedCursor",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty,
                null, element,
                cursor is null ? [null] : [cursor]);
        }
        catch { /* cursor customization is cosmetic */ }
    }

    /// <summary>Returns an InputCursor sized to a circle ring (the eraser visual).</summary>
    public static InputCursor? CreateCircleCursor(int diameter)
    {
        diameter = Math.Clamp(diameter, 10, 124);
        var argb = RenderCircleArgb(diameter);
        var hCursor = BuildHCursor(argb, diameter, diameter, diameter / 2, diameter / 2);
        var cursor = FromHCursor(hCursor);
        if (cursor is null) Dbg.Log($"CursorInterop: circle cursor d={diameter} FAILED (hCursor=0x{hCursor:X})");
        if (hCursor != 0) DestroyIcon(hCursor);
        return cursor;
    }

    /// <summary>
    /// Draws a precision target reticle for the image color picker: a thin
    /// white ring with dark halos, four tick marks and a tiny center hole
    /// (the exact pixel sits in the middle). Hotspot = center.
    /// </summary>
    public static InputCursor? CreateTargetCursor()
    {
        var diameter = 31;
        var argb = RenderTargetArgb(diameter);
        var hCursor = BuildHCursor(argb, diameter, diameter, diameter / 2, diameter / 2);
        var cursor = FromHCursor(hCursor);
        if (cursor is null) Dbg.Log($"CursorInterop: target cursor FAILED (hCursor=0x{hCursor:X})");
        if (hCursor != 0) DestroyIcon(hCursor);
        return cursor;
    }

    /// <summary>Wraps a native HCURSOR into an InputCursor (WinAppSDK internal factory).</summary>
    public static InputCursor? FromHCursor(nint hCursor)
    {
        if (hCursor == 0)
        {
            Dbg.Log("CursorInterop.FromHCursor: hCursor == 0");
            return null;
        }

        var iidInterop = new Guid("ac6f5065-90c4-46ce-beb7-05e138e54117");
        var iidActivation = new Guid("00000035-0000-0000-c000-000000000046");
        const string className = "Microsoft.UI.Input.InputCursor";

        var hr = WindowsCreateString(className, className.Length, out var hs);
        if (hr != 0)
        {
            Dbg.Log($"CursorInterop: WindowsCreateString hr=0x{hr:X8}");
            return null;
        }
        try
        {
            hr = RoGetActivationFactory(hs, ref iidActivation, out var factory);
            if (hr != 0)
            {
                Dbg.Log($"CursorInterop: RoGetActivationFactory hr=0x{hr:X8}");
                return null;
            }
            try
            {
                hr = Marshal.QueryInterface(factory, in iidInterop, out var interop);
                if (hr != 0 || interop == 0)
                {
                    Dbg.Log($"CursorInterop: QueryInterface(IInputCursorStatics) hr=0x{hr:X8}");
                    return null;
                }
                try
                {
                    // IUnknown (3 slots) + IInspectable (3 slots) -> first custom method is slot 6.
                    var vtable = Marshal.ReadIntPtr(interop);
                    var fn = Marshal.ReadIntPtr(vtable, 6 * Marshal.SizeOf<nint>());
                    if (fn == 0)
                    {
                        Dbg.Log("CursorInterop: vtable slot 6 is empty");
                        return null;
                    }
                    var invoke = (NativeCreateFromHCursor)Marshal.GetDelegateForFunctionPointer(
                        fn, typeof(NativeCreateFromHCursor));
                    hr = invoke(interop, hCursor, out var abi);
                    if (hr < 0 || abi == 0)
                    {
                        Dbg.Log($"CursorInterop: CreateFromHCursor hr=0x{hr:X8} abi=0x{abi:X}");
                        return null;
                    }
                    return WinRT.MarshalInspectable<InputCursor>.FromAbi(abi);
                }
                finally { Marshal.Release(interop); }
            }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(hs); }
    }

    /// <summary>
    /// Draws an elegant precision cursor: one thin anti-aliased white ring with
    /// two faint dark halos (visible on light and dark images) plus a small
    /// center dot. All pixels are premultiplied alpha.
    /// </summary>
    private static byte[] RenderCircleArgb(int diameter)
    {
        var pixels = new byte[diameter * diameter * 4];
        var center = (diameter - 1) / 2.0;
        var sigma = Math.Clamp(diameter * 0.014, 0.7, 1.6); // ring crispness
        double Bell(double x) => Math.Exp(-0.5 * x * x / (sigma * sigma));

        var ringR = diameter / 2.0 - 2.0;
        var haloGap = Math.Max(1.6, sigma * 2.6);
        var dotR = Math.Max(1.0, diameter * 0.018);
        var dotHalo = dotR + haloGap;

        for (var y = 0; y < diameter; y++)
        {
            for (var x = 0; x < diameter; x++)
            {
                var dist = Math.Sqrt((x - center) * (x - center) + (y - center) * (y - center));

                var white = Bell(dist - ringR);                            // main ring
                var dark = Math.Min(0.85, Bell(Math.Abs(dist - ringR) - haloGap)); // inner/outer halos
                var dw = Bell(Math.Max(0, dist - dotR));                   // center dot
                var db = Bell(Math.Max(0, dist - dotHalo)) * 0.7;          // dot halo

                white = Math.Clamp(white + dw * 0.9, 0, 1);
                dark = Math.Clamp(Math.Max(dark, db), 0, 1);
                var coverage = Math.Clamp(Math.Max(white, dark * 0.9), 0, 1);
                if (coverage <= 0.01) continue;

                var idx = (y * diameter + x) * 4;
                var a = (byte)Math.Round(coverage * 255);
                var useWhite = white >= dark;
                var v = useWhite ? a : (byte)0; // premultiplied: white = alpha, black = 0
                pixels[idx] = v;
                pixels[idx + 1] = v;
                pixels[idx + 2] = v;
                pixels[idx + 3] = a;
            }
        }
        return pixels;
    }

    /// <summary>
    /// Draws the color-picker target: white ring + four tick marks with dark
    /// halos, and a center hole so the exact pixel under the hotspot shows.
    /// Premultiplied alpha, like RenderCircleArgb.
    /// </summary>
    private static byte[] RenderTargetArgb(int diameter)
    {
        var pixels = new byte[diameter * diameter * 4];
        var center = (diameter - 1) / 2.0;
        var sigma = 0.9;
        double Bell(double x) => Math.Exp(-0.5 * x * x / (sigma * sigma));

        var ringR = 7.0;
        var tickInner = 9.0;
        var tickOuter = diameter / 2.0 - 2.0;
        var holeR = 1.4;
        var haloGap = 1.8;

        for (var y = 0; y < diameter; y++)
        {
            for (var x = 0; x < diameter; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var dist = Math.Sqrt(dx * dx + dy * dy);

                var white = Bell(dist - ringR);                                   // ring
                var dark = Math.Min(0.85, Bell(Math.Abs(dist - ringR) - haloGap)); // ring halos

                // four tick marks on the axes (crisp, with faint dark halo)
                var onAxis = Math.Min(Math.Abs(dx), Math.Abs(dy));
                if (onAxis <= 0.9 && dist >= tickInner && dist <= tickOuter)
                    white = 1.0;
                else if (onAxis <= 2.2 && dist >= tickInner - 1.2 && dist <= tickOuter + 1.2)
                    dark = Math.Max(dark, 0.55);

                // center hole: erase the exact hot pixel area
                var hole = 1.0 - Bell(dist - holeR);
                white = Math.Clamp(white * (1.0 - hole), 0, 1);
                dark = Math.Clamp(dark * (1.0 - hole * 0.4), 0, 1);

                var coverage = Math.Clamp(Math.Max(white, dark * 0.9), 0, 1);
                if (coverage <= 0.01) continue;

                var idx = (y * diameter + x) * 4;
                var a = (byte)Math.Round(coverage * 255);
                var useWhite = white >= dark;
                var v = useWhite ? a : (byte)0; // premultiplied: white = alpha, black = 0
                pixels[idx] = v;
                pixels[idx + 1] = v;
                pixels[idx + 2] = v;
                pixels[idx + 3] = a;
            }
        }
        return pixels;
    }

    /// <summary>Creates a native cursor (HICON with fIcon=false) from 32bpp BGRA premultiplied bits.</summary>
    private static nint BuildHCursor(byte[] bgra, int width, int height, int hotspotX, int hotspotY)
    {
        var hdc = GetDC(0);
        try
        {
            var bmi = new BITMAPINFO
            {
                biSize = 40,
                biWidth = width,
                biHeight = -height, // top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            };

            var hbmColor = CreateDIBSection(hdc, ref bmi, 0, out var bits, 0, 0);
            if (hbmColor == 0)
            {
                Dbg.Log($"CursorInterop: CreateDIBSection failed err={Marshal.GetLastWin32Error()}");
                return 0;
            }
            Marshal.Copy(bgra, 0, bits, bgra.Length);

            // Monochrome AND-mask, all zero = use the color pixels. For a cursor
            // created through CreateIconIndirect the mask covers just the icon height.
            var maskStride = ((width + 15) / 16) * 2;
            var maskBits = new byte[maskStride * height];

            var hbmMask = CreateBitmap(width, height, 1, 1, maskBits);
            if (hbmMask == 0)
            {
                DeleteObject(hbmColor);
                Dbg.Log($"CursorInterop: CreateBitmap(mask) failed err={Marshal.GetLastWin32Error()}");
                return 0;
            }

            var info = new ICONINFO
            {
                fIcon = 0,
                xHotspot = (uint)hotspotX,
                yHotspot = (uint)hotspotY,
                hbmMask = hbmMask,
                hbmColor = hbmColor,
            };
            var hCursor = CreateIconIndirect(ref info);
            if (hCursor == 0)
            {
                Dbg.Log($"CursorInterop: CreateIconIndirect failed err={Marshal.GetLastWin32Error()}");
            }

            DeleteObject(hbmColor);
            DeleteObject(hbmMask);
            return hCursor;
        }
        finally { ReleaseDC(0, hdc); }
    }

    private delegate int NativeCreateFromHCursor(nint self, nint hCursor, out nint result);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string source, int length, out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO bmi, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(nint hwnd, nint hdc);
}
