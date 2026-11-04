using System.IO;
using NAudio.Wave;
using System.Runtime.InteropServices;
using Windows.Win32.Media.MediaFoundation;
using static Windows.Win32.PInvoke;

namespace Limona.Services;

/// <summary>Audio side of the export: decodes added clips with NAudio (MF
/// under the hood), resamples them to the video's rate and mixes everything
/// into the AAC stream of the sink writer.</summary>
public static unsafe partial class MediaFoundationVideo
{
    private sealed class MixedClip
    {
        public long StartFrame;
        public float Volume = 1f;
        public short[] Samples = Array.Empty<short>(); // stereo interleaved
    }

    /// <summary>Decodes the used part of every added clip into memory, already
    /// resampled to the output rate and stereo.</summary>
    private static List<MixedClip> PrepareAudioClips(
        EditSpec edit, long exportStartTicks, int rate, Action<string>? status)
    {
        var result = new List<MixedClip>();
        foreach (var clip in edit.Audios)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(clip.Path) || !File.Exists(clip.Path)) continue;

                var overlapStart = Math.Max(exportStartTicks, clip.StartTicks);
                var overlapEnd = clip.EndTicks;
                if (overlapEnd - overlapStart < 10_000) continue; // < 1 ms

                var sourceSkip = clip.SourceStartTicks + (overlapStart - clip.StartTicks);
                var duration = TimeSpan.FromTicks(overlapEnd - overlapStart);
                status?.Invoke("Loading audio: " + (clip.Name.Length > 0 ? clip.Name : Path.GetFileName(clip.Path)));

                using var reader = new NAudio.Wave.MediaFoundationReader(clip.Path);
                NAudio.Wave.ISampleProvider provider = reader.ToSampleProvider();
                var sourceRate = provider.WaveFormat.SampleRate;
                var sourceChannels = provider.WaveFormat.Channels;
                if (sourceRate <= 0 || sourceChannels <= 0) continue;

                // Skip the trimmed-off head of the file.
                var skipFrames = (long)Math.Round(sourceSkip / 10_000_000.0 * sourceRate);
                var skipBuf = new float[Math.Max(1, sourceChannels) * 4096];
                while (skipFrames > 0)
                {
                    var want = (int)Math.Min(skipFrames, 4096);
                    var got = provider.Read(skipBuf, 0, want * sourceChannels) / sourceChannels;
                    if (got <= 0) break;
                    skipFrames -= got;
                }

                var outFrames = (long)Math.Round(duration.TotalSeconds * rate);
                if (outFrames <= 0) continue;
                var wantedSourceFrames = (long)Math.Ceiling(duration.TotalSeconds * sourceRate) + 2;
                var raw = new float[wantedSourceFrames * sourceChannels];
                var readFrames = 0;
                while (readFrames < wantedSourceFrames)
                {
                    var want = (int)Math.Min(wantedSourceFrames - readFrames, 8192);
                    var got = provider.Read(raw, readFrames * sourceChannels, want * sourceChannels);
                    if (got <= 0) break;
                    readFrames += got / sourceChannels;
                }
                if (readFrames <= 0) continue;

                var produced = (long)Math.Min(outFrames,
                    Math.Floor(readFrames * (double)rate / sourceRate));
                if (produced <= 0) continue;

                var samples = new short[produced * 2];
                for (long i = 0; i < produced; i++)
                {
                    var srcPos = i * (double)sourceRate / rate;
                    var i0 = (long)srcPos;
                    var frac = (float)(srcPos - i0);
                    var i1 = Math.Min(i0 + 1, readFrames - 1);
                    if (i0 >= readFrames) { i0 = readFrames - 1; i1 = i0; frac = 0; }

                    float l, r;
                    if (sourceChannels == 1)
                    {
                        l = r = Lerp(raw[i0 * sourceChannels], raw[i1 * sourceChannels], frac);
                    }
                    else
                    {
                        l = Lerp(raw[i0 * sourceChannels], raw[i1 * sourceChannels], frac);
                        r = Lerp(raw[i0 * sourceChannels + 1], raw[i1 * sourceChannels + 1], frac);
                    }
                    samples[i * 2] = ToShort(l);
                    samples[i * 2 + 1] = ToShort(r);
                }

                var startFrame = (long)Math.Round(
                    (overlapStart - exportStartTicks) / 10_000_000.0 * rate);
                result.Add(new MixedClip
                {
                    StartFrame = Math.Max(0, startFrame),
                    Volume = (float)Math.Clamp(clip.Volume, 0, 2),
                    Samples = samples,
                });
                Log($"audio clip ready: {Path.GetFileName(clip.Path)} f0={startFrame} frames={produced} vol={clip.Volume:0.##}");
            }
            catch (Exception ex)
            {
                Log($"audio clip failed: {clip.Path} {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }
        return result;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static short ToShort(float v)
    {
        var scaled = v * 32767f;
        if (scaled > 32767f) return short.MaxValue;
        if (scaled < -32768f) return short.MinValue;
        return (short)scaled;
    }

    /// <summary>Mixes the video's PCM with the added clips and writes the
    /// contiguous result. Gaps are padded with silence so the AAC stream is
    /// always continuous.</summary>
    private static void WriteMixedAudio(
        IMFSourceReader reader, int aIdx, IMFSinkWriter writer, uint aS,
        int rate, int srcChannels,
        long startTicks, long spanTicks,
        bool hasBaseAudio, double baseVolume,
        List<MixedClip> clips, Action<string>? status, Action<double>? progress)
    {
        if (spanTicks <= 0) return;
        var spanFrames = (long)Math.Round(spanTicks / 10_000_000.0 * rate);
        if (spanFrames <= 0) return;

        var next = 0L;
        var logCount = 0;
        var lastProgressPercent = -1;

        void ReportProgress(long frame)
        {
            var fraction = Math.Clamp((double)frame / spanFrames, 0, 1);
            var percent = (int)(fraction * 100);
            if (percent > lastProgressPercent || fraction >= 1)
            {
                lastProgressPercent = percent;
                progress?.Invoke(fraction);
            }
        }

        // baseSamples is stereo interleaved and covers exactly [f0, f1).
        void WriteBlock(long f0, long f1, short[]? baseSamples)
        {
            const long MaxBlock = 4096;
            while (f0 < f1)
            {
                var count = (int)Math.Min(MaxBlock, f1 - f0);
                var pcm = new short[count * 2];
                if (baseSamples is not null)
                {
                    for (var i = 0; i < count; i++)
                    {
                        pcm[i * 2] = ClampShort((int)(baseSamples[i * 2] * baseVolume));
                        pcm[i * 2 + 1] = ClampShort((int)(baseSamples[i * 2 + 1] * baseVolume));
                    }
                }

                foreach (var clip in clips)
                {
                    var clipLen = clip.Samples.Length / 2;
                    var from = Math.Max(f0, clip.StartFrame);
                    var to = Math.Min(f1, clip.StartFrame + clipLen);
                    for (var f = from; f < to; f++)
                    {
                        var src = (int)(f - clip.StartFrame) * 2;
                        var dst = (int)(f - f0) * 2;
                        pcm[dst] = ClampShort(pcm[dst] + (int)(clip.Samples[src] * clip.Volume));
                        pcm[dst + 1] = ClampShort(pcm[dst + 1] + (int)(clip.Samples[src + 1] * clip.Volume));
                    }
                }

                MFCreateMemoryBuffer((uint)(pcm.Length * 2), out var buf);
                try
                {
                    byte* pDst;
                    buf.Lock(&pDst, null, null);
                    try
                    {
                        fixed (short* pSrc = pcm)
                            Buffer.MemoryCopy(pSrc, pDst, (uint)(pcm.Length * 2), (uint)(pcm.Length * 2));
                    }
                    finally { buf.Unlock(); }
                    buf.SetCurrentLength((uint)(pcm.Length * 2));

                    MFCreateSample(out var sample);
                    try
                    {
                        sample.AddBuffer(buf);
                        var time = f0 * 10_000_000L / rate;
                        var dur = count * 10_000_000L / rate;
                        sample.SetSampleTime(time);
                        sample.SetSampleDuration(Math.Max(1, dur));
                        writer.WriteSample(aS, sample);
                    }
                    finally { Marshal.FinalReleaseComObject(sample); }
                }
                finally { Marshal.FinalReleaseComObject(buf); }

                f0 += count;
                ReportProgress(f0);
            }
        }

        void FlushGap(long upTo)
        {
            if (upTo > next)
            {
                WriteBlock(next, upTo, null);
                next = upTo;
            }
        }

        if (hasBaseAudio)
        {
            status?.Invoke("Mixing audio…");
            while (true)
            {
                long aTime; uint aFlags;
                IMFSample sample;
                try
                {
                    Windows.Win32.Media_MediaFoundation_IMFSourceReader_Extensions.ReadSample(
                        reader, (uint)aIdx, 0, out _, out aFlags, out aTime, out sample);
                }
                catch (COMException ce)
                {
                    Log("THROW ReadSample(mixed audio): 0x" + ce.HResult.ToString("X8"));
                    break;
                }
                if ((aFlags & ReaderFlagEndOfStream) != 0) break;
                if (sample == null) continue;
                try
                {
                    if (aTime < startTicks) continue;
                    if (aTime >= startTicks + spanTicks) break;

                    sample.ConvertToContiguousBuffer(out var buf);
                    try
                    {
                        byte* pSrc; uint cbMax; uint cbCur;
                        buf.Lock(&pSrc, &cbMax, &cbCur);
                        try
                        {
                            var srcFrames = (int)(cbCur / (uint)(srcChannels * 2));
                            if (srcFrames <= 0) continue;

                            var frame = (long)Math.Round(
                                (aTime - startTicks) / 10_000_000.0 * rate);
                            if (frame < 0) frame = 0;
                            if (frame > next) FlushGap(frame);
                            if (frame >= spanFrames) continue;

                            var available = (int)Math.Min(srcFrames, spanFrames - frame);
                            var baseSamples = new short[available * 2];
                            {
                                var pIn = (short*)pSrc;
                                for (var i = 0; i < available; i++)
                                {
                                    baseSamples[i * 2] = pIn[i * srcChannels];
                                    baseSamples[i * 2 + 1] = srcChannels > 1
                                        ? pIn[i * srcChannels + 1]
                                        : pIn[i * srcChannels];
                                }
                            }

                            var end = frame + available;
                            if (frame < next)
                            {
                                // Overlapping blocks: drop the already written part.
                                var skip = (int)(next - frame);
                                if (skip >= available) continue;
                                WriteBlock(next, end, baseSamples[(skip * 2)..]);
                            }
                            else
                            {
                                WriteBlock(frame, end, baseSamples);
                            }
                            next = Math.Max(next, end);
                            if (logCount++ < 3)
                                Log($"mix block at {frame} frames={available} next={next}");
                        }
                        finally { buf.Unlock(); }
                    }
                    finally { Marshal.FinalReleaseComObject(buf); }
                }
                finally { Marshal.FinalReleaseComObject(sample); }
            }
        }
        else
        {
            status?.Invoke("Writing added audio…");
        }

        FlushGap(spanFrames);
        ReportProgress(spanFrames);
        Log($"mixed audio: frames={next}/{spanFrames} clips={clips.Count} base={hasBaseAudio} vol={baseVolume:0.##}");
    }

    private static short ClampShort(int v) =>
        v > short.MaxValue ? short.MaxValue : v < short.MinValue ? short.MinValue : (short)v;
}
