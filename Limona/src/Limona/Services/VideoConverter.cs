using System.IO;

namespace Limona.Services;

/// <summary>
/// Video conversions: FFmpeg (via FfmpegService) is used as the primary
/// engine for broad format support — MOV, MKV, WebM, FLV, AVI, 3GP, etc.
/// The Windows Media Foundation crop pipeline (MediaFoundationVideo) is
/// kept for the advanced crop+trim use-case.
/// </summary>
public static class VideoConverter
{
    private static readonly HashSet<string> s_exts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".mkv", ".avi", ".webm",
        ".flv", ".mpg", ".mpeg", ".3gp", ".ts", ".mts", ".m2ts"
    };

    public static bool IsVideoFile(string path) =>
        s_exts.Contains(Path.GetExtension(path));

    /// <summary>Duration via FFprobe (handles all formats reliably).</summary>
    public static System.Threading.Tasks.Task<TimeSpan> GetDurationAsync(string path) =>
        FfmpegService.GetDurationAsync(path);

    // ─────────────────────────────────────────────────────────────────────────
    // Quality ladder
    // ─────────────────────────────────────────────────────────────────────────

    public enum Mp4Quality
    {
        Tiny144p,
        Small240p,
        Low360p,
        Sd480p,
        Hd720p,
        FullHd1080p,
        Qhd1440p,
        Uhd4K2160p,
    }

    /// <summary>All quality values in order, for UI combo-box population.</summary>
    public static readonly Mp4Quality[] AllQualities =
    {
        Mp4Quality.Tiny144p,
        Mp4Quality.Small240p,
        Mp4Quality.Low360p,
        Mp4Quality.Sd480p,
        Mp4Quality.Hd720p,
        Mp4Quality.FullHd1080p,
        Mp4Quality.Qhd1440p,
        Mp4Quality.Uhd4K2160p,
    };

    /// <summary>Short display label shown in the combo-box.</summary>
    public static string QualityLabel(Mp4Quality q) => q switch
    {
        Mp4Quality.Tiny144p    => "144p  · ~200 kbps  — smallest file",
        Mp4Quality.Small240p   => "240p  · ~400 kbps  — very small",
        Mp4Quality.Low360p     => "360p  · ~750 kbps  — mobile",
        Mp4Quality.Sd480p      => "480p  ·  1.5 Mbps  — SD",
        Mp4Quality.Hd720p      => "720p  ·  3.5 Mbps  — HD  ★ recommended",
        Mp4Quality.FullHd1080p => "1080p ·  6.5 Mbps  — Full HD",
        Mp4Quality.Qhd1440p   => "1440p ·   12 Mbps  — 2K / QHD",
        Mp4Quality.Uhd4K2160p => "2160p ·   25 Mbps  — 4K UHD",
        _                      => q.ToString(),
    };

    /// <summary>Tooltip explaining what the quality means for file size.</summary>
    public static string QualityTooltip(Mp4Quality q) => q switch
    {
        Mp4Quality.Tiny144p    => "Very low resolution. Best for sharing over slow connections or archiving tiny clips.",
        Mp4Quality.Small240p   => "Low resolution. Good for messaging apps where file size matters most.",
        Mp4Quality.Low360p     => "Acceptable on small screens. Typical mobile streaming quality.",
        Mp4Quality.Sd480p      => "Standard definition. Clear on most screens, manageable file size.",
        Mp4Quality.Hd720p      => "HD quality. Great balance of quality and file size for most uses.",
        Mp4Quality.FullHd1080p => "Full HD. Sharp on large screens. Larger file size.",
        Mp4Quality.Qhd1440p   => "2K resolution. For high-end monitors or editing masters.",
        Mp4Quality.Uhd4K2160p => "4K Ultra HD. Maximum quality. Very large files — use only if your source is 4K.",
        _                      => "",
    };

    /// <summary>Target resolution and bitrate for each quality tier.</summary>
    public static (int Width, int Height, string Bitrate) QualityParams(Mp4Quality q) => q switch
    {
        Mp4Quality.Tiny144p    => (256,  144,  "200k"),
        Mp4Quality.Small240p   => (426,  240,  "400k"),
        Mp4Quality.Low360p     => (640,  360,  "750k"),
        Mp4Quality.Sd480p      => (854,  480,  "1500k"),
        Mp4Quality.Hd720p      => (1280, 720,  "3500k"),
        Mp4Quality.FullHd1080p => (1920, 1080, "6500k"),
        Mp4Quality.Qhd1440p   => (2560, 1440, "12000k"),
        Mp4Quality.Uhd4K2160p => (3840, 2160, "25000k"),
        _                      => (1280, 720,  "3500k"),
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Conversion methods
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Full-file convert to MP4, MOV, WMV, or GIF using FFmpeg.
    /// Handles virtually any input format.
    /// </summary>
    public static System.Threading.Tasks.Task<string> ConvertVideoAsync(
        string sourcePath, string targetFormat, Mp4Quality quality, IProgress<double>? progress = null) =>
        FfmpegService.ConvertVideoAsync(sourcePath, targetFormat, quality, progress);

    /// <summary>
    /// Full-file convert: re-encodes to a clean H.264/AAC MP4 using FFmpeg.
    /// Backward-compatible wrapper around ConvertVideoAsync.
    /// </summary>
    public static System.Threading.Tasks.Task<string> ConvertToMp4Async(
        string sourcePath, Mp4Quality quality, IProgress<double>? progress = null) =>
        FfmpegService.ConvertVideoAsync(sourcePath, "MP4", quality, progress);

    /// <summary>Trim convert: exports only the [start, end) span as MP4 via FFmpeg.</summary>
    public static async System.Threading.Tasks.Task<string> TrimToMp4Async(
        string sourcePath, TimeSpan start, TimeSpan end, IProgress<double>? progress = null)
    {
        await FfmpegService.EnsureReadyAsync();

        var dir = Path.GetDirectoryName(sourcePath)
                  ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var destPath = ImageConverter.GetUniqueOutputPath(dir, baseName + "_trimmed", ".mp4");

        var duration = end - start;
        if (duration <= TimeSpan.Zero) duration = TimeSpan.FromSeconds(1);

        var durationStr = duration.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        var args = FFMpegCore.FFMpegArguments
            .FromFileInput(sourcePath, verifyExists: true, opts =>
            {
                if (start > TimeSpan.Zero)
                    opts.Seek(start);
            })
            .OutputToFile(destPath, overwrite: true, opts =>
            {
                opts.WithVideoCodec(FFMpegCore.Enums.VideoCodec.LibX264)
                    .WithAudioCodec(FFMpegCore.Enums.AudioCodec.Aac)
                    .WithCustomArgument("-t")
                    .WithCustomArgument(durationStr)
                    .WithCustomArgument("-preset fast")
                    .WithCustomArgument("-movflags +faststart")
                    .WithCustomArgument("-pix_fmt yuv420p");
            });

        if (progress != null)
            args = args.NotifyOnProgress(pct =>
                progress.Report(Math.Clamp(pct / 100.0, 0.0, 1.0)), duration);

        await args.ProcessAsynchronously(throwOnError: true);
        return destPath;
    }

    /// <summary>
    /// Geometric crop via the Windows Media Foundation pipeline:
    /// keeps only the region [xF..x+w, yF..y+h] as fractions of the source frame.
    /// Optionally also trims to [start, end) in the same encode pass.
    /// </summary>
    public static System.Threading.Tasks.Task<string> CropToMp4Async(
        string sourcePath, double xF, double yF, double wF, double hF,
        IProgress<double>? progress, System.Action<string>? status,
        TimeSpan start = default, TimeSpan end = default, EditSpec? edit = null) =>
        MediaFoundationVideo.CropToMp4Async(sourcePath, xF, yF, wF, hF, progress, status, start, end, edit);

    /// <summary>Exports a trimmed video with optional crop, text, sticker, and audio edits through FFmpeg.</summary>
    public static System.Threading.Tasks.Task<string> ExportEditedToMp4Async(
        string sourcePath, double xF, double yF, double wF, double hF,
        TimeSpan start, TimeSpan end, EditSpec edit,
        IProgress<double>? progress, System.Action<string>? status) =>
        FfmpegVideoEditExporter.ExportAsync(sourcePath, xF, yF, wF, hF, start, end, edit, progress, status);
}
