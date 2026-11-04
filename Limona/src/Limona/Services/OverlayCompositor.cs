using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Limona.Services;

/// <summary>Burns text and picture overlays into a cropped NV12 frame.
/// GDI+ (System.Drawing) does the rasterising; the YUV conversion is plain
/// code so the video pipeline stays Media Foundation only.</summary>
internal static unsafe class OverlayCompositor
{
    private static readonly object s_cacheLock = new();
    private static readonly Dictionary<string, (DateTime Stamp, Image Image)> s_images = new();
    private static readonly Dictionary<string, FontFamily> s_families = new();

    public static bool HasActiveOverlay(EditSpec? spec, long timeTicks)
    {
        if (spec is null) return false;
        foreach (var t in spec.Texts)
            if (timeTicks >= t.StartTicks && timeTicks < t.EndTicks) return true;
        foreach (var i in spec.Images)
            if (timeTicks >= i.StartTicks && timeTicks < i.EndTicks) return true;
        return false;
    }

    public static void Render(byte* nv12, int w, int h, long timeTicks, EditSpec spec, bool bt709)
    {
        var texts = new List<TextOverlay>();
        foreach (var t in spec.Texts)
            if (timeTicks >= t.StartTicks && timeTicks < t.EndTicks) texts.Add(t);
        var images = new List<ImageOverlay>();
        foreach (var i in spec.Images)
            if (timeTicks >= i.StartTicks && timeTicks < i.EndTicks) images.Add(i);
        if (texts.Count == 0 && images.Count == 0) return;

        var stride = w * 4;
        var size = (nuint)(stride * h);
        var bgra = (byte*)NativeMemory.Alloc(size);
        try
        {
            Nv12ToBgra(nv12, w, h, bgra, stride, bt709);
            using (var bmp = new Bitmap(w, h, stride, PixelFormat.Format32bppArgb, (IntPtr)bgra))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                foreach (var img in images) DrawImage(g, img, w, h);
                foreach (var text in texts) DrawText(g, text, w, h);
            }
            BgraToNv12(bgra, stride, w, h, nv12, bt709);
        }
        finally
        {
            NativeMemory.Free(bgra);
        }
    }

    public static void CompositeBgra(byte[] bgra, int width, int height,
        IReadOnlyList<ImageOverlay> images, IReadOnlyList<TextOverlay> texts)
    {
        if (width <= 0 || height <= 0 || bgra.Length < checked(width * height * 4))
            throw new ArgumentOutOfRangeException(nameof(bgra));
        if (images.Count == 0 && texts.Count == 0) return;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = checked(width * 4);
            for (var y = 0; y < height; y++)
                Marshal.Copy(bgra, y * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            foreach (var image in images) DrawImage(graphics, image, width, height);
            foreach (var text in texts) DrawText(graphics, text, width, height);
        }

        data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = checked(width * 4);
            for (var y = 0; y < height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bgra, y * rowBytes, rowBytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void DrawText(Graphics g, TextOverlay o, int w, int h)
    {
        var size = Math.Max(6f, (float)(o.FontSizeFraction * h));
        var style = FontStyle.Regular;
        if (o.Bold || o.FontWeight >= 600) style |= FontStyle.Bold;
        if (o.Italic) style |= FontStyle.Italic;

        FontFamily family;
        lock (s_cacheLock)
        {
            if (!s_families.TryGetValue(o.FontFamily, out var cached))
            {
                try { cached = new FontFamily(o.FontFamily); }
                catch { cached = FontFamily.GenericSansSerif; }
                s_families[o.FontFamily] = cached;
            }
            family = cached;
        }

        // Allow the text to sit partially outside the image, matching the preview
        // drag (the part outside is simply cropped by the image bounds).
        var cx = (float)(o.XFraction * w);
        var cy = (float)(o.YFraction * h);

        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoClip | StringFormatFlags.NoWrap,
        };

        var text = string.IsNullOrEmpty(o.Text) ? " " : o.Text;
        SizeF measured;
        using (var font = new Font(family, size, style, GraphicsUnit.Pixel))
        {
            measured = g.MeasureString(text, font, int.MaxValue, format);
        }

        using var path = new GraphicsPath();
        path.AddString(text, family, (int)style, size,
            new PointF(cx - measured.Width / 2, cy - measured.Height / 2), format);

        var opacity = (int)(Math.Clamp(o.Opacity, 0, 1) * 255);

        // Soft shadow first so light text stays readable on any frame.
        using (var shadow = new SolidBrush(Color.FromArgb(120 * opacity / 255, 0, 0, 0)))
        using (var shadowPath = (GraphicsPath)path.Clone())
        {
            using var offset = new Matrix();
            offset.Translate(size * 0.05f, size * 0.05f);
            shadowPath.Transform(offset);
            g.FillPath(shadow, shadowPath);
        }

        var outlineAlpha = (int)(((o.OutlineArgb >> 24) & 0xFF) * opacity / 255);
        if (outlineAlpha > 0 && o.StrokeFraction > 0)
        {
            var color = Color.FromArgb(outlineAlpha,
                (int)((o.OutlineArgb >> 16) & 0xFF),
                (int)((o.OutlineArgb >> 8) & 0xFF),
                (int)(o.OutlineArgb & 0xFF));
            using var pen = new Pen(color, Math.Max(1.5f, size * (float)o.StrokeFraction))
            {
                LineJoin = LineJoin.Round,
            };
            g.DrawPath(pen, path);
        }

        var fill = Color.FromArgb(
            (int)(((o.ColorArgb >> 24) & 0xFF) * opacity / 255),
            (int)((o.ColorArgb >> 16) & 0xFF),
            (int)((o.ColorArgb >> 8) & 0xFF),
            (int)(o.ColorArgb & 0xFF));
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
    }

    private static void DrawImage(Graphics g, ImageOverlay o, int w, int h)
    {
        var image = LoadImage(o.Path);
        if (image is null) return;
        var destW = (float)(Math.Clamp(o.WidthFraction, 0.02, 2.0) * w);
        var destH = destW * image.Height / image.Width;
        var x = (float)(Math.Clamp(o.XFraction, 0, 1) * w) - destW / 2;
        var y = (float)(Math.Clamp(o.YFraction, 0, 1) * h) - destH / 2;

        using var attrs = new ImageAttributes();
        var matrix = new ColorMatrix { Matrix33 = (float)Math.Clamp(o.Opacity, 0, 1) };
        attrs.SetColorMatrix(matrix);
        g.DrawImage(image,
            Rectangle.Round(new RectangleF(x, y, destW, destH)),
            0, 0, image.Width, image.Height,
            GraphicsUnit.Pixel, attrs);
    }

    private static Image? LoadImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (s_cacheLock)
            {
                if (s_images.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Image;
                using var loaded = Image.FromFile(path);
                var copy = new Bitmap(loaded);
                s_images[path] = (stamp, copy);
                return copy;
            }
        }
        catch (Exception ex)
        {
            Limona.Interop.Dbg.Log("Overlay image failed: " + path + " " + ex.GetType().Name);
            return null;
        }
    }

    private static void Nv12ToBgra(byte* src, int w, int h, byte* dst, int stride, bool bt709)
    {
        var uv = src + (long)w * h;
        // Studio-range BT.601 / BT.709 inverse (H.264 reference constants).
        var uScale = bt709 ? 2.112 : 2.017;
        var vScale = bt709 ? 1.793 : 1.596;
        var uGreen = bt709 ? 0.213 : 0.392;
        var vGreen = bt709 ? 0.533 : 0.813;

        for (var y = 0; y < h; y++)
        {
            var yRow = src + (long)y * w;
            var uvRow = uv + (long)(y / 2) * w;
            var outRow = dst + (long)y * stride;
            for (var x = 0; x < w; x++)
            {
                var c = 1.164 * (yRow[x] - 16);
                var d = uvRow[(x >> 1) << 1] - 128;
                var e = uvRow[((x >> 1) << 1) + 1] - 128;
                var r = c + vScale * e;
                var gg = c - uGreen * d - vGreen * e;
                var b = c + uScale * d;
                var o = outRow + x * 4;
                o[0] = Clamp(b);
                o[1] = Clamp(gg);
                o[2] = Clamp(r);
                o[3] = 255;
            }
        }
    }

    private static void BgraToNv12(byte* src, int stride, int w, int h, byte* dst, bool bt709)
    {
        var uv = dst + (long)w * h;
        // Studio-range forward matrix (integer reference scaled by 1/256).
        double yr, yg, yb, ur, ug, ub, vr, vg, vb;
        if (bt709)
        {
            yr = 47 / 256.0; yg = 157 / 256.0; yb = 16 / 256.0;
            ur = -26 / 256.0; ug = -87 / 256.0; ub = 112 / 256.0;
            vr = 112 / 256.0; vg = -102 / 256.0; vb = -10 / 256.0;
        }
        else
        {
            yr = 66 / 256.0; yg = 129 / 256.0; yb = 25 / 256.0;
            ur = -38 / 256.0; ug = -74 / 256.0; ub = 112 / 256.0;
            vr = 112 / 256.0; vg = -94 / 256.0; vb = -18 / 256.0;
        }

        for (var y = 0; y < h; y++)
        {
            var yRow = src + (long)y * stride;
            var yDst = dst + (long)y * w;
            for (var x = 0; x < w; x++)
            {
                var o = yRow + x * 4;
                yDst[x] = Clamp(16 + yr * o[2] + yg * o[1] + yb * o[0]);
            }
        }

        for (var y = 0; y < h; y += 2)
        {
            var row0 = src + (long)y * stride;
            var row1 = src + (long)Math.Min(y + 1, h - 1) * stride;
            var uvDst = uv + (long)(y / 2) * w;
            for (var x = 0; x < w; x += 2)
            {
                var x1 = Math.Min(x + 1, w - 1);
                var p0 = row0 + x * 4;
                var p1 = row0 + x1 * 4;
                var p2 = row1 + x * 4;
                var p3 = row1 + x1 * 4;
                var b = (p0[0] + p1[0] + p2[0] + p3[0]) / 4.0;
                var g = (p0[1] + p1[1] + p2[1] + p3[1]) / 4.0;
                var r = (p0[2] + p1[2] + p2[2] + p3[2]) / 4.0;
                uvDst[x] = Clamp(128 + ur * r + ug * g + ub * b);
                uvDst[x + 1] = Clamp(128 + vr * r + vg * g + vb * b);
            }
        }
    }

    private static byte Clamp(double v) =>
        v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
}
