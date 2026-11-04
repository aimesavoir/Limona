using System.IO;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using Windows.Foundation.Metadata;
using Windows.Media;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Limona.Services;

/// <summary>Per-format conversion settings chosen in the audio window.</summary>
public sealed record AudioConvertSettings(
    int BitrateKbps,     // MP3 / M4A / OGG
    uint SampleRate,     // WAV
    uint BitsPerSample,  // WAV (16/24; 32 = float)
    int Channels         // 0 = keep source (WAV & OGG), 1 = mono, 2 = stereo
);

/// <summary>
/// Audio conversions on Windows Media Foundation: MP3 / M4A / WAV natively via
/// MediaTranscoder, OGG via a PCM detour + Concentus (pure C# Opus encoder
/// muxed into an OggOpus container).
/// </summary>
public static class AudioConverter
{
    public static bool IsAudioFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp3" or ".wav" or ".m4a" or ".aac" or ".flac" or ".ogg" or ".wma" or ".opus" or ".webm"
            or ".aac";
    }

    /// <summary>Duration of an audio file via its Windows property store.</summary>
    public static async System.Threading.Tasks.Task<TimeSpan> GetDurationAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var music = await file.Properties.GetMusicPropertiesAsync();
            return music.Duration;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>Converts an audio file to the requested format; returns the output path.
    /// Reports linear progress 0..1 through <paramref name="progress"/> when given.</summary>
    public static async System.Threading.Tasks.Task<string> ConvertAsync(
        string sourcePath, string targetExt, AudioConvertSettings settings,
        IProgress<double>? progress = null)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var destPath = ImageConverter.GetUniqueOutputPath(dir, baseName + "_converted", targetExt);

        if (targetExt.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            await ConvertToOggAsync(sourcePath, destPath, settings, progress);
        }
        else
        {
            // Predicted output size lets the transcode loop report real % progress.
            var duration = await GetDurationAsync(sourcePath);
            var predicted = EstimateOutputBytes(targetExt, settings, duration.TotalSeconds);
            var profile = BuildProfile(targetExt, settings);
            await TranscodeAsync(sourcePath, destPath, profile, predicted, progress);
        }
        return destPath;
    }

    private static long EstimateOutputBytes(string targetExt, AudioConvertSettings settings, double seconds)
    {
        if (seconds <= 0) return 0;
        if (targetExt.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            var channels = settings.Channels is 1 or 2 ? settings.Channels : 2;
            var bytesPerSample = settings.BitsPerSample / 8;
            return (long)(44 + seconds * settings.SampleRate * channels * bytesPerSample);
        }
        // Lossy CBR-ish estimate with a little container overhead headroom.
        return (long)(seconds * Math.Clamp(settings.BitrateKbps, 32, 320) * 1000 / 8 * 1.03);
    }

    private static MediaEncodingProfile BuildProfile(string targetExt, AudioConvertSettings settings)
    {
        MediaEncodingProfile profile;
        if (targetExt.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            profile = MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High);
            profile.Audio.Bitrate = (uint)(Math.Clamp(settings.BitrateKbps, 32, 320) * 1000);
        }
        else if (targetExt.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
        {
            profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
            profile.Audio.Bitrate = (uint)(Math.Clamp(settings.BitrateKbps, 32, 320) * 1000);
        }
        else if (targetExt.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
            var channels = settings.Channels is 1 or 2 ? settings.Channels : 2;
            var bits = settings.BitsPerSample is 16 or 24 or 32 ? settings.BitsPerSample : 16u;
            var properties = AudioEncodingProperties.CreatePcm(settings.SampleRate, (uint)channels, bits);
            if (bits == 32) properties.Subtype = "Float"; // 32-bit float runtime PCM standard
            profile.Audio = properties;
        }
        else
        {
            throw new NotSupportedException("Unknown audio target: " + targetExt);
        }
        return profile;
    }

    private static async System.Threading.Tasks.Task TranscodeAsync(
        string src, string dst, MediaEncodingProfile profile,
        long predictedBytes = 0, IProgress<double>? progress = null,
        int sampleRate = 48000, int channels = 2, uint bitsPerSample = 16)
    {
        var srcFile = await StorageFile.GetFileFromPathAsync(src);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(dst)!);
        var dstFile = await folder.CreateFileAsync(Path.GetFileName(dst), CreationCollisionOption.ReplaceExisting);
        var transcoder = new MediaTranscoder();
        var prepared = await transcoder.PrepareFileTranscodeAsync(srcFile, dstFile, profile);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException("Windows cannot transcode this file: " + prepared.FailureReason);

        // Run the transcode and poll the growing output file every ~150 ms to
        // report a real percentage (MediaFoundation exposes no progress API).
        var encodeTask = prepared.TranscodeAsync().AsTask();
        while (true)
        {
            var done = await System.Threading.Tasks.Task.WhenAny(
                encodeTask, System.Threading.Tasks.Task.Delay(150));
            if (encodeTask.IsCompleted) break;
            if (predictedBytes <= 0) continue;
            var written = new FileInfo(dst).Length;
            progress?.Report(Math.Min(0.95, (double)written / predictedBytes));
        }
        await encodeTask;
        progress?.Report(1.0);
    }

    private static async System.Threading.Tasks.Task ConvertToOggAsync(
        string src, string destPath, AudioConvertSettings settings, IProgress<double>? progress = null)
    {
        var tempWav = Path.Combine(Path.GetTempPath(), $"rm_ogg_{Guid.NewGuid():N}.wav");
        try
        {
            // Media Foundation decodes anything (mp3/wav/flac/m4a…) into PCM 48 kHz.
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
            var channels = settings.Channels is 1 or 2 ? settings.Channels : 2;
            profile.Audio = AudioEncodingProperties.CreatePcm(48000, (uint)channels, 16);
            if (progress is not null) progress.Report(0.03);
            await TranscodeAsync(src, tempWav, profile); // PCM output: size is exact, no % polling value
            if (progress is not null) progress.Report(0.1);

            var (rate, fileChannels, bits, pcm) = ParseWavPcm(tempWav);
            if (rate != 48000) throw new InvalidOperationException("Unexpected PCM rate " + rate);

#pragma warning disable CS0618 // Concentus 2.2 marks direct constructor obsolete in favor of factory methods
            var encoder = new OpusEncoder(48000, fileChannels, OpusApplication.OPUS_APPLICATION_AUDIO)
            {
                Bitrate = Math.Clamp(settings.BitrateKbps, 32, 320) * 1000,
                Complexity = 10,
            };
#pragma warning restore CS0618
            var tags = new OpusTags { Comment = "Converted by Limona" };
            await using (var outStream = File.Create(destPath))
            {
                var writer = new OpusOggWriteStream(encoder, outStream, tags, 48000, fileChannels, false);
                // ~1 second per chunk so the UI progress bar moves smoothly.
                var chunk = 48000 * fileChannels;
                var total = pcm.Length;
                for (var offset = 0; offset < total; offset += chunk)
                {
                    writer.WriteSamples(pcm, offset, Math.Min(chunk, total - offset));
                    if (progress is not null)
                        progress.Report(0.1 + 0.85 * offset / total);
                }
                writer.Finish();
            }
            if (progress is not null) progress.Report(1.0);
        }
        finally
        {
            try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
        }
    }

    /// <summary>Reads the data chunk of a RIFF/WAVE file into interleaved int16 samples.</summary>
    private static (int Rate, int Channels, int Bits, short[] Pcm) ParseWavPcm(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("not RIFF");
        br.ReadUInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("not WAVE");
        int rate = 0, channels = 0, bits = 0;
        byte[]? data = null;
        while (fs.Position < fs.Length - 8)
        {
            var id = new string(br.ReadChars(4));
            var size = br.ReadUInt32();
            if (id == "fmt " && size >= 16)
            {
                var tag = br.ReadUInt16();
                channels = br.ReadUInt16();
                rate = (int)br.ReadUInt32();
                br.ReadUInt32(); br.ReadUInt16();
                bits = br.ReadUInt16();
                if (tag != 1) throw new InvalidDataException("not integer PCM");
                var rest = size - 16;
                if (rest > 0) fs.Seek(rest, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                data = br.ReadBytes((int)size);
                break;
            }
            else
            {
                if (size > fs.Length - fs.Position) break;
                fs.Seek(size + (size % 2), SeekOrigin.Current);
            }
        }
        if (data is null) throw new InvalidDataException("no data chunk");

        if (bits == 16)
        {
            var pcm = new short[data.Length / 2];
            Buffer.BlockCopy(data, 0, pcm, 0, data.Length);
            return (rate, channels, bits, pcm);
        }
        if (bits == 24) // extensible 24-bit PCM written by Media Foundation
        {
            var pcm = new short[data.Length / 3];
            for (var i = 0; i < pcm.Length; i++)
            {
                var v = data[i * 3] | (data[i * 3 + 1] << 8) | (data[i * 3 + 2] << 16);
                pcm[i] = (short)(v >> 8);
            }
            return (rate, channels, bits, pcm);
        }
        throw new InvalidDataException($"unsupported bit depth {bits}");
    }

    // ------------------------------------------------------------------
    // Editor pipeline: decode at a fixed 48 kHz int16 stereo for waveform
    // editing, then encode the (possibly re-arranged) sample buffer out.
    // ------------------------------------------------------------------

    /// <summary>Decodes any supported audio file to interleaved int16 samples
    /// at exactly 48 kHz; channels follow the source (1..2).</summary>
    public static async System.Threading.Tasks.Task<(short[] Pcm, int Rate, int Channels)>
        DecodeToPcmAsync(string sourcePath)
    {
        var tempWav = Path.Combine(
            Path.GetTempPath(), $"rm_decode_{Guid.NewGuid():N}.wav");
        try
        {
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
            profile.Audio = AudioEncodingProperties.CreatePcm(48000, 2, 16);
            await TranscodeAsync(sourcePath, tempWav, profile);
            var (rate, channels, _, pcm) = ParseWavPcm(tempWav);
            return (pcm, rate, channels);
        }
        finally
        {
            try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
        }
    }

    /// <summary>
    /// Encodes an interleaved int16 sample buffer (48 kHz) to the requested
    /// format next to the original file. WAV is written directly; MP3/M4A go
    /// through a temp WAV + MediaTranscoder; OGG uses Concentus/Opus.
    /// </summary>
    public static async System.Threading.Tasks.Task<string> EncodePcmFromSourceAsync(
        short[] pcm, int rate, int channels, string sourcePath,
        string targetExt, AudioConvertSettings settings,
        IProgress<double>? progress = null)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath) + "_edited";
        var destPath = ImageConverter.GetUniqueOutputPath(dir, baseName, targetExt);
        var seconds = pcm.Length / Math.Max(1, channels) / (double)rate;

        if (targetExt.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            var bits = settings.BitsPerSample == 24 ? 24 : 16;
            await System.Threading.Tasks.Task.Run(() => WriteWav(destPath, pcm, rate, channels, bits));
            if (progress is not null) progress.Report(1.0);
            return destPath;
        }

        if (targetExt.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            await EncodePcmToOggAsync(destPath, pcm, rate, channels, settings, progress);
            return destPath;
        }

        // MP3 / M4A: the encoder consumes a file, so stage temp PCM first.
        var tempWav = Path.Combine(Path.GetTempPath(), $"rm_encode_{Guid.NewGuid():N}.wav");
        try
        {
            await System.Threading.Tasks.Task.Run(() => WriteWav(tempWav, pcm, 48000, channels, 16));
            var profile = BuildProfile(targetExt, settings);
            var predicted = (long)(seconds * Math.Clamp(settings.BitrateKbps, 32, 320) * 1000 / 8 * 1.03);
            await TranscodeAsync(tempWav, destPath, profile, predicted, progress);
        }
        finally
        {
            try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
        }
        return destPath;
    }

    /// <summary>Encodes 48 kHz interleave int16 buffer into an OggOpus file.</summary>
    private static System.Threading.Tasks.Task EncodePcmToOggAsync(
        string destPath, short[] pcm, int rate, int channels,
        AudioConvertSettings settings, IProgress<double>? progress)
    {
        return System.Threading.Tasks.Task.Run(async () =>
        {
#pragma warning disable CS0618
            var encoder = new OpusEncoder(48000, channels, OpusApplication.OPUS_APPLICATION_AUDIO)
            {
                Bitrate = Math.Clamp(settings.BitrateKbps, 32, 320) * 1000,
                Complexity = 10,
            };
#pragma warning restore CS0618
            var tags = new OpusTags { Comment = "Edited by Limona" };
            await using (var outStream = File.Create(destPath))
            {
                var writer = new OpusOggWriteStream(encoder, outStream, tags, 48000, channels, false);
                var chunk = 48000 * channels;
                var total = pcm.Length;
                for (var offset = 0; offset < total; offset += chunk)
                {
                    writer.WriteSamples(pcm, offset, Math.Min(chunk, total - offset));
                    if (progress is not null)
                        progress.Report(offset / (double)total);
                }
                writer.Finish();
            }
            if (progress is not null) progress.Report(1.0);
            await System.Threading.Tasks.Task.CompletedTask;
        });
    }

    /// <summary>Writes a RIFF/WAVE file with 16-bit int or 24-bit int samples.</summary>
    public static void WriteWav(string path, short[] pcm, int rate, int channels, int bits)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        byte[] data;
        if (bits == 16)
        {
            data = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, data, 0, data.Length);
        }
        else // 24-bit
        {
            data = new byte[pcm.Length * 3];
            for (var i = 0; i < pcm.Length; i++)
            {
                var v = pcm[i] << 8;
                data[i * 3] = (byte)v;
                data[i * 3 + 1] = (byte)(v >> 8);
                data[i * 3 + 2] = (byte)(v >> 16);
            }
        }
        bw.Write("RIFF"u8);
        bw.Write((uint)(36 + data.Length));
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write((uint)16);
        bw.Write((ushort)1);
        bw.Write((ushort)channels);
        bw.Write((uint)rate);
        bw.Write((uint)(rate * channels * bits / 8));
        bw.Write((ushort)(channels * bits / 8));
        bw.Write((ushort)bits);
        bw.Write("data"u8);
        bw.Write((uint)data.Length);
        bw.Write(data);
    }
}
