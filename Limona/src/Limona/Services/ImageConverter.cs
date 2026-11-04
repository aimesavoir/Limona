using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Limona.Interop;

namespace Limona.Services;

internal static class ImageConverter
{
    private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Modern web formats
        ".avif", ".avis", ".webp", ".web",
        // Standard photographic formats
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".jif",
        // High efficiency & Apple formats
        ".heic", ".heif", ".hif",
        // Standard bitmap & icon formats
        ".bmp", ".dib", ".gif", ".ico", ".cur",
        // Professional, print & graphics
        ".tiff", ".tif",
        ".svg", ".svgz",
        ".tga", ".dds", ".hdr", ".exr",
        ".psd", ".psb",
        // Camera RAW formats
        ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".rw2", ".orf", ".pef",
        // JPEG 2000
        ".jp2", ".j2k", ".jpf", ".jpx", ".jpm"
    };

    public static bool IsImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && s_imageExtensions.Contains(ext);
    }

    /// <summary>Every supported image extension, sorted (used by the Help window).</summary>
    public static string[] SupportedImageExtensions =>
        [.. s_imageExtensions.OrderBy(e => e, StringComparer.OrdinalIgnoreCase)];

    public static Task<string> ConvertAsync(string sourcePath, string targetFormat)
    {
        // Run the whole pipeline on a background thread: WIC decoding fails on
        // the WinUI DispatcherQueue thread (0x88982F50), but works on worker MTA threads.
        return Task.Run(() => ConvertAsyncCore(sourcePath, targetFormat));
    }

    private static async Task<string> ConvertAsyncCore(string sourcePath, string targetFormat)
    {
        try
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Source file not found", sourcePath);

            var dir = Path.GetDirectoryName(sourcePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var baseName = Path.GetFileNameWithoutExtension(sourcePath);
            var targetExt = targetFormat.StartsWith('.') ? targetFormat.ToLowerInvariant() : "." + targetFormat.ToLowerInvariant();

            var outputPath = GetUniqueDestinationPath(dir, baseName, targetExt, sourcePath);

            Dbg.Log($"ImageConverter: converting '{sourcePath}' to '{outputPath}' ({targetFormat})");

            if (targetExt is ".pdf")
            {
                await ConvertToPdfAsync(sourcePath, outputPath);
                return outputPath;
            }

            if (targetExt is ".ico")
            {
                await ConvertToIcoAsync(sourcePath, outputPath);
                return outputPath;
            }

            var encoderId = targetExt switch
            {
                ".png" => BitmapEncoder.PngEncoderId,
                ".jpg" or ".jpeg" => BitmapEncoder.JpegEncoderId,
                ".bmp" => BitmapEncoder.BmpEncoderId,
                ".gif" => BitmapEncoder.GifEncoderId,
                ".tiff" or ".tif" => BitmapEncoder.TiffEncoderId,
                _ => BitmapEncoder.PngEncoderId,
            };

            await ConvertWithWicAsync(sourcePath, outputPath, encoderId);
            return outputPath;
        }
        catch (Exception ex)
        {
            Dbg.Log("ImageConverter THREW: " + ex);
            throw;
        }
    }

    /// <summary>Public helper: returns a unique file path that doesn't overwrite an existing file.</summary>
    public static string GetUniqueOutputPath(string dir, string baseName, string ext)
    {
        var dest = Path.Combine(dir, $"{baseName}{ext}");
        var counter = 1;
        while (File.Exists(dest))
        {
            dest = Path.Combine(dir, $"{baseName}_{counter}{ext}");
            counter++;
        }
        return dest;
    }

    private static string GetUniqueDestinationPath(string dir, string baseName, string targetExt, string sourcePath)
    {
        var dest = Path.Combine(dir, $"{baseName}{targetExt}");
        if (!File.Exists(dest) || string.Equals(dest, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(dest, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                dest = Path.Combine(dir, $"{baseName}_converted{targetExt}");
            }
            else
            {
                return dest;
            }
        }

        var counter = 1;
        while (File.Exists(dest))
        {
            dest = Path.Combine(dir, $"{baseName}_{counter}{targetExt}");
            counter++;
        }

        return dest;
    }

    private static async Task ConvertWithWicAsync(string sourcePath, string outputPath, Guid encoderId)
    {
        var decoder = await OpenDecoderAsync(sourcePath);

        var isJpegOrBmp = encoderId == BitmapEncoder.JpegEncoderId || encoderId == BitmapEncoder.BmpEncoderId;
        var alphaMode = isJpegOrBmp ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied;

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            alphaMode,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        // Encode directly into the destination file stream: copying bytes
        // between .NET streams and WinRT streams loses data (the .NET wrapper
        // discards writes), which corrupts decode/encode round-trips.
        var dstFile = await CreateReplaceStorageFileAsync(outputPath);
        using var outStream = await dstFile.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(encoderId, outStream);

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            alphaMode,
            decoder.PixelWidth,
            decoder.PixelHeight,
            decoder.DpiX,
            decoder.DpiY,
            pixelData.DetachPixelData());

        await encoder.FlushAsync();
    }

    /// <summary>Opens a decoder directly from the source file (no .NET stream interop).</summary>
    private static async Task<BitmapDecoder> OpenDecoderAsync(string sourcePath)
    {
        var srcFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(sourcePath);
        using var srcStream = await srcFile.OpenAsync(FileAccessMode.Read);
        return await BitmapDecoder.CreateAsync(srcStream);
    }

    /// <summary>Creates/overwrites the output file so its WinRT stream can be encoded into.</summary>
    private static async Task<Windows.Storage.StorageFile> CreateReplaceStorageFileAsync(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath)!;
        var name = Path.GetFileName(outputPath);
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(dir);
        return await folder.CreateFileAsync(name, Windows.Storage.CreationCollisionOption.ReplaceExisting);
    }

    /// <summary>Decodes an image as BGRA pixel buffer, downsampled so max(w,h) <= maxSide (0 = original size).</summary>
    public static async Task<(byte[] Pixels, uint Width, uint Height, uint OriginWidth, uint OriginHeight)> DecodeScaledPixelsAsync(
        string sourcePath, int maxSide)
    {
        var decoder = await OpenDecoderAsync(sourcePath);

        var transform = new BitmapTransform();
        uint targetW = decoder.PixelWidth, targetH = decoder.PixelHeight;
        var maxDim = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
        if (maxSide is > 0 && maxDim > maxSide)
        {
            var factor = (double)maxSide / maxDim;
            targetW = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * factor));
            targetH = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * factor));
            transform.ScaledWidth = targetW;
            transform.ScaledHeight = targetH;
        }

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        return (pixelData.DetachPixelData(), targetW, targetH, decoder.PixelWidth, decoder.PixelHeight);
    }

    /// <summary>Decodes a grayscale mask PNG as a byte buffer, scaled to exactly width x height.</summary>
    public static async Task<byte[]> DecodeGrayAsync(string maskPath, uint width, uint height)
    {
        var decoder = await OpenDecoderAsync(maskPath);

        var transform = new BitmapTransform();
        if (decoder.PixelWidth != width || decoder.PixelHeight != height)
        {
            transform.ScaledWidth = width;
            transform.ScaledHeight = height;
        }

        // Note: GetPixelDataAsync rejects BitmapPixelFormat.Gray8 here
        // (E_INVALIDARG), so decode as Bgra8 and take the R channel — OpenCV
        // wrote the grayscale PNG with R = G = B.
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        var raw = pixelData.DetachPixelData(); // 4 bytes per pixel
        var gray = new byte[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            gray[i] = raw[i * 4];
        }
        return gray;
    }

    /// <summary>Encodes BGRA (straight alpha) pixels into PNG bytes.</summary>
    public static async Task<byte[]> EncodePngBytesAsync(byte[] bgra, uint width, uint height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            width, height, 96, 96, bgra);
        await encoder.FlushAsync();
        return await ReadStreamBytesAsync(stream);
    }

    /// <summary>
    /// Encodes BGRA pixels into JPEG bytes at a given quality (0.05–0.98).
    /// The "ImageQuality" encoder option is the only way to drive the WIC JPEG
    /// quality through the WinRT BitmapEncoder surface.
    /// </summary>
    public static async Task<byte[]> EncodeJpegBytesAsync(byte[] bgra, uint width, uint height, double quality)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = new Dictionary<string, BitmapTypedValue>
        {
            ["ImageQuality"] = new BitmapTypedValue(Math.Clamp(quality, 0.05, 0.98), PropertyType.Single),
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            width, height, 96, 96, bgra);
        await encoder.FlushAsync();
        return await ReadStreamBytesAsync(stream);
    }

    /// <summary>Scales BGRA pixels to exactly dw x dh with bilinear filtering (all 4 channels).</summary>
    public static byte[] BilinearResizeRgba(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var fy = (y + 0.5) * sh / dh - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(fy), 0, sh - 1);
            var y1 = Math.Min(sh - 1, y0 + 1);
            var wy = Math.Clamp(fy - y0, 0, 1);
            var sy0 = y0 * sw;   // NOTE: no *4 here — pixel base index receives it below
            var sy1 = y1 * sw;
            var drow = y * dw;
            for (var x = 0; x < dw; x++)
            {
                var fx = (x + 0.5) * sw / dw - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(fx), 0, sw - 1);
                var x1 = Math.Min(sw - 1, x0 + 1);
                var wx = Math.Clamp(fx - x0, 0, 1);

                var i00 = (sy0 + x0) * 4;
                var i10 = (sy0 + x1) * 4;
                var i01 = (sy1 + x0) * 4;
                var i11 = (sy1 + x1) * 4;
                var di = (drow + x) * 4;
                for (var ch = 0; ch < 4; ch++)
                {
                    var v00 = src[i00 + ch];
                    var v10 = src[i10 + ch];
                    var v01 = src[i01 + ch];
                    var v11 = src[i11 + ch];
                    var top = v00 + (v10 - v00) * wx;
                    var bottom = v01 + (v11 - v01) * wx;
                    dst[di + ch] = (byte)Math.Clamp(top + (bottom - top) * wy, 0, 255);
                }
            }
        }
        return dst;
    }

    /// <summary>Reads the oriented pixel dimensions of an image file (decode-light).</summary>
    public static async Task<(uint Width, uint Height)> GetImageDimensionsAsync(string path)
    {
        var decoder = await OpenDecoderAsync(path);
        return (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
    }

    /// <summary>Wraps raw bytes into an InMemoryRandomAccessStream for WinRT readers.</summary>
    public static async Task<InMemoryRandomAccessStream> BytesToWinRtStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        stream.Seek(0);
        return stream;
    }

    /// <summary>
    /// Bridges raw BGRA straight-alpha pixels into a XAML-displayable bitmap.
    /// SoftwareBitmap.Convert() is a no-op for Bgra8-only mode changes, so the
    /// premultiplication is done here explicitly; otherwise XAML treats the
    /// buffer as premultiplied and ghost colors leak through transparent pixels.
    /// </summary>
    public static SoftwareBitmap CreateSoftwareBitmapFromPixels(byte[] bgra, uint width, uint height)
    {
        var n = (int)(width * height);
        var premul = new byte[bgra.Length];
        System.Buffer.BlockCopy(bgra, 0, premul, 0, bgra.Length);
        for (var i = 0; i < n; i++)
        {
            var a = premul[i * 4 + 3];
            if (a != 255)
            {
                premul[i * 4 + 0] = (byte)(premul[i * 4 + 0] * a / 255);
                premul[i * 4 + 1] = (byte)(premul[i * 4 + 1] * a / 255);
                premul[i * 4 + 2] = (byte)(premul[i * 4 + 2] * a / 255);
            }
        }
        return SoftwareBitmap.CreateCopyFromBuffer(
            premul.AsBuffer(), BitmapPixelFormat.Bgra8, (int)width, (int)height, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>
    /// Reads all bytes from a WinRT stream via DataReader. IMPORTANT: DataReader
    /// spans must be consumed with ReadBuffer per LoadAsync — a Load+ReadBytes(sliced
    /// array) loop was verified to return zeroed memory on this machine.
    /// </summary>
    private static async Task<byte[]> ReadStreamBytesAsync(IRandomAccessStream stream)
    {
        stream.Seek(0);
        var reader = new DataReader(stream.GetInputStreamAt(0));
        using var output = new MemoryStream();
        try
        {
            while (true)
            {
                var loaded = await reader.LoadAsync(1 << 20); // 1MB chunks
                if (loaded == 0) break;
                var buffer = reader.ReadBuffer(loaded);
                var chunk = buffer.ToArray();
                output.Write(chunk, 0, chunk.Length);
            }
        }
        finally
        {
            reader.DetachStream();
        }
        if (output.Length != (long)stream.Size && stream.Size > 0)
            throw new IOException($"Stream read incomplete: {output.Length}/{stream.Size} bytes");
        return output.ToArray();
    }

    private static async Task ConvertToIcoAsync(string sourcePath, string outputPath)
    {
        // Decode source image, clamp to 256x256, encode as PNG, wrap in ICO header
        var decoder = await OpenDecoderAsync(sourcePath);

        // Clamp icon dimensions to max 256x256 for standard Windows icons
        var width = decoder.PixelWidth;
        var height = decoder.PixelHeight;
        var transform = new BitmapTransform();
        if (width > 256 || height > 256)
        {
            var max = Math.Max(width, height);
            transform.ScaledWidth = (uint)Math.Round((double)width / max * 256);
            transform.ScaledHeight = (uint)Math.Round((double)height / max * 256);
            width = transform.ScaledWidth;
            height = transform.ScaledHeight;
        }

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        using var pngStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, pngStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            width,
            height,
            96,
            96,
            pixelData.DetachPixelData());

        await encoder.FlushAsync();
        var pngBytes = await ReadStreamBytesAsync(pngStream);

        // Write ICO format (ICONDIR + ICONDIRENTRY + PNG data)
        using var icoFile = File.Create(outputPath);
        using var writer = new BinaryWriter(icoFile);

        // ICONDIR
        writer.Write((ushort)0); // Reserved
        writer.Write((ushort)1); // Type: 1 = ICO
        writer.Write((ushort)1); // Count: 1 image

        // ICONDIRENTRY
        writer.Write((byte)(width >= 256 ? 0 : width));   // Width (0 = 256)
        writer.Write((byte)(height >= 256 ? 0 : height)); // Height (0 = 256)
        writer.Write((byte)0);  // Color count (0 = >=8bpp)
        writer.Write((byte)0);  // Reserved
        writer.Write((ushort)1); // Color planes
        writer.Write((ushort)32); // Bits per pixel
        writer.Write((uint)pngBytes.Length); // Size of image data
        writer.Write((uint)22);  // Offset of image data (6 header + 16 entry = 22)

        // Image data
        writer.Write(pngBytes);
    }

    private static async Task ConvertToPdfAsync(string sourcePath, string outputPath)
    {
        // Decode source image, re-encode as JPEG, then embed in a valid minimalist PDF 1.4
        var decoder = await OpenDecoderAsync(sourcePath);
        var width = (int)decoder.PixelWidth;
        var height = (int)decoder.PixelHeight;

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        using var jpgStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, jpgStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            decoder.PixelWidth,
            decoder.PixelHeight,
            decoder.DpiX,
            decoder.DpiY,
            pixelData.DetachPixelData());

        await encoder.FlushAsync();
        var jpgBytes = await ReadStreamBytesAsync(jpgStream);

        // Build PDF document containing this JPEG
        using var pdfFile = File.Create(outputPath);
        using var writer = new StreamWriter(pdfFile, Encoding.ASCII);

        var offsets = new List<long>();

        void WriteObj(string content)
        {
            writer.Flush();
            offsets.Add(pdfFile.Position);
            writer.Write(content);
        }

        writer.Write("%PDF-1.4\n");

        // 1: Catalog
        WriteObj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // 2: Pages
        WriteObj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // 3: Page
        WriteObj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im4 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");

        // 4: Image XObject
        writer.Flush();
        offsets.Add(pdfFile.Position);
        writer.Write($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpgBytes.Length} >>\nstream\n");
        writer.Flush();

        pdfFile.Write(jpgBytes, 0, jpgBytes.Length);
        writer.Write("\nendstream\nendobj\n");

        // 5: Contents stream (draw image)
        var streamContent = $"q\n{width} 0 0 {height} 0 0 cm\n/Im4 Do\nQ\n";
        var streamBytes = Encoding.ASCII.GetBytes(streamContent);
        WriteObj($"5 0 obj\n<< /Length {streamBytes.Length} >>\nstream\n{streamContent}endstream\nendobj\n");

        // Xref
        writer.Flush();
        var startXref = pdfFile.Position;
        writer.Write($"xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            writer.Write($"{offset:D10} 00000 n \n");
        }

        writer.Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{startXref}\n%%EOF\n");
        writer.Flush();
    }
}
