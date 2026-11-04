using System.IO;
using System.Net.Http;
using System.IO.Compression;
using FFMpegCore;
using FFMpegCore.Enums;
using Limona.Interop;

namespace Limona.Services;

/// <summary>
/// Wraps FFMpegCore: locates (or auto-downloads) ffmpeg.exe and exposes
/// a simple Convert-to-MP4 API that handles any input format the user
/// might throw at it — MOV, MKV, WebM, FLV, AVI, etc.
/// </summary>
public static class FfmpegService
{
    // Store ffmpeg in the app's local data folder so we don't clutter the
    // user's system and avoid needing admin rights.
    private static readonly string s_ffmpegDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Limona", "ffmpeg");

    private static readonly SemaphoreSlim s_initLock = new(1, 1);
    private static bool s_ready;

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures ffmpeg is available (downloaded if needed) and configure
    /// FFMpegCore's global options to point at it.
    /// Call this once before any conversion; subsequent calls are instant.
    /// </summary>
    public static async System.Threading.Tasks.Task EnsureReadyAsync(Action<string>? statusCallback = null)
    {
        if (s_ready) return;
        await s_initLock.WaitAsync();
        try
        {
            if (s_ready) return;
            await EnsureReadyCore(statusCallback);
            s_ready = true;
        }
        finally
        {
            s_initLock.Release();
        }
    }

    internal static async System.Threading.Tasks.Task<string> GetExecutablePathAsync(
        string executableName, Action<string>? statusCallback = null)
    {
        await EnsureReadyAsync(statusCallback);

        var executableBaseName = Path.GetFileNameWithoutExtension(executableName);
        var fromPath = TryFindOnPath(executableBaseName);
        if (fromPath is not null) return fromPath;

        var localPath = Path.Combine(s_ffmpegDir, executableBaseName + ".exe");
        if (File.Exists(localPath)) return localPath;

        throw new FileNotFoundException($"FFmpeg executable '{executableName}' was not found.");
    }

    /// <summary>
    /// Converts any video file to MP4, MOV, WMV, or GIF using FFmpeg.
    /// Reports progress 0→1 via <paramref name="progress"/>.
    /// Returns the path to the output file.
    /// </summary>
    public static async System.Threading.Tasks.Task<string> ConvertVideoAsync(
        string sourcePath,
        string targetFormat,
        VideoConverter.Mp4Quality quality,
        IProgress<double>? progress,
        System.Threading.CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync();

        targetFormat = (targetFormat ?? "MP4").Trim().ToUpperInvariant();
        var ext = targetFormat switch
        {
            "MOV" => ".mov",
            "WMV" => ".wmv",
            "GIF" => ".gif",
            _     => ".mp4",
        };

        var dir = Path.GetDirectoryName(sourcePath)
                  ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var destPath = ImageConverter.GetUniqueOutputPath(dir, baseName + "_" + targetFormat.ToLowerInvariant(), ext);

        var (width, height, bitrate) = VideoConverter.QualityParams(quality);

        // Probe source to avoid upscaling
        try
        {
            var info = await FFProbe.AnalyseAsync(sourcePath, cancellationToken: cancellationToken);
            var vs = info.PrimaryVideoStream;
            if (vs != null)
            {
                // Scale to fit inside target resolution, keeping aspect ratio
                if (vs.Width > 0 && vs.Width < width)   width  = vs.Width  / 2 * 2;
                if (vs.Height > 0 && vs.Height < height) height = vs.Height / 2 * 2;
            }
        }
        catch (Exception ex)
        {
            Dbg.Log("FfmpegService probe THREW: " + ex.Message);
        }

        width  = Math.Max(2, (width  / 2) * 2);
        height = Math.Max(2, (height / 2) * 2);

        // Get total duration for progress reporting
        TimeSpan duration = TimeSpan.Zero;
        try
        {
            var info = await FFProbe.AnalyseAsync(sourcePath, cancellationToken: cancellationToken);
            duration = info.Duration;
        }
        catch { }

        // Build the FFMpegCore arguments according to target format
        var args = FFMpegArguments
            .FromFileInput(sourcePath)
            .OutputToFile(destPath, overwrite: true, options =>
            {
                switch (targetFormat)
                {
                    case "MOV":
                        options
                            .WithVideoCodec(VideoCodec.LibX264)
                            .WithAudioCodec(AudioCodec.Aac)
                            .WithVideoFilters(vf => vf.Scale(width, height))
                            .WithCustomArgument($"-b:v {bitrate}")
                            .WithCustomArgument("-preset fast")
                            .WithCustomArgument("-movflags +faststart")
                            .WithCustomArgument("-pix_fmt yuv420p");
                        break;

                    case "WMV":
                        options
                            .WithCustomArgument("-c:v")
                            .WithCustomArgument("wmv2")
                            .WithCustomArgument("-b:v")
                            .WithCustomArgument(bitrate)
                            .WithVideoFilters(vf => vf.Scale(width, height))
                            .WithCustomArgument("-c:a")
                            .WithCustomArgument("wmav2")
                            .WithCustomArgument("-b:a")
                            .WithCustomArgument("192k");
                        break;

                    case "GIF":
                        options
                            .WithCustomArgument("-vf")
                            .WithCustomArgument($"fps=15,scale={width}:-2:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse")
                            .WithCustomArgument("-loop")
                            .WithCustomArgument("0")
                            .WithCustomArgument("-an");
                        break;

                    default: // MP4
                        options
                            .WithVideoCodec(VideoCodec.LibX264)
                            .WithAudioCodec(AudioCodec.Aac)
                            .WithVideoFilters(vf => vf.Scale(width, height))
                            .WithCustomArgument($"-b:v {bitrate}")
                            .WithCustomArgument("-preset fast")
                            .WithCustomArgument("-movflags +faststart")
                            .WithCustomArgument("-pix_fmt yuv420p");
                        break;
                }
            });

        if (progress != null && duration > TimeSpan.Zero)
        {
            // NotifyOnProgress callback receives a double from 0-100 (percentage)
            args = args.NotifyOnProgress(pct =>
            {
                progress.Report(Math.Clamp(pct / 100.0, 0.0, 1.0));
            }, duration);
        }

        var processor = args;
        if (cancellationToken != default)
            processor = processor.CancellableThrough(cancellationToken);

        await processor.ProcessAsynchronously(throwOnError: true);
        return destPath;
    }

    /// <summary>
    /// Converts any video file to MP4 (H.264 + AAC) using FFmpeg.
    /// Backward-compatible overload.
    /// </summary>
    public static System.Threading.Tasks.Task<string> ConvertToMp4Async(
        string sourcePath,
        VideoConverter.Mp4Quality quality,
        IProgress<double>? progress,
        System.Threading.CancellationToken cancellationToken = default) =>
        ConvertVideoAsync(sourcePath, "MP4", quality, progress, cancellationToken);

    /// <summary>
    /// Gets video duration via FFprobe (much more reliable than WMF).
    /// </summary>
    public static async System.Threading.Tasks.Task<TimeSpan> GetDurationAsync(string sourcePath)
    {
        await EnsureReadyAsync();
        try
        {
            var info = await FFProbe.AnalyseAsync(sourcePath);
            return info.Duration;
        }
        catch (Exception ex)
        {
            Dbg.Log("FfmpegService GetDuration THREW: " + ex.Message);
            return TimeSpan.Zero;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private – locate or download ffmpeg
    // ─────────────────────────────────────────────────────────────────────────

    private static async System.Threading.Tasks.Task EnsureReadyCore(Action<string>? statusCallback)
    {
        // 1. Try system PATH first
        var fromPath = TryFindOnPath("ffmpeg");
        if (fromPath != null)
        {
            Dbg.Log("FfmpegService: using system ffmpeg at " + fromPath);
            var dir = Path.GetDirectoryName(fromPath)!;
            GlobalFFOptions.Configure(opts => opts.BinaryFolder = dir);
            return;
        }

        // 2. Try our local cached copy
        var localExe = Path.Combine(s_ffmpegDir, "ffmpeg.exe");
        if (File.Exists(localExe))
        {
            Dbg.Log("FfmpegService: using cached ffmpeg at " + localExe);
            GlobalFFOptions.Configure(opts => opts.BinaryFolder = s_ffmpegDir);
            return;
        }

        // 3. Download the official gyan.dev essentials build (x64, ~70 MB zip)
        statusCallback?.Invoke(Loc.T("Downloading FFmpeg (one-time setup)…"));
        Dbg.Log("FfmpegService: downloading ffmpeg…");
        await DownloadFfmpegAsync(statusCallback);

        if (!File.Exists(localExe))
            throw new FileNotFoundException("FFmpeg download failed – ffmpeg.exe not found at " + localExe);

        GlobalFFOptions.Configure(opts => opts.BinaryFolder = s_ffmpegDir);
        Dbg.Log("FfmpegService: ffmpeg ready at " + localExe);
    }

    private static string? TryFindOnPath(string exe)
    {
        var name = exe + ".exe";
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), name);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    private static async System.Threading.Tasks.Task DownloadFfmpegAsync(Action<string>? statusCallback)
    {
        // gyan.dev provides static Windows builds – essentials build is ~70 MB
        // and includes ffmpeg.exe + ffprobe.exe.
        const string Url =
            "https://github.com/GyanD/codexffmpeg/releases/download/7.1.1/ffmpeg-7.1.1-essentials_build.zip";

        Directory.CreateDirectory(s_ffmpegDir);
        var zipPath = Path.Combine(s_ffmpegDir, "ffmpeg_dl.zip");

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);

        statusCallback?.Invoke(Loc.T("Downloading FFmpeg…"));

        using (var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0L;
            using var src = await response.Content.ReadAsStreamAsync();
            using var dst = File.Create(zipPath);
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                read += n;
                if (total > 0)
                    statusCallback?.Invoke(Loc.F(
                        "Downloading FFmpeg… {0} / {1} MB",
                        read / 1024 / 1024, total / 1024 / 1024));
            }
        }

        statusCallback?.Invoke(Loc.T("Extracting FFmpeg…"));
        Dbg.Log("FfmpegService: extracting zip…");

        // Extract only ffmpeg.exe and ffprobe.exe from the zip (skip docs, etc.)
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var name = Path.GetFileName(entry.FullName);
            if (name is "ffmpeg.exe" or "ffprobe.exe")
            {
                var target = Path.Combine(s_ffmpegDir, name);
                entry.ExtractToFile(target, overwrite: true);
                Dbg.Log("FfmpegService: extracted " + name);
            }
        }

        try { File.Delete(zipPath); } catch { }
    }
}
