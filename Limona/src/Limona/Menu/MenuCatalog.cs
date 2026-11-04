using System.Linq;
using Microsoft.UI.Xaml.Media;
using Limona.Services;

namespace Limona.Menu;

/// <summary>
/// Dynamic menu entry representation.
/// </summary>
/// <param name="Label">Full action name displayed in the central hub on hover/execution.</param>
/// <param name="Icon">Factory returning a fresh geometry for the vector icon.</param>
/// <param name="Hint">Informative subtitle/details displayed in the hub.</param>
/// <param name="Badge">Short badge text displayed directly on the pie slice (e.g. "PNG", "JPG").</param>
/// <param name="Action">Optional execution handler receiving the target file's full path.</param>
public sealed record MenuEntry(
    string Label,
    Func<Geometry> Icon,
    string Hint,
    string Badge,
    Func<string, Task<string>>? Action = null
)
{
    // Label/Hint/Badge stay in English on purpose: they are the stable keys
    // used for dispatch (e.g. Label == "Settings"). The UI always renders
    // these localized views instead.
    public string DisplayLabel => Services.Loc.T(Label);
    public string DisplayHint => Services.Loc.T(Hint);
    public string DisplayBadge => Services.Loc.T(Badge);
}

public static class MenuCatalog
{
    private static readonly HashSet<string> s_audioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma"
    };

    private static readonly HashSet<string> s_videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv"
    };

    private static readonly HashSet<string> s_officeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".docm", ".dotx", ".dotm", ".rtf", ".txt",
        ".xlsx", ".xls", ".xlsm", ".xlsb", ".xltx", ".csv",
        ".pptx", ".ppt", ".pptm", ".ppsx", ".potx"
    };

    private static readonly HashSet<string> s_docExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".xlsx", ".pptx", ".txt", ".rtf", ".csv"
    };

    public static readonly MenuEntry[] DefaultEntries =
    [
        new("Images", Icons.Images, "Browse & convert images", "IMG"),
        new("Documents", Icons.Documents, "Document tools & PDF", "DOC"),
        new("Audio", Icons.Audio, "Audio converters & tools", "AUDIO"),
        new("Video", Icons.Video, "Video formats & clips", "VIDEO"),
        new("Other", Icons.Other, "Tools & utilities", "MORE"),
        new("Settings", Icons.Settings, "Limona settings", "SET"),
    ];

    /// <summary>Extension groups shown in the Help window (kept in sync with
    /// the internal sets driving the menu selection).</summary>
    public static (string Name, string[] Extensions, string Actions)[] ExtensionGroups()
    {
        return
        [
            ("Images", Sorted(ImageConverter.SupportedImageExtensions),
                "Convert to PNG / JPG / ICO / PDF · remove background · resize · edit"),
            ("Audio", Sorted(s_audioExtensions),
                "Convert to MP3 / WAV / M4A / OGG · trim, cut & export"),
            ("Video", Sorted(s_videoExtensions),
                "Convert to MP4 / MOV / WMV / GIF · extract audio · trim & export"),
            ("Office documents", Sorted(s_officeExtensions),
                "Convert to PDF / DOCX / TXT / HTML / EPUB / RTF"),
            ("Documents", Sorted(s_docExtensions),
                "Convert to PDF / DOCX / TXT / HTML / EPUB / RTF"),
        ];
    }

    private static string[] Sorted(System.Collections.Generic.IEnumerable<string> set) =>
        set.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToArray();

    public static MenuEntry[] GetEntriesForTarget(string? filePath, string? displayName)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            // Folders get their own action set.
            if (Directory.Exists(filePath))
            {
                return GetFolderEntries();
            }

            if (ImageConverter.IsImageFile(filePath))
            {
                return GetImageConversionEntries();
            }

            var ext = System.IO.Path.GetExtension(filePath);
            if (!string.IsNullOrEmpty(ext))
            {
                if (s_officeExtensions.Contains(ext))
                    return GetOfficeEntries();
                if (s_audioExtensions.Contains(ext))
                    return GetAudioEntries();
                if (s_videoExtensions.Contains(ext))
                    return GetVideoEntries();
                if (s_docExtensions.Contains(ext))
                    return GetDocumentEntries();
            }
        }

        return DefaultEntries;
    }

    /// <summary>
    /// Returns true when the given path maps to a supported menu (folder,
    /// image, audio, video, or office/document file).
    /// Returns false for unsupported files or when nothing is under the cursor,
    /// so the Limona is suppressed entirely.
    /// </summary>
    public static bool IsSupported(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        if (Directory.Exists(filePath)) return true;

        if (!File.Exists(filePath)) return false;

        if (ImageConverter.IsImageFile(filePath)) return true;

        var ext = System.IO.Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return false;

        return s_audioExtensions.Contains(ext)
            || s_videoExtensions.Contains(ext)
            || s_officeExtensions.Contains(ext)
            || s_docExtensions.Contains(ext);
    }

    public static MenuEntry[] GetFolderEntries() =>
    [
        new("Icon", Icons.FolderIcon, "Icon packs, covers, badges & monograms", "ICON"),
        new("Compress ZIP", Icons.Zip, "Archive this folder in place", "ZIP",
            async path => await FolderService.CompressFolderAsync(path)),
        new("Size", Icons.Ruler, "Total size · files · folders", "SIZE"),
        new("Copy Path", Icons.CopyPath, "Put the full folder path on the clipboard", "PATH"),
        new("Terminal", Icons.Terminal, "Open a terminal here", "TERM"),
    ];

    public static MenuEntry[] GetImageConversionEntries()
    {
        var bgHint = Limona.Services.BackgroundRemover.IsModelReady
            ? "AI remove background · Local ONNX"
            : "⚠ Model not downloaded yet";

        return
        [
            new("Convert to PNG", Icons.Png, "Lossless quality · Transparency", "PNG",
                async path => await ImageConverter.ConvertAsync(path, ".png")),

            new("Convert to JPG", Icons.Jpg, "Standard compressed photo", "JPG",
                async path => await ImageConverter.ConvertAsync(path, ".jpg")),

            // Opening the interactive preview window is handled by LimonaWindow
            // (see ExecuteItem); no direct-save action needed here.
            new("Remove Background", Icons.RemoveBg, bgHint, "BG"),

            // The interactive resize window is opened from LimonaWindow.ExecuteItem.
            new("Resize", Icons.Resize, "Width / height · exact file size", "SZ"),

            new("Convert to ICO", Icons.Ico, "Windows icon 256x256", "ICO",
                async path => await ImageConverter.ConvertAsync(path, ".ico")),

            new("Edit", Icons.Settings, "Color · rotate · flip · crop", "EDIT"),

            new("Convert to PDF", Icons.Pdf, "Single-page document PDF", "PDF",
                async path => await ImageConverter.ConvertAsync(path, ".pdf")),
        ];
    }

    private static MenuEntry[] GetAudioEntries() =>
    [
        // Interactive settings windows are opened from LimonaWindow.ExecuteItem.
        new("Convert to MP3", Icons.Audio, "Standard audio format", "MP3"),
        new("Convert to WAV", Icons.Audio, "Uncompressed high quality", "WAV"),
        new("Convert to M4A", Icons.Audio, "Apple audio container", "M4A"),
        new("Convert to OGG", Icons.Audio, "Ogg Opus · exact settings", "OGG"),
        new("Edit Audio", Icons.Settings, "Trim · cut · split · export", "EDIT"),
    ];

    private static MenuEntry[] GetVideoEntries() =>
    [
        // Interactive windows are opened from LimonaWindow.ExecuteItem.
        new("Convert to MP4", Icons.Video, "H.264 · standard web & mobile", "MP4"),
        new("Convert to MOV", Icons.Video, "QuickTime · Apple compatible", "MOV"),
        new("Convert to WMV", Icons.Video, "Windows Media Video", "WMV"),
        new("Convert to GIF", Icons.Gif,   "Animated GIF · looped", "GIF"),
        new("Extract Audio",  Icons.Audio, "Get the sound track · MP3/WAV/M4A/OGG", "EX A"),
        new("Edit Video",     Icons.Settings, "Trim A→B · export MP4", "EDIT"),
    ];

    public static MenuEntry[] GetOfficeEntries() =>
    [
        new("Convert to PDF", Icons.Pdf, "Export as PDF document", "PDF",
            async path => await OfficeConverter.ConvertAsync(path, ".pdf")),
        new("Convert to DOCX", Icons.Documents, "Word document format", "DOCX",
            async path => await OfficeConverter.ConvertAsync(path, ".docx")),
        new("Convert to TXT", Icons.Documents, "Extract plain text", "TXT",
            async path => await OfficeConverter.ConvertAsync(path, ".txt")),
        new("Convert to HTML", Icons.Documents, "Web page format", "HTML",
            async path => await OfficeConverter.ConvertAsync(path, ".html")),
        new("Convert to EPUB", Icons.Documents, "E-Book format", "EPUB",
            async path => await OfficeConverter.ConvertAsync(path, ".epub")),
        new("Convert to RTF", Icons.Documents, "Rich Text Format", "RTF",
            async path => await OfficeConverter.ConvertAsync(path, ".rtf")),
    ];

    private static MenuEntry[] GetDocumentEntries() =>
    [
        new("Convert to PDF", Icons.Pdf, "Portable Document Format", "PDF",
            async path => await OfficeConverter.ConvertAsync(path, ".pdf")),
        new("Convert to DOCX", Icons.Documents, "Microsoft Word Document", "DOCX",
            async path => await OfficeConverter.ConvertAsync(path, ".docx")),
        new("Convert to TXT", Icons.Documents, "Plain text document", "TXT",
            async path => await OfficeConverter.ConvertAsync(path, ".txt")),
        new("Convert to HTML", Icons.Documents, "HyperText Markup", "HTML",
            async path => await OfficeConverter.ConvertAsync(path, ".html")),
        new("Convert to EPUB", Icons.Documents, "E-Book format", "EPUB",
            async path => await OfficeConverter.ConvertAsync(path, ".epub")),
        new("Convert to RTF", Icons.Documents, "Rich Text Format", "RTF",
            async path => await OfficeConverter.ConvertAsync(path, ".rtf")),
    ];
}
