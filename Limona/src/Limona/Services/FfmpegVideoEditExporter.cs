using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Limona.Interop;

namespace Limona.Services;

internal static class FfmpegVideoEditExporter
{
    private sealed record VideoInfo(int Width, int Height, double FrameRate, TimeSpan Duration, bool HasAudio);

    public static async Task<string> ExportAsync(
        string sourcePath,
        double xF,
        double yF,
        double wF,
        double hF,
        TimeSpan start,
        TimeSpan end,
        EditSpec edit,
        IProgress<double>? progress,
        Action<string>? status)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Source video not found.", sourcePath);

        var ffmpeg = await FfmpegService.GetExecutablePathAsync("ffmpeg", status);
        var ffprobe = await FfmpegService.GetExecutablePathAsync("ffprobe", status);
        var info = await ProbeAsync(ffprobe, sourcePath);
        var exportStart = Math.Max(0, start.TotalSeconds);
        var exportEnd = end > start ? Math.Min(end.TotalSeconds, info.Duration.TotalSeconds) : info.Duration.TotalSeconds;
        var duration = exportEnd - exportStart;
        if (duration < 0.25)
            throw new InvalidOperationException("The selected video range is empty or shorter than 0.25 seconds.");

        var cropX = Math.Clamp((int)(info.Width * xF) & ~1, 0, info.Width - 4);
        var cropY = Math.Clamp((int)(info.Height * yF) & ~1, 0, info.Height - 4);
        var cropWidth = Math.Min(Math.Max((int)(info.Width * wF) & ~3, 4), info.Width - cropX);
        var cropHeight = Math.Min(Math.Max((int)(info.Height * hF) & ~3, 4), info.Height - cropY);
        var frameRate = info.FrameRate > 0 ? info.FrameRate : 30;
        var bitrate = Math.Clamp((int)(cropWidth * cropHeight * frameRate * 0.16), 1_200_000, 16_000_000);

        var outputDirectory = Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;
        var outputPath = ImageConverter.GetUniqueOutputPath(
            outputDirectory, Path.GetFileNameWithoutExtension(sourcePath) + "_edited", ".mp4");
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Limona", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-i", sourcePath };
            var filters = new List<string>();
            var videoBase = new StringBuilder();
            videoBase.Append("[0:v]trim=start=").Append(Number(exportStart))
                .Append(":duration=").Append(Number(duration))
                .Append(",setpts=PTS-STARTPTS");
            if (cropX != 0 || cropY != 0 || cropWidth != info.Width || cropHeight != info.Height)
            {
                videoBase.Append(",crop=").Append(cropWidth).Append(':').Append(cropHeight)
                    .Append(':').Append(cropX).Append(':').Append(cropY);
            }
            filters.Add(videoBase.Append("[v0]").ToString());

            var inputIndex = 1;
            var videoLabel = "v0";
            var video = new StringBuilder();
            foreach (var sticker in edit.Images)
            {
                var (layerStart, layerEnd) = ActiveRange(sticker.StartTicks, sticker.EndTicks, exportStart, duration);
                if (layerEnd <= layerStart) continue;
                if (!File.Exists(sticker.Path))
                    throw new FileNotFoundException("Sticker image not found.", sticker.Path);

                var stickerLabel = "sticker" + inputIndex.ToString(CultureInfo.InvariantCulture);
                var nextLabel = "v" + inputIndex.ToString(CultureInfo.InvariantCulture);
                args.Add("-stream_loop");
                args.Add("-1");
                args.Add("-i");
                args.Add(sticker.Path);
                filters.Add($"[{inputIndex}:v]scale={EvenScale(cropWidth * Math.Clamp(sticker.WidthFraction, 0.02, 2.0))}:-2,format=rgba,colorchannelmixer=aa={Number(Math.Clamp(sticker.Opacity, 0, 1))}[{stickerLabel}]");
                filters.Add($"[{videoLabel}][{stickerLabel}]overlay=x={Number(cropWidth * sticker.XFraction)}-overlay_w/2:y={Number(cropHeight * sticker.YFraction)}-overlay_h/2:enable='between(t\\,{Number(layerStart)}\\,{Number(layerEnd)})'[{nextLabel}]");
                videoLabel = nextLabel;
                inputIndex++;
            }

            var textIndex = 0;
            foreach (var text in edit.Texts)
            {
                var (layerStart, layerEnd) = ActiveRange(text.StartTicks, text.EndTicks, exportStart, duration);
                if (layerEnd <= layerStart) continue;

                var textPath = Path.Combine(temporaryDirectory, $"text-{textIndex++}.txt");
                await File.WriteAllTextAsync(textPath, text.Text, new UTF8Encoding(false));
                var fontSize = Math.Max(6, (int)Math.Round(text.FontSizeFraction * cropHeight));
                var alpha = Math.Clamp(text.Opacity, 0, 1);
                var fill = ColorWithAlpha(text.ColorArgb, alpha);
                var outline = ColorWithAlpha(text.OutlineArgb, alpha);
                var borderWidth = (text.OutlineArgb >> 24) == 0
                    ? 0
                    : Math.Max(1, (int)Math.Round(fontSize * Math.Clamp(text.StrokeFraction, 0, 1)));
                var font = FontFile(text.FontFamily, text.Bold || text.FontWeight >= 600, text.Italic);
                if (video.Length == 0) video.Append('[').Append(videoLabel).Append(']');
                else video.Append(',');
                video.Append("drawtext=fontfile=").Append(QuoteFilterPath(font))
                    .Append(":textfile=").Append(QuoteFilterPath(textPath))
                    .Append(":expansion=none:fontsize=").Append(fontSize)
                    .Append(":fontcolor=").Append(fill)
                    .Append(":borderw=").Append(borderWidth)
                    .Append(":bordercolor=").Append(outline)
                    .Append(":x=").Append(Number(cropWidth * text.XFraction)).Append("-text_w/2")
                    .Append(":y=").Append(Number(cropHeight * text.YFraction)).Append("-text_h/2")
                    .Append(":enable='between(t\\,").Append(Number(layerStart))
                    .Append("\\,").Append(Number(layerEnd)).Append(")'");
            }
            if (video.Length == 0)
                filters.Add($"[{videoLabel}]null[vout]");
            else
                filters.Add(video.Append("[vout]").ToString());

            var audioLabels = new List<string>();
            if (info.HasAudio)
            {
                filters.Add($"[0:a:0]atrim=start={Number(exportStart)}:duration={Number(duration)},asetpts=PTS-STARTPTS,volume={Number(Math.Clamp(edit.VideoVolume, 0, 2))}[abase]");
                audioLabels.Add("[abase]");
            }

            var audioIndex = 0;
            foreach (var clip in edit.Audios)
            {
                var (layerStart, layerEnd) = ActiveRange(clip.StartTicks, clip.EndTicks, exportStart, duration);
                if (layerEnd <= layerStart) continue;
                if (!File.Exists(clip.Path))
                    throw new FileNotFoundException("Added audio file not found.", clip.Path);

                var clipStart = clip.StartTicks / (double)TimeSpan.TicksPerSecond;
                var sourceOffset = Math.Max(0, clip.SourceStartTicks +
                    (long)((exportStart + layerStart - clipStart) * TimeSpan.TicksPerSecond));
                var clipDuration = layerEnd - layerStart;
                var delayMs = Math.Max(0, (int)Math.Round(layerStart * 1000));
                args.Add("-ss");
                args.Add(Number(sourceOffset / (double)TimeSpan.TicksPerSecond));
                args.Add("-t");
                args.Add(Number(clipDuration));
                args.Add("-i");
                args.Add(clip.Path);

                var label = "aclip" + audioIndex.ToString(CultureInfo.InvariantCulture);
                audioIndex++;
                filters.Add($"[{inputIndex}:a:0]atrim=duration={Number(clipDuration)},asetpts=PTS-STARTPTS,volume={Number(Math.Clamp(clip.Volume, 0, 2))},adelay={delayMs}|{delayMs}[{label}]");
                audioLabels.Add($"[{label}]");
                inputIndex++;
            }

            if (audioLabels.Count == 1)
            {
                filters.Add($"{audioLabels[0]}anull[aout]");
            }
            else if (audioLabels.Count > 1)
            {
                filters.Add($"{string.Join("", audioLabels)}amix=inputs={audioLabels.Count}:duration=longest:dropout_transition=0,apad,atrim=duration={Number(duration)}[aout]");
            }

            var graph = string.Join(';', filters);
            args.Add("-filter_complex");
            args.Add(graph);
            args.Add("-map");
            args.Add("[vout]");
            if (audioLabels.Count > 0)
            {
                args.Add("-map");
                args.Add("[aout]");
                args.Add("-c:a");
                args.Add("aac");
                args.Add("-b:a");
                args.Add("192k");
            }
            else
            {
                args.Add("-an");
            }
            args.Add("-c:v");
            args.Add("libx264");
            args.Add("-preset");
            args.Add("fast");
            args.Add("-b:v");
            args.Add(bitrate.ToString(CultureInfo.InvariantCulture));
            args.Add("-pix_fmt");
            args.Add("yuv420p");
            args.Add("-movflags");
            args.Add("+faststart");
            args.Add("-t");
            args.Add(Number(duration));
            args.Add("-progress");
            args.Add("pipe:1");
            args.Add("-nostats");
            args.Add(outputPath);

            status?.Invoke("Encoding with FFmpeg…");
            progress?.Report(0);
            Dbg.Log($"FFmpeg edit export: {sourcePath} -> {outputPath} {cropWidth}x{cropHeight} duration={duration:0.###}s text={edit.Texts.Count} images={edit.Images.Count} audio={edit.Audios.Count}");
            await RunFfmpegAsync(ffmpeg, args, duration, progress);

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new IOException("FFmpeg finished without producing a video file.");
            progress?.Report(1);
            return outputPath;
        }
        catch
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); }
            catch (Exception ex) { Dbg.Log("FFmpeg edit export cleanup failed: " + ex.Message); }
            throw;
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); }
            catch (Exception ex) { Dbg.Log("FFmpeg edit temp cleanup failed: " + ex.Message); }
        }
    }

    private static async Task<VideoInfo> ProbeAsync(string ffprobe, string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height,r_frame_rate", "-of", "json", path })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFprobe.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Could not inspect the source video: " + error.Trim());

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var formatDuration = ReadJsonNumber(root.GetProperty("format").GetProperty("duration"), "format duration");
        var videoDuration = 0d;
        var width = 0;
        var height = 0;
        var frameRate = 0d;
        var hasAudio = false;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var type = stream.GetProperty("codec_type").GetString();
            if (type == "audio") hasAudio = true;
            if (type != "video" || width > 0) continue;
            width = stream.GetProperty("width").GetInt32();
            height = stream.GetProperty("height").GetInt32();
            frameRate = ParseFrameRate(stream.GetProperty("r_frame_rate").GetString());
            if (stream.TryGetProperty("duration", out var streamDuration) &&
                streamDuration.ValueKind == JsonValueKind.String &&
                double.TryParse(streamDuration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                videoDuration = seconds;
        }

        var duration = videoDuration > 0 ? videoDuration : formatDuration;
        if (width < 4 || height < 4 || duration <= 0)
            throw new InvalidDataException("FFprobe could not determine the video dimensions and duration.");
        return new VideoInfo(width, height, frameRate, TimeSpan.FromSeconds(duration), hasAudio);
    }

    private static async Task RunFfmpegAsync(
        string ffmpeg, IReadOnlyList<string> args, double duration, IProgress<double>? progress)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var errorLines = new ConcurrentQueue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            errorLines.Enqueue(e.Data);
            while (errorLines.Count > 40) errorLines.TryDequeue(out var removedLine);
        };
        process.BeginErrorReadLine();

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
        {
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) continue;
            if (!long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                continue;
            progress?.Report(Math.Clamp(microseconds / (duration * 1_000_000), 0, 0.98));
        }
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var details = string.Join(Environment.NewLine, errorLines);
            Dbg.Log($"FFmpeg edit export failed ({process.ExitCode}): {details}");
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                ? $"FFmpeg video export failed (exit code {process.ExitCode})."
                : "FFmpeg video export failed: " + details);
        }
    }

    private static (double Start, double End) ActiveRange(long startTicks, long endTicks, double exportStart, double duration)
    {
        var start = Math.Max(0, startTicks / (double)TimeSpan.TicksPerSecond - exportStart);
        var end = Math.Min(duration, endTicks / (double)TimeSpan.TicksPerSecond - exportStart);
        return (start, end);
    }

    private static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0)
            return numerator / denominator;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : 0;
    }

    private static double ReadJsonNumber(JsonElement element, string description)
    {
        var parsed = element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String => double.TryParse(
                element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : double.NaN,
            _ => double.NaN
        };
        if (!double.IsFinite(parsed))
            throw new InvalidDataException($"FFprobe returned an invalid {description}.");
        return parsed;
    }

    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    private static string EvenScale(double value) =>
        Math.Max(2, (int)Math.Round(value / 2) * 2).ToString(CultureInfo.InvariantCulture);

    private static string ColorWithAlpha(uint argb, double opacity)
    {
        var color = $"0x{argb & 0x00FFFFFF:X6}";
        var alpha = ((argb >> 24) & 0xFF) / 255.0 * opacity;
        return color + "@" + Number(alpha);
    }

    private static string QuoteFilterPath(string path)
    {
        var escaped = path.Replace('\\', '/').Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        return "'" + escaped + "'";
    }

    private static string FontFile(string family, bool bold, bool italic)
    {
        var fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        var stem = family.Trim().ToLowerInvariant() switch
        {
            "arial" => "arial",
            "calibri" => "calibri",
            "georgia" => "georgia",
            "impact" => "impact",
            "times new roman" => "times",
            "tahoma" => "tahoma",
            "verdana" => "verdana",
            "courier new" => "cour",
            "comic sans ms" => "comic",
            _ => "segoeui",
        };
        var suffix = bold && italic ? "z" : bold ? "b" : italic ? "i" : "";
        var candidate = Path.Combine(fonts, stem + suffix + ".ttf");
        if (File.Exists(candidate)) return candidate;
        candidate = Path.Combine(fonts, stem + (bold ? "b" : "") + ".ttf");
        return File.Exists(candidate) ? candidate : Path.Combine(fonts, "segoeui.ttf");
    }
}
