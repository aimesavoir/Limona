using System.IO.Compression;
using System.Text;

namespace Limona.Services;

/// <summary>
/// Folder-only operations backing the folder Limona:
///   · Compress ZIP   — zip the folder next to itself
///   · Size           — recursive byte/file/folder count
///   · Change Icon    — desktop.ini + embedded folder.ico + shell notify
/// </summary>
public static class FolderService
{
    // ------------------------------------------------------------------
    // Compress to ZIP
    // ------------------------------------------------------------------

    /// <summary>
    /// Zips the folder (content wrapped in a top-level folder entry) next to
    /// itself; picks "name (2).zip" when the plain name is taken.
    /// </summary>
    public static async Task<string> CompressFolderAsync(string folderPath)
    {
        var parent = Directory.GetParent(folderPath)?.FullName ?? ".";
        var baseName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var dest = Path.Combine(parent, baseName + ".zip");
        for (var i = 2; File.Exists(dest); i++)
        {
            dest = Path.Combine(parent, $"{baseName} ({i}).zip");
        }

        await Task.Run(() => ZipFile.CreateFromDirectory(
            folderPath, dest, CompressionLevel.Optimal, includeBaseDirectory: true));
        return dest;
    }

    // ------------------------------------------------------------------
    // Recursive size
    // ------------------------------------------------------------------

    /// <summary>Recursive walk (per-entry try/catch: skips locked/forbidden nodes).</summary>
    public static async Task<(long Bytes, int Files, int Folders)> ComputeSizeAsync(string root)
    {
        return await Task.Run(() =>
        {
            long bytes = 0;
            var files = 0;
            var folders = 0;
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                foreach (var file in EnumerateNoThrow(Directory.EnumerateFiles(dir)))
                {
                    try
                    {
                        bytes += new FileInfo(file).Length;
                        files++;
                    }
                    catch { /* unreadable — skip */ }
                }
                foreach (var sub in EnumerateNoThrow(Directory.EnumerateDirectories(dir)))
                {
                    folders++;
                    stack.Push(sub);
                }
            }
            return (bytes, files, folders);
        });
    }

    private static IEnumerable<string> EnumerateNoThrow(IEnumerable<string> sequence)
    {
        using var enumerator = sequence.GetEnumerator();
        while (true)
        {
            string? current;
            try { current = enumerator.MoveNext() ? enumerator.Current : null; }
            catch { yield break; }
            if (current is null) yield break;
            yield return current;
        }
    }

    // ------------------------------------------------------------------
    // Colorized default folder icon
    // ------------------------------------------------------------------

    /// <summary>
    /// Renders the classic folder shape in the chosen color as an ICO, then
    /// applies it like any picked .ico file — no image browsing needed.
    /// </summary>
    public static async Task SetColorizedFolderIconAsync(string folderPath, string folderColorHex) =>
        await ApplyGeneratedIconAsync(folderPath, BuildFolderIconIco(folderColorHex));

    /// <summary>
    /// Draws the folder silhouette (tab + body + highlight band, outlined) in
    /// the given color and packages it as an ICO containing every standard
    /// size (16…256 px). Explorer's zoom levels request different sizes, so a
    /// single-image ico would stay unscaled in Large / Extra-large views.
    /// </summary>
    private static byte[] BuildFolderIconIco(string argbHex)
    {
        var color = FromArgbHex(argbHex);
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

        var pngs = new List<byte[]>(sizes.Length);
        foreach (var size in sizes)
        {
            using var bmp = new System.Drawing.Bitmap(size, size);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                DrawFolderSilhouette(g, color, size);
            }
            using var pngStream = new MemoryStream();
            bmp.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
            pngs.Add(pngStream.ToArray());
        }

        // Multi-image ICO container: ICONDIR + ICONDIRENTRY per image.
        using var ico = new MemoryStream();
        WriteU16(ico, 0);                     // reserved
        WriteU16(ico, 1);                     // type: icon
        WriteU16(ico, (ushort)pngs.Count);    // image count
        var offset = 6 + 16 * pngs.Count;
        for (var i = 0; i < pngs.Count; i++)
        {
            ico.WriteByte(sizes[i] == 256 ? (byte)0 : (byte)sizes[i]); // width
            ico.WriteByte(sizes[i] == 256 ? (byte)0 : (byte)sizes[i]); // height (0 = 256)
            ico.WriteByte(0);                 // palette size
            ico.WriteByte(0);                 // reserved
            WriteU16(ico, 1);                 // planes
            WriteU16(ico, 32);                // bpp
            WriteU32(ico, (uint)pngs[i].Length);
            WriteU32(ico, (uint)offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs)
        {
            ico.Write(png, 0, png.Length);
        }
        return ico.ToArray();
    }

    /// <summary>Draws the folder silhouette scaled to the square canvas of the given pixel size.</summary>
    public static void DrawFolderSilhouette(System.Drawing.Graphics g, System.Drawing.Color c, int size)
    {
        var f = size / 128f; // authored at 128-design units

        // Back tab (darker tone), tucked behind the body like the classic icon.
        using (var tabBrush = new System.Drawing.SolidBrush(Shift(c, 0.80)))
        {
            g.FillPath(tabBrush, RoundedRect(12f * f, 28f * f, 58f * f, 22f * f, 7f * f));
        }

        // Body.
        var body = RoundedRect(12f * f, 38f * f, 104f * f, 74f * f, 10f * f);
        using (var bodyBrush = new System.Drawing.SolidBrush(c))
        {
            g.FillPath(bodyBrush, body);
        }

        // Lighter content band over the top half of the body.
        using (var hiBrush = new System.Drawing.SolidBrush(Shift(c, 1.22)))
        {
            g.FillPath(hiBrush, RoundedRect(12f * f, 38f * f, 104f * f, 28f * f, 10f * f));
        }

        // Outline to keep the shape readable on any background.
        using (var pen = new System.Drawing.Pen(Shift(c, 0.62), MathF.Max(1.4f, 2.6f * f)))
        {
            g.DrawPath(pen, body);
        }
    }

    private static void WriteU16(MemoryStream s, ushort v)
    {
        s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8));
    }

    private static void WriteU32(MemoryStream s, uint v)
    {
        s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 24));
    }

    private static System.Drawing.Color FromArgbHex(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return System.Drawing.Color.FromArgb(
            (int)((value >> 24) & 0xFF),
            (int)((value >> 16) & 0xFF),
            (int)((value >> 8) & 0xFF),
            (int)(value & 0xFF));
    }

    /// <summary>Scales each RGB channel (amt &lt; 1 darkens, amt &gt; 1 lightens).</summary>
    private static System.Drawing.Color Shift(System.Drawing.Color c, double amt)
    {
        static byte Ch(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
        if (amt <= 1)
        {
            return System.Drawing.Color.FromArgb(c.A, Ch(c.R * amt), Ch(c.G * amt), Ch(c.B * amt));
        }
        return System.Drawing.Color.FromArgb(
            c.A, Ch(c.R + (255 - c.R) * (amt - 1) * 0.6), Ch(c.G + (255 - c.G) * (amt - 1) * 0.6), Ch(c.B + (255 - c.B) * (amt - 1) * 0.6));
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ------------------------------------------------------------------
    // Custom folder icon (desktop.ini + folder.ico)
    // ------------------------------------------------------------------

    private const string IniSection = "[.ShellClassInfo]";

    /// <summary>
    /// Applies a custom icon: converts any picked image to .ico when needed,
    /// copies it into the folder as "folder.ico", writes desktop.ini, marks the
    /// folder SYSTEM so Explorer reads it, and notifies the shell.
    /// </summary>
    public static async Task SetFolderIconAsync(string folderPath, string pickedImagePath)
    {
        var icoPath = Path.Combine(folderPath, "folder.ico");
        var ext = Path.GetExtension(pickedImagePath);

        byte[] iconBytes;
        if (ext.Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            iconBytes = await File.ReadAllBytesAsync(pickedImagePath);
        }
        else
        {
            // Convert via ImageConverter, then read the result before installing
            // it atomically at the folder's icon path.
            var converted = await ImageConverter.ConvertAsync(pickedImagePath, ".ico");
            iconBytes = await File.ReadAllBytesAsync(converted);
            try { File.Delete(converted); } catch { /* temp cleanup best-effort */ }
        }

        WriteFileAtomically(icoPath, iconBytes, FileAttributes.Hidden);
        FinishIconApply(folderPath);
        return;
    }

    /// <summary>
    /// Applies a fully generated multi-size ICO byte blob (tinted folder,
    /// gallery preset) directly as the folder icon.
    /// </summary>
    public static async Task ApplyGeneratedIconAsync(string folderPath, byte[] icoBytes)
    {
        await Task.Run(() =>
        {
            var icoPath = Path.Combine(folderPath, "folder.ico");
            WriteFileAtomically(icoPath, icoBytes, FileAttributes.Hidden);
            FinishIconApply(folderPath);
        });
    }

    /// <summary>
    /// Applies an icon chosen from the gallery folder: .ico files are kept
    /// as-is when they already carry several sizes, everything else (and
    /// single-size .ico) is re-rendered into a full 16…256 px ICO so the icon
    /// scales cleanly with Explorer's zoom slider.
    /// </summary>
    public static Task ApplyGalleryIconAsync(string folderPath, string sourceFile) => Task.Run(() =>
    {
        var icoBytes = BuildIconBytesForSource(sourceFile);

        var t = ApplyGeneratedIconAsync(folderPath, icoBytes);
        t.GetAwaiter().GetResult();
    });

    /// <summary>
    /// Converts any gallery source (.ico, .png, .jpg, …) into a full multi-size
    /// ICO blob (16…256 px). Also used by the customization window to stage a
    /// previewed icon before the user presses Apply.
    /// </summary>
    public static byte[] BuildIconBytesForSource(string sourceFile) =>
        sourceFile.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
            ? NormalizeIcoBytes(sourceFile)
            : BuildMultiSizeIcoFromImageFile(sourceFile);

    /// <summary>Shared tail: desktop.ini icon line, folder SYSTEM attr, shell refresh.</summary>
    private static void FinishIconApply(string folderPath)
    {
        var iconRef = "folder.ico,0";
        var ini = Path.Combine(folderPath, "desktop.ini");
        var lines = new List<string>();
        if (File.Exists(ini))
        {
            try
            {
                var foundShellClassInfo = false;
                foreach (var line in ReadDesktopIniLines(ini))
                {
                    if (line.Trim().Equals(IniSection, StringComparison.OrdinalIgnoreCase))
                    {
                        if (foundShellClassInfo) continue;
                        foundShellClassInfo = true;
                    }

                    if (line.StartsWith("IconResource", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("IconFile", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("IconIndex", StringComparison.OrdinalIgnoreCase))
                        continue;

                    lines.Add(line);
                }
            }
            catch { lines.Clear(); }
        }

        if (!File.Exists(ini) || lines.Count == 0)
        {
            lines.Add(IniSection);
        }
        else if (!lines.Any(l => l.Trim().Equals(IniSection, StringComparison.OrdinalIgnoreCase)))
        {
            lines.Insert(0, IniSection);
        }

        var headerIdx = lines.FindIndex(l => l.Trim().Equals(IniSection, StringComparison.OrdinalIgnoreCase));
        lines.Insert(headerIdx + 1, $"IconResource={iconRef}");

        WriteDesktopIni(ini, lines);
        try { File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System); } catch { }

        // Folder must carry SYSTEM so Explorer parses desktop.ini.
        var folderAttrs = File.GetAttributes(folderPath);
        File.SetAttributes(folderPath, folderAttrs | FileAttributes.System);

        // Shell refresh: folder itself (desktop.ini re-parse) + parent listing.
        Interop.Win32.NotifyFolderIconChanged(folderPath);
        Interop.Dbg.Log($"FolderService: icon applied to '{folderPath}'");
    }

    /// <summary>
    /// Reads an .ico file: used as-is when it already contains multiple sizes;
    /// otherwise its largest image is re-rendered into a full multi-size ICO.
    /// Returns null when normalization is impossible (caller falls back to
    /// using the file as-is).
    /// </summary>
    private static byte[] NormalizeIcoBytes(string icoPath)
    {
        var bytes = File.ReadAllBytes(icoPath);
        if (bytes.Length < 6)
        {
            return bytes;
        }
        var count = (int)((uint)bytes[4] | ((uint)bytes[5] << 8));
        if (count > 1)
        {
            return bytes;
        }

        try
        {
            // ICONDIRENTRY #0: [6]=width [7]=height, size at [8], offset at [12].
            if (bytes.Length < 22)
            {
                return bytes;
            }
            var dataOffset = (int)((uint)bytes[12] | ((uint)bytes[13] << 8) | ((uint)bytes[14] << 16) | ((uint)bytes[15] << 24));
            var dataSize = (int)((uint)bytes[8] | ((uint)bytes[9] << 8) | ((uint)bytes[10] << 16) | ((uint)bytes[11] << 24));
            if (dataOffset < 0 || dataSize < 1 || dataOffset + dataSize > bytes.Length)
            {
                return bytes;
            }

            using var image = new System.Drawing.Bitmap(new MemoryStream(bytes, dataOffset, dataSize));
            return BuildMultiSizeIcoFromImage(image);
        }
        catch (Exception ex)
        {
            Interop.Dbg.Log("FolderService: ico normalize fell back as-is: " + ex.Message);
            return bytes;
        }
    }

    /// <summary>Decodes any image file and re-renders it into a 16…256 px multi-size ICO.</summary>
    private static byte[] BuildMultiSizeIcoFromImageFile(string imageFile)
    {
        using var image = new System.Drawing.Bitmap(imageFile);
        return BuildMultiSizeIcoFromImage(image);
    }

    public static byte[] BuildMultiSizeIcoFromImage(System.Drawing.Image master)
    {
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
        var pngs = new List<byte[]>(sizes.Length);
        foreach (var size in sizes)
        {
            using var bmp = new System.Drawing.Bitmap(size, size);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(master, 0, 0, size, size);
            }
            using var pngStream = new MemoryStream();
            bmp.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
            pngs.Add(pngStream.ToArray());
        }

        using var ico = new MemoryStream();
        WriteU16(ico, 0);                       // reserved
        WriteU16(ico, 1);                       // type: icon
        WriteU16(ico, (ushort)pngs.Count);      // image count
        var offset = 6 + 16 * pngs.Count;
        for (var i = 0; i < pngs.Count; i++)
        {
            ico.WriteByte(sizes[i] == 256 ? (byte)0 : (byte)sizes[i]);
            ico.WriteByte(sizes[i] == 256 ? (byte)0 : (byte)sizes[i]);
            ico.WriteByte(0);                   // palette size
            ico.WriteByte(0);                   // reserved
            WriteU16(ico, 1);                   // planes
            WriteU16(ico, 32);                  // bpp
            WriteU32(ico, (uint)pngs[i].Length);
            WriteU32(ico, (uint)offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs)
        {
            ico.Write(png, 0, png.Length);
        }
        return ico.ToArray();
    }

    /// <summary>Atomically replaces a folder icon file and restores its shell attributes.</summary>
    private static void WriteDesktopIni(string iniPath, List<string> lines)
    {
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var content = encoding.GetPreamble()
            .Concat(encoding.GetBytes(string.Join(Environment.NewLine, lines) + Environment.NewLine))
            .ToArray();
        WriteFileAtomically(iniPath, content, FileAttributes.Hidden | FileAttributes.System);
    }

    private static IEnumerable<string> ReadDesktopIniLines(string iniPath)
    {
        var bytes = File.ReadAllBytes(iniPath);
        string content;
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
        {
            content = Encoding.Unicode.GetString(bytes, Encoding.Unicode.GetPreamble().Length,
                bytes.Length - Encoding.Unicode.GetPreamble().Length);
        }
        else if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
        {
            content = Encoding.BigEndianUnicode.GetString(bytes, Encoding.BigEndianUnicode.GetPreamble().Length,
                bytes.Length - Encoding.BigEndianUnicode.GetPreamble().Length);
        }
        else if (bytes.Length >= 2 && bytes.Length % 2 == 0 &&
                 bytes.Where((_, index) => index % 2 == 1).Count(value => value == 0) > bytes.Length / 4)
        {
            content = Encoding.Unicode.GetString(bytes).Replace("\0", string.Empty, StringComparison.Ordinal);
        }
        else
        {
            content = Encoding.UTF8.GetString(bytes);
        }

        return content.TrimStart('\uFEFF').Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
    }

    private static void WriteFileAtomically(string path, byte[] contents, FileAttributes attributes)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The folder icon file has no parent directory.");
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.SetAttributes(path, FileAttributes.Normal);

            File.Move(tempPath, path, overwrite: true);
            File.SetAttributes(path, attributes);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.SetAttributes(tempPath, FileAttributes.Normal);
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>Removes the custom icon section from desktop.ini and deletes folder.ico.</summary>
    public static void ResetFolderIcon(string folderPath)
    {
        try
        {
            try { File.Delete(Path.Combine(folderPath, "folder.ico")); } catch { }

            var ini = Path.Combine(folderPath, "desktop.ini");
            if (File.Exists(ini))
            {
                var lines = new List<string>();
                var foundShellClassInfo = false;
                foreach (var line in ReadDesktopIniLines(ini))
                {
                    if (line.Trim().Equals(IniSection, StringComparison.OrdinalIgnoreCase))
                    {
                        if (foundShellClassInfo) continue;
                        foundShellClassInfo = true;
                    }

                    if (line.StartsWith("IconResource", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("IconFile", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("IconIndex", StringComparison.OrdinalIgnoreCase))
                        continue;

                    lines.Add(line);
                }

                // Drop the ini entirely when only the shell class header remains.
                if (!lines.Any(line => !string.IsNullOrWhiteSpace(line) &&
                                       !line.Trim().Equals(IniSection, StringComparison.OrdinalIgnoreCase)))
                {
                    File.SetAttributes(ini, FileAttributes.Normal);
                    File.Delete(ini);
                }
                else
                {
                    WriteDesktopIni(ini, lines);
                }
            }
        }
        catch (Exception ex)
        {
            Interop.Dbg.Log("ResetFolderIcon THREW: " + ex.Message);
        }

        Interop.Win32.NotifyFolderIconChanged(folderPath);
        Interop.Dbg.Log($"FolderService: icon reset for '{folderPath}'");
    }
}
