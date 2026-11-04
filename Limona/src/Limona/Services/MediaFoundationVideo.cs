using System.IO;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

using static Windows.Win32.PInvoke;

namespace Limona.Services;

/// <summary>
/// Pure Windows Media Foundation pipeline (no ffmpeg, no downloads): decodes
/// the source, crops each NV12 frame in memory, encodes H.264/AAC into MP4 via
/// the Sink Writer, optionally applying a trim window at the same time.
/// Everything below runs on a worker thread.
/// </summary>
public static unsafe partial class MediaFoundationVideo
{
    // Constants not exposed by CsWin32's metadata db - stable documented values.
    private static readonly Guid s_nv12 =
        new(0x3231564E, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    private static readonly Guid s_h264 =
        new(0x34363248, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    private static readonly Guid s_aac =
        new(0x00001610, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    private static readonly Guid s_aacPayloadType =
        new(0x37E48BF5, 0x645E, 0x4C5B, 0x89, 0xDE, 0xAD, 0xA9, 0xE2, 0x9B, 0x69, 0x6A);
    private static readonly Guid s_mtAudioAvgBytes =
        new(0x1AAB75C8, 0xCFEF, 0x451C, 0xAB, 0x95, 0xAC, 0x03, 0x4B, 0x8E, 0x17, 0x31);
    private static readonly Guid s_pcm =
        new(0x00000001, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    private static readonly Guid s_readerAdvancedVideoProcessing =
        new(0x0F81DA2C, 0xB537, 0x4672, 0xA8, 0xB2, 0xA6, 0x81, 0xB1, 0x73, 0x07, 0xA3);
    private static readonly Guid s_mfPdDuration =
        new(0x6C990D13, 0xBB8E, 0x477A, 0x85, 0x9D, 0x0F, 0x23, 0xE1, 0xD5, 0xD0, 0x8C);

    private const uint ReaderFlagEndOfStream = 0x2; // MF_SOURCE_READERF_ENDOFSTREAM
    private const uint MfVersion = 0x00020070u;
    private const uint MfStartupNoSocket = 0x1u;

    private static readonly object s_mfLock = new();
    private static int s_mfRefs;

    // Friendly wrappers over CsWin32's generated extension classes (the raw
    // ComImport interface methods take pointer parameters).
    private static void AttrSetGuid(IMFAttributes t, Guid k, Guid v) =>
        Media_MediaFoundation_IMFAttributes_Extensions.SetGUID(t, k, v);
    private static void AttrSetU32(IMFAttributes t, Guid k, uint v) =>
        Media_MediaFoundation_IMFAttributes_Extensions.SetUINT32(t, k, v);
    private static uint AttrGetU32Opt(IMFAttributes t, Guid k)
    {
        try
        {
            Media_MediaFoundation_IMFAttributes_Extensions.GetUINT32(t, k, out var v);
            return v;
        }
        catch (COMException) { return 0; }
    }
    private static ulong AttrGetU64Opt(IMFAttributes t, Guid k)
    {
        try
        {
            Media_MediaFoundation_IMFAttributes_Extensions.GetUINT64(t, k, out var v);
            return v;
        }
        catch (COMException) { return 0; }
    }
    private static void MtMajorType(IMFMediaType t, out Guid v) =>
        Media_MediaFoundation_IMFMediaType_Extensions.GetMajorType(t, out v);

    private static void Log(string m) => Limona.Interop.Dbg.Log("MF: " + m);

    public static System.Threading.Tasks.Task<string> CropToMp4Async(
        string sourcePath, double xF, double yF, double wF, double hF,
        IProgress<double>? progress, Action<string>? status,
        TimeSpan start = default, TimeSpan end = default, EditSpec? edit = null)
        => System.Threading.Tasks.Task.Run(() =>
            CropToMp4Core(sourcePath, xF, yF, wF, hF, progress, status, start, end, edit));

    /// <summary>
    /// Authoritative video-stream duration. Presentation attributes are tried
    /// first; when they are missing (verified on ASF/WMV and some MP4 files)
    /// the compressed samples are demuxed to measure the real end.
    /// </summary>
    public static System.Threading.Tasks.Task<TimeSpan> VideoDurationAsync(string sourcePath)
        => System.Threading.Tasks.Task.Run(() =>
        {
            AcquireMf();
            try
            {
                MFCreateAttributes(out var attrs, 0);
                HRESULT hr = MFCreateSourceReaderFromURL(sourcePath, attrs, out var reader);
                Marshal.FinalReleaseComObject(attrs);
                if (hr.Failed) return TimeSpan.Zero;
                try
                {
                    var (vIdx, _) = FindStreams(reader);
                    if (vIdx < 0) return TimeSpan.Zero;
                    var ticks = GetVideoDurationTicks(reader, vIdx);
                    return ticks > 0 ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero;
                }
                finally { Marshal.FinalReleaseComObject(reader); }
            }
            catch (Exception ex)
            {
                Log($"VideoDuration failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                return TimeSpan.Zero;
            }
            finally { ReleaseMf(); }
        });

    /// <summary>Temporary diagnostic: reproduce the audio attach on a real
    /// source reader and try several PCM input type variants.</summary>
    public static System.Threading.Tasks.Task<string> DiagnoseAudioAsync(string sourcePath)
        => System.Threading.Tasks.Task.Run(() => DiagnoseAudio(sourcePath));

    private static unsafe string DiagnoseAudio(string sourcePath)
    {
        var lines = new System.Text.StringBuilder();
        void L(string m) { Log("DIAG: " + m); lines.AppendLine(m); }
        AcquireMf();
        try
        {
            MFCreateAttributes(out var attrs, 2);
            AttrSetU32(attrs, s_readerAdvancedVideoProcessing, 1);
            var hr = MFCreateSourceReaderFromURL(sourcePath, attrs, out var reader);
            Marshal.FinalReleaseComObject(attrs);
            if (hr.Failed) { L($"reader 0x{hr.Value:X8}"); return lines.ToString(); }
            try
            {
                var (vIdx, aIdx) = FindStreams(reader);
                L($"streams v={vIdx} a={aIdx}");
                if (aIdx < 0) return lines.ToString();

                MFCreateMediaType(out var wantPcm);
                AttrSetGuid(wantPcm, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
                AttrSetGuid(wantPcm, PInvoke.MF_MT_SUBTYPE, s_pcm);
                AttrSetU32(wantPcm, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                try { reader.SetCurrentMediaType((uint)aIdx, null, wantPcm); }
                catch (COMException ce) { L($"pcm set 0x{ce.HResult:X8}"); return lines.ToString(); }
                Marshal.FinalReleaseComObject(wantPcm);

                reader.GetCurrentMediaType((uint)aIdx, out var pcm);
                var rate = AttrGetU32Opt(pcm, MF_MT_AUDIO_SAMPLES_PER_SECOND);
                var channels = AttrGetU32Opt(pcm, MF_MT_AUDIO_NUM_CHANNELS);
                var bits = AttrGetU32Opt(pcm, MF_MT_AUDIO_BITS_PER_SAMPLE);
                L($"pcm rate={rate} ch={channels} bits={bits}");
                Marshal.FinalReleaseComObject(pcm);

                int vw = 640, vh = 360;
                if (vIdx >= 0)
                {
                    try
                    {
                        reader.GetCurrentMediaType((uint)vIdx, out var touch);
                        Marshal.FinalReleaseComObject(touch);
                        var want = NewVideoType();
                        reader.SetCurrentMediaType((uint)vIdx, null, want);
                        Marshal.FinalReleaseComObject(want);
                        reader.GetCurrentMediaType((uint)vIdx, out var vcur);
                        (var sw, var sh) = GetFrameSize(vcur);
                        vw = (int)sw; vh = (int)sh;
                        Marshal.FinalReleaseComObject(vcur);
                    }
                    catch (COMException ce) { L($"video type 0x{ce.HResult:X8}"); }
                }
                L($"video {vw}x{vh}");

                for (var variant = 0; variant < 4; variant++)
                {
                    var path = Path.Combine(Path.GetTempPath(), "opencode", $"diag_audio_{variant}.mp4");
                    try { File.Delete(path); } catch { }
                    var dhr = MFCreateSinkWriterFromURL(path, null, null, out var w);
                    if (dhr.Failed) { L($"v{variant} writer 0x{dhr.Value:X8}"); continue; }
                    try
                    {
                        try
                        {
                            w.AddStream(WireVideoOut(vw, vh, 30, 1, 1_200_000), out var vs);
                            w.SetInputMediaType(vs, WireVideoIn(vw, vh, 30, 1), null);
                            L($"v{variant} video ok");
                        }
                        catch (COMException ce) { L($"v{variant} video 0x{ce.HResult:X8}"); continue; }

                        MFCreateMediaType(out var aOut);
                        AttrSetGuid(aOut, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
                        AttrSetGuid(aOut, PInvoke.MF_MT_SUBTYPE, s_aac);
                        AttrSetU32(aOut, MF_MT_AVG_BITRATE, 160_000);
                        AttrSetU32(aOut, MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
                        AttrSetU32(aOut, MF_MT_AUDIO_NUM_CHANNELS, channels);
                        if (variant != 2) AttrSetU32(aOut, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                        if (variant != 3) AttrSetU32(aOut, s_mtAudioAvgBytes, rate * channels * 2 / 8);
                        if (variant != 3) aOut!.SetUINT32(s_aacPayloadType, 0);
                        uint aS;
                        try { w.AddStream(aOut, out aS); L($"v{variant} audio AddStream ok s={aS}"); }
                        catch (COMException ce) { L($"v{variant} audio AddStream 0x{ce.HResult:X8}"); Marshal.FinalReleaseComObject(aOut); continue; }
                        Marshal.FinalReleaseComObject(aOut);

                        for (var ii = 0; ii < 3; ii++)
                        {
                            MFCreateMediaType(out var aIn);
                            AttrSetGuid(aIn, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
                            AttrSetGuid(aIn, PInvoke.MF_MT_SUBTYPE, s_pcm);
                            AttrSetU32(aIn, MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
                            AttrSetU32(aIn, MF_MT_AUDIO_NUM_CHANNELS, channels);
                            AttrSetU32(aIn, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                            if (ii >= 1) AttrSetU32(aIn, MF_MT_AUDIO_BLOCK_ALIGNMENT, channels * 2);
                            if (ii >= 2) AttrSetU32(aIn, s_mtAudioAvgBytes, rate * channels * 2);
                            try
                            {
                                w.SetInputMediaType(aS, aIn, null);
                                L($"v{variant} input variant {ii} OK");
                            }
                            catch (COMException ce)
                            {
                                L($"v{variant} input variant {ii} 0x{ce.HResult:X8}");
                            }
                            finally { Marshal.FinalReleaseComObject(aIn); }
                        }
                    }
                    finally { Marshal.FinalReleaseComObject(w); }
                }
                return lines.ToString();
            }
            finally { Marshal.FinalReleaseComObject(reader); }
        }
        finally { ReleaseMf(); }
    }

    public static System.Threading.Tasks.Task<string> DescribeStreamsAsync(string path)
        => System.Threading.Tasks.Task.Run(() =>
        {
            var sb = new System.Text.StringBuilder();
            AcquireMf();
            try
            {
                MFCreateAttributes(out var attrs, 0);
                var hr = MFCreateSourceReaderFromURL(path, attrs, out var reader);
                Marshal.FinalReleaseComObject(attrs);
                if (hr.Failed) return $"open 0x{hr.Value:X8}";
                try
                {
                    for (uint i = 0; i < 8; i++)
                    {
                        try
                        {
                            reader.GetNativeMediaType(i, 0, out var t);
                            MtMajorType(t, out var major);
                            AttrGetGuid(t, PInvoke.MF_MT_SUBTYPE, out var sub);
                            sb.AppendLine($"stream {i}: major={major} sub={sub} " +
                                $"w={GetFrameSize(t).width} h={GetFrameSize(t).height} " +
                                $"rate={AttrGetU32Opt(t, MF_MT_AUDIO_SAMPLES_PER_SECOND)} ch={AttrGetU32Opt(t, MF_MT_AUDIO_NUM_CHANNELS)}");
                            Marshal.FinalReleaseComObject(t);
                        }
                        catch (COMException) { break; }
                    }
                }
                finally { Marshal.FinalReleaseComObject(reader); }
            }
            finally { ReleaseMf(); }
            return sb.ToString();
        });

    private static void AttrGetGuid(IMFAttributes t, Guid k, out Guid v)
    {
        try { Media_MediaFoundation_IMFAttributes_Extensions.GetGUID(t, k, out v); }
        catch (COMException) { v = Guid.Empty; }
    }

    public static System.Threading.Tasks.Task<(int width, int height)> VideoSizeAsync(string sourcePath)
        => System.Threading.Tasks.Task.Run(() =>
        {
            AcquireMf();
            try
            {
                try
                {
                    MFCreateAttributes(out var attrs, 2);
                    AttrSetU32(attrs, s_readerAdvancedVideoProcessing, 1);
                    HRESULT hr = MFCreateSourceReaderFromURL(sourcePath, attrs, out var reader);
                    Marshal.FinalReleaseComObject(attrs);
                    if (hr.Failed) return (0, 0);
                    try
                    {
                        var (vIdx, _) = FindStreams(reader);
                        if (vIdx < 0) return (0, 0);
                        var want = NewVideoType();
                        reader.SetCurrentMediaType((uint)vIdx, null, want);
                        Marshal.FinalReleaseComObject(want);
                        reader.GetCurrentMediaType((uint)vIdx, out var cur);
                        try
                        {
                            var (w, h) = GetFrameSize(cur);
                            return ((int)w, (int)h);
                        }
                        finally { Marshal.FinalReleaseComObject(cur); }
                    }
                    finally { Marshal.FinalReleaseComObject(reader); }
                }
                catch (COMException) { return (0, 0); }
                catch (InvalidOperationException) { return (0, 0); }
            }
            finally { ReleaseMf(); }
        });

    // ------------------------------------------------------------------
    // Core export
    // ------------------------------------------------------------------

    private static string CropToMp4Core(
        string sourcePath, double xF, double yF, double wF, double hF,
        IProgress<double>? progress, Action<string>? status,
        TimeSpan start, TimeSpan end, EditSpec? edit)
    {
        AcquireMf();
        try
        {
            var trimming = end - start >= TimeSpan.FromMilliseconds(250);
            var startTicks = trimming ? start.Ticks : 0;
            var endTicks = trimming ? end.Ticks : long.MaxValue;
            if (trimming && startTicks >= endTicks)
                throw new InvalidOperationException("The trim range is invalid (start must be before end).");

            var dir = Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;
            var baseName = Path.GetFileNameWithoutExtension(sourcePath);
            var destPath = ImageConverter.GetUniqueOutputPath(dir, baseName + "_cropped", ".mp4");
            Log($"export dest={destPath}");

            status?.Invoke("Loading video…");
            MFCreateAttributes(out var readerAttrs, 2);
            AttrSetU32(readerAttrs, s_readerAdvancedVideoProcessing, 1);
            HRESULT hr = MFCreateSourceReaderFromURL(sourcePath, readerAttrs, out IMFSourceReader reader);
            Marshal.FinalReleaseComObject(readerAttrs);
            if (hr.Failed) throw new InvalidOperationException($"Cannot open this video (0x{hr.Value:X8}).");

            try
            {
                var (vIdx, aIdx) = FindStreams(reader);
                if (vIdx < 0) throw new InvalidOperationException("No video stream found in this file.");

                // Resolve the real video length on a separate reader so the
                // export reader stays at the beginning. This is also needed
                // for truthful progress when exporting without a trim range.
                long vidTicks = 0;
                status?.Invoke("Checking video duration…");
                try
                {
                    MFCreateAttributes(out var durAttrs, 0);
                    HRESULT dhr = MFCreateSourceReaderFromURL(sourcePath, durAttrs, out var durReader);
                    Marshal.FinalReleaseComObject(durAttrs);
                    if (dhr.Succeeded)
                    {
                        try
                        {
                            var (dv, _) = FindStreams(durReader);
                            if (dv >= 0) vidTicks = GetVideoDurationTicks(durReader, dv);
                            else Log("duration: no video stream found");
                        }
                        finally { Marshal.FinalReleaseComObject(durReader); }
                    }
                    else Log($"duration: cannot open a second reader (0x{dhr.Value:X8})");
                }
                catch (Exception ex)
                {
                    Log($"duration resolution failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                }

                if (trimming)
                {
                    var reqStart = TimeSpan.FromTicks(startTicks);
                    var reqEnd = TimeSpan.FromTicks(endTicks);
                    if (vidTicks > 0)
                    {
                        var realDur = TimeSpan.FromTicks(vidTicks);
                        Log($"crop trim window: video={realDur.TotalSeconds:0.###}s requested={reqStart.TotalSeconds:0.###}s->{reqEnd.TotalSeconds:0.###}s");
                        if (startTicks >= vidTicks)
                            throw new InvalidOperationException(
                                $"The selected range starts at {reqStart.TotalSeconds:0.###} s but the video ends at {realDur.TotalSeconds:0.###} s. Pick a range inside the video.");
                        if (endTicks > vidTicks) endTicks = vidTicks;
                        if (endTicks - startTicks < 2_500_000)
                            throw new InvalidOperationException(
                                $"The selected range ({reqStart.TotalSeconds:0.###} s -> {reqEnd.TotalSeconds:0.###} s) is empty or shorter than 0.25 s.");
                    }
                    else
                        Log($"crop trim window: video length unknown, using requested {reqStart.TotalSeconds:0.###}s->{reqEnd.TotalSeconds:0.###}s");
                }

                // Touching the stream first is required: the Source Reader
                // rejects a partial output type until the default current
                // type has been read once (probe-verified behaviour).
                try { reader.GetCurrentMediaType((uint)vIdx, out var touch); Marshal.FinalReleaseComObject(touch); }
                catch (COMException ce) { Log($"video touch failed 0x{ce.HResult:X8}"); }

                var want = NewVideoType();
                try { reader.SetCurrentMediaType((uint)vIdx, null, want); Log("video SetCurrentMediaType ok"); }
                catch (COMException ce) { Log($"video SetCurrentMediaType failed 0x{ce.HResult:X8}"); throw; }
                Marshal.FinalReleaseComObject(want);

                reader.GetCurrentMediaType((uint)vIdx, out var vCur);
                try
                {
                    var (srcW, srcH) = GetFrameSize(vCur);
                    var (fpsNum, fpsDen) = GetFrameRate(vCur);
                    var srcPitch = GetStride(vCur, srcW);
                    Log($"Source: {srcW}x{srcH} fps={fpsNum}/{fpsDen} pitch={srcPitch}");

                    var cx = Math.Clamp((int)(srcW * xF) & ~1, 0, (int)srcW - 4);
                    var cy = Math.Clamp((int)(srcH * yF) & ~1, 0, (int)srcH - 4);
                    var cw = Math.Min(Math.Max((int)(srcW * wF) & ~3, 4), (int)srcW - cx);
                    var ch = Math.Min(Math.Max((int)(srcH * hF) & ~3, 4), (int)srcH - cy);
                    Log($"crop-round-v3: crop=({cx},{cy})+{cw}x{ch}");

                    var bitrate = Math.Clamp(
                        (uint)(cw * ch * ((double)fpsNum / fpsDen) * 0.16),
                        1_200_000u, 16_000_000u);

                    try
                    {
                        Encode(reader, destPath, srcPitch, srcH,
                            cx, cy, cw, ch, fpsNum, fpsDen, bitrate,
                            vIdx, aIdx, startTicks, endTicks, vidTicks, trimming, progress, status, edit);
                    }
                    catch
                    {
                        // Never leave a broken/partial MP4 behind; the source
                        // file itself is only ever read.
                        try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                        throw;
                    }
                }
                finally { Marshal.FinalReleaseComObject(vCur); }
            }
            finally { Marshal.FinalReleaseComObject(reader); }

            if (!File.Exists(destPath))
            {
                var found = string.Join(", ",
                    Directory.GetFiles(dir, baseName + "*.mp4").Select(Path.GetFileName));
                Log($"no output file! dest={destPath} dirFiles=[{found}]");
                throw new InvalidOperationException("Encoding produced no output file.");
            }
            return destPath;
        }
        finally { ReleaseMf(); }
    }

    private static unsafe void Encode(
        IMFSourceReader reader, string destPath,
        long srcPitch, long srcHeight,
        int cx, int cy, int cw, int ch,
        uint fpsNum, uint fpsDen, uint bitrate,
        int vIdx, int aIdx,
        long startTicks, long endTicks, long videoDurationTicks, bool trimming,
        IProgress<double>? progress, Action<string>? status, EditSpec? edit)
    {
        Log($"Encode: pitch={srcPitch} height={srcHeight} crop=({cx},{cy})+{cw}x{ch} fps={fpsNum}/{fpsDen} bitrate={bitrate} vIdx={vIdx} aIdx={aIdx} trimStart={startTicks} trimEnd={(endTicks == long.MaxValue ? "MAX" : endTicks.ToString())} trimming={trimming}");

        // Pre-decide the audio plan BEFORE creating the writer: a failed
        // audio attach after AddStream leaves a dangling stream whose
        // Finalize always fails, so on failure we recreate without audio.
        uint aS = 0xFFFFFFFFu;
        bool haveAudio = false;
        IMFMediaType? aPcmIn = null;
        var srcAudioRate = 0;
        var srcAudioChannels = 0;
        if (aIdx >= 0)
        {
            try
            {
                try { reader.GetCurrentMediaType((uint)aIdx, out var touch); Marshal.FinalReleaseComObject(touch); }
                catch (COMException ce) { Log($"audio touch failed 0x{ce.HResult:X8}"); }

                MFCreateMediaType(out var wantPcm);
                AttrSetGuid(wantPcm, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
                AttrSetGuid(wantPcm, PInvoke.MF_MT_SUBTYPE, s_pcm);
                AttrSetU32(wantPcm, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                try
                {
                    reader.SetCurrentMediaType((uint)aIdx, null, wantPcm);
                    Marshal.FinalReleaseComObject(wantPcm);

                    reader.GetCurrentMediaType((uint)aIdx, out aPcmIn!);
                    int rate = (int)AttrGetU32Opt(aPcmIn!, MF_MT_AUDIO_SAMPLES_PER_SECOND);
                    int channels = (int)AttrGetU32Opt(aPcmIn!, MF_MT_AUDIO_NUM_CHANNELS);
                    int bits = (int)AttrGetU32Opt(aPcmIn!, MF_MT_AUDIO_BITS_PER_SAMPLE);
                    Log($"audio pcm rate={rate} ch={channels} bits={bits}");
                    if (!(rate > 0 && channels > 0 && bits == 16))
                    {
                        Marshal.FinalReleaseComObject(aPcmIn!);
                        aPcmIn = null;
                    }
                    else
                    {
                        srcAudioRate = rate;
                        srcAudioChannels = channels;
                    }
                }
                catch (COMException ce)
                {
                    Log($"audio PCM decode failed 0x{ce.HResult:X8}");
                    if (aPcmIn != null) { Marshal.FinalReleaseComObject(aPcmIn!); aPcmIn = null; }
                }
            }
            catch (COMException ce) { Log("audio setup failed 0x" + ce.HResult.ToString("X8")); }
        }

        // Audio plan: without added clips and at volume 1 the source PCM is
        // copied through untouched; otherwise everything is mixed to stereo.
        var wantsClips = edit?.HasAddedAudio == true;
        var outRate = srcAudioRate > 0 ? srcAudioRate : 44100;
        var outChannels = srcAudioChannels > 0 ? Math.Min(srcAudioChannels, 2) : 2;
        if (wantsClips) outChannels = 2;
        var mixedClips = wantsClips
            ? PrepareAudioClips(edit!, startTicks, outRate, status)
            : new List<MixedClip>();
        var hasBaseAudio = aPcmIn != null;
        var wantsAudioStream = hasBaseAudio || mixedClips.Count > 0;
        var baseVolume = Math.Clamp(edit?.VideoVolume ?? 1.0, 0, 2);
        var simpleAudio = hasBaseAudio && mixedClips.Count == 0
            && Math.Abs(baseVolume - 1) < 0.001
            && srcAudioChannels is >= 1 and <= 2;
        Log($"audio plan: rate={outRate} ch={outChannels} clips={mixedClips.Count} base={hasBaseAudio} simple={simpleAudio} vol={baseVolume:0.##}");

        uint vS = 0xFFFFFFFFu;
        IMFSinkWriter? writer = null;
        for (var attempt = 0; attempt < 2 && writer == null; attempt++)
        {
            HRESULT hr = MFCreateSinkWriterFromURL(destPath, null, null, out var w2);
            if (hr.Failed) throw new InvalidOperationException($"Cannot create the MP4 file (0x{hr.Value:X8}).");
            var ok = false;
            try
            {
                w2.AddStream(WireVideoOut(cw, ch, fpsNum, fpsDen, bitrate), out vS);
                w2.SetInputMediaType(vS, WireVideoIn(cw, ch, fpsNum, fpsDen), null);
                ok = true; // a video-only writer is always usable
                Log($"Encode: video streams ok (attempt {attempt})");

                if (wantsAudioStream && attempt == 0)
                {
                    long rate = outRate;
                    int channels = outChannels;
                    MFCreateMediaType(out var aOut);
                    AttrSetGuid(aOut, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
                    AttrSetGuid(aOut, PInvoke.MF_MT_SUBTYPE, s_aac);
                    // Keep this type minimal: adding BITS_PER_SAMPLE, a byte
                    // rate or an explicit payload type makes the sink writer
                    // reject the PCM input type (0xC00D36B4) on this build.
                    AttrSetU32(aOut, MF_MT_AVG_BITRATE, 160_000);
                    AttrSetU32(aOut, MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)rate);
                    AttrSetU32(aOut, MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
                    try
                    {
                        w2.AddStream(aOut, out aS);
                        try
                        {
                            MFCreateMediaType(out var aIn);
                            AttrSetGuid(aIn, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
                            AttrSetGuid(aIn, PInvoke.MF_MT_SUBTYPE, s_pcm);
                            AttrSetU32(aIn, MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)rate);
                            AttrSetU32(aIn, MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
                            AttrSetU32(aIn, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                            AttrSetU32(aIn, MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)(channels * 2));
                            AttrSetU32(aIn, s_mtAudioAvgBytes, (uint)(rate * channels * 2));
                            try
                            {
                                w2.SetInputMediaType(aS, aIn, null);
                                haveAudio = true;
                                Log("Encode: audio stream ok");
                            }
                            finally { Marshal.FinalReleaseComObject(aIn); }
                        }
                        catch (COMException ce)
                        {
                            Log($"audio SetInputMediaType failed 0x{ce.HResult:X8} (will retry video-only)");
                            aS = 0xFFFFFFFFu;
                            ok = false;
                        }
                    }
                    catch (COMException ce)
                    {
                        aS = 0xFFFFFFFFu;
                        ok = false;
                        Log($"audio AddStream failed 0x{ce.HResult:X8} (will retry video-only)");
                    }
                    finally { Marshal.FinalReleaseComObject(aOut); }
                }
            }
            finally
            {
                if (ok)
                {
                    writer = w2;
                }
                else
                {
                    Marshal.FinalReleaseComObject(w2);
                    aS = 0xFFFFFFFFu;
                    haveAudio = false;
                    if (aPcmIn != null)
                    {
                        Log("recreating writer video-only");
                        Marshal.FinalReleaseComObject(aPcmIn!);
                        aPcmIn = null;
                    }
                }
            }
        }
        if (aPcmIn != null) { Marshal.FinalReleaseComObject(aPcmIn!); aPcmIn = null; }
        if (writer == null) throw new InvalidOperationException("The sink writer could not be prepared.");

        try
        {
            writer.BeginWriting();
            status?.Invoke("Encoding video…");
            var spanTicks = trimming
                ? endTicks - startTicks
                : Math.Max(0, videoDurationTicks - startTicks);
            var videoProgressEnd = haveAudio ? 0.80 : 0.96;
            var videoEndTicks = startTicks;

            var frameLog = 0;
            var readLog = 0;
            while (true)
            {
                long vTime; uint vFlags;
                IMFSample sample;
                try
                {
                    Windows.Win32.Media_MediaFoundation_IMFSourceReader_Extensions.ReadSample(
                        reader, (uint)vIdx, 0, out var actIdx, out vFlags, out vTime, out sample);
                }
                catch (COMException ce) { Log("THROW ReadSample(video): 0x" + ce.HResult.ToString("X8")); throw; }
                if (readLog < 3 || (readLog % 128) == 0) Log($"video read: time={vTime} flags=0x{vFlags:X} sampleNull={sample == null} wrote={frameLog}");
                readLog++;
                if ((vFlags & ReaderFlagEndOfStream) != 0) break;
                if (sample == null) continue;
                try
                {
                    var time = vTime;
                    if (time < startTicks) continue;
                    if (endTicks != long.MaxValue && time >= endTicks) { Log($"skip late time={time} >= end={endTicks}"); break; }

                    var segSw = frameLog < 4 ? System.Diagnostics.Stopwatch.StartNew() : null;
                    IMFMediaBuffer buf;
                    try { sample.ConvertToContiguousBuffer(out buf); }
                    catch (COMException ce) { Log("THROW ConvertToContiguousBuffer: 0x" + ce.HResult.ToString("X8")); throw; }
                    sample.GetSampleDuration(out var sDur);
                    var dur = sDur > 0 ? sDur : 10000000 * (long)fpsDen / fpsNum;
                    try
                    {
                        byte* pSrc; uint cbMax; uint cbCur;
                        buf.Lock(&pSrc, &cbMax, &cbCur);
                        segSw?.Stop();
                        try
                        {
                            var layout = GetSourceLayout(srcPitch, srcHeight, cbCur);
                            var lockMs = segSw?.Elapsed.TotalMilliseconds ?? 0;
                            segSw?.Restart();
                            if (frameLog == 0 && layout.Pitch != srcPitch)
                                Log($"source layout: pitch={layout.Pitch} uvOffset={layout.UvOffset}");
                            WriteCroppedFrame(writer, vS, pSrc, cbCur,
                                layout.Pitch, layout.UvOffset,
                                cx, cy, cw, ch,
                                time - startTicks, dur, edit, ch >= 576, time);
                            segSw?.Stop();
                            if (segSw is not null)
                                Log($"seg frame#2: lock={lockMs:0.##}ms write={segSw.Elapsed.TotalMilliseconds:0.##}ms");
                        }
                        catch (COMException ce) { Log("THROW WriteSample(video): 0x" + ce.HResult.ToString("X8")); throw; }
                        finally { buf.Unlock(); }
                    }
                    finally
                    {
                        try { Marshal.FinalReleaseComObject(buf); } catch { }
                    }
                if (frameLog < 6 || (frameLog % 64) == 0) Log($"frame#{frameLog} ok time={time} dur={dur}");
                frameLog++;
                videoEndTicks = Math.Max(videoEndTicks, time + dur);
                if (spanTicks > 0)
                    progress?.Report(Math.Clamp((double)(time - startTicks) / spanTicks, 0, 1) * videoProgressEnd);
                }
                finally
                {
                    try { Marshal.FinalReleaseComObject(sample); } catch { }
                }
            }

            if (haveAudio)
            {
                var audioSpanTicks = trimming
                    ? endTicks - startTicks
                    : Math.Max(0, videoEndTicks - startTicks);

                if (simpleAudio)
                {
                    status?.Invoke("Encoding audio…");
                    var audioProgressEnd = 0.96;
                    var lastAudioPercent = -1;
                    var audioProgressSpan = Math.Max(audioSpanTicks, 1);
                    while (true)
                    {
                        long aTime; uint aFlags;
                        IMFSample sample;
                        try
                        {
                            Windows.Win32.Media_MediaFoundation_IMFSourceReader_Extensions.ReadSample(
                                reader, (uint)aIdx, 0, out var actIdx, out aFlags, out aTime, out sample);
                        }
                        catch (COMException ce) { Log("THROW ReadSample(audio): 0x" + ce.HResult.ToString("X8")); throw; }
                        if ((aFlags & ReaderFlagEndOfStream) != 0) break;
                        if (sample == null) continue;
                        var time = aTime;
                        try
                        {
                            if (time < startTicks) continue;
                            if (endTicks != long.MaxValue && time >= endTicks) break;

                            sample.ConvertToContiguousBuffer(out var buf);
                            try
                            {
                                byte* pSrc; uint cbMax; uint cbCur;
                                buf.Lock(&pSrc, &cbMax, &cbCur);
                                sample.GetSampleDuration(out var aDur);
                                try
                                {
                                    CopyAudioSample(writer, aS, pSrc, cbCur, time - startTicks, aDur > 0 ? aDur : 1);
                                }
                                finally { buf.Unlock(); }
                            }
                            finally
                            {
                                try { Marshal.FinalReleaseComObject(buf); } catch { }
                            }
                        }
                        finally
                        {
                            try { Marshal.FinalReleaseComObject(sample); } catch { }
                        }
                        var audioFraction = Math.Clamp((double)(time - startTicks) / audioProgressSpan, 0, 1);
                        var audioPercent = (int)(audioFraction * 100);
                        if (audioPercent > lastAudioPercent || audioFraction >= 1)
                        {
                            lastAudioPercent = audioPercent;
                            progress?.Report(videoProgressEnd +
                                (audioProgressEnd - videoProgressEnd) * audioFraction);
                        }
                    }
                    progress?.Report(audioProgressEnd);
                }
                else
                {
                    WriteMixedAudio(reader, aIdx, writer, aS, outRate, srcAudioChannels,
                        startTicks, audioSpanTicks, hasBaseAudio, baseVolume, mixedClips, status,
                        fraction => progress?.Report(videoProgressEnd +
                            (0.96 - videoProgressEnd) * Math.Clamp(fraction, 0, 1)));
                }
            }

            status?.Invoke("Finalizing MP4…");
            progress?.Report(0.98);
            if (!haveAudio && aIdx >= 0)
                Log("export continues video-only: this Windows build has no AAC encoder MFT");
            Log($"video loop done wrote={frameLog} haveAudio={haveAudio}");
            if (frameLog == 0)
            {
                var from = (double)startTicks / 10_000_000;
                var to = endTicks == long.MaxValue ? "end" : ((double)endTicks / 10_000_000).ToString("0.###");
                throw new InvalidOperationException(
                    $"No video frames were written for the selected range ({from:0.###} s -> {to} s). The range is empty or outside the video — nothing was saved.");
            }
            if (spanTicks <= 0) progress?.Report(videoProgressEnd);
            // Do not call Flush(vS) here: the Sink Writer's H.264 encoder
            // buffers ~16 frames, and an explicit Flush drops that pending
            // tail (verified: 16 frames and any clip shorter than the
            // lookahead were lost). Finalize drains the encoder properly.
            Log("finalizing…");
            writer.Finalize();
            Log("finalize ok");
            progress?.Report(1.0);
        }
        finally
        {
            Marshal.FinalReleaseComObject(writer!);
        }
    }

    private static IMFMediaType WireVideoOut(int cw, int ch, uint fpsNum, uint fpsDen, uint bitrate)
    {
        MFCreateMediaType(out var vOut);
        AttrSetGuid(vOut, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        AttrSetGuid(vOut, PInvoke.MF_MT_SUBTYPE, s_h264);
        AttrSetU32(vOut, MF_MT_AVG_BITRATE, bitrate);
        AttrSetU32(vOut, PInvoke.MF_MT_INTERLACE_MODE, 2); // progressive
        AttrSetU64EntryPoint(vOut, MF_MT_FRAME_SIZE, ((ulong)cw << 32) | (uint)ch);
        AttrSetU64EntryPoint(vOut, MF_MT_FRAME_RATE, ((ulong)fpsNum << 32) | fpsDen);
        return vOut;
    }

    private static IMFMediaType WireVideoIn(int cw, int ch, uint fpsNum, uint fpsDen)
    {
        MFCreateMediaType(out var vIn);
        AttrSetGuid(vIn, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        AttrSetGuid(vIn, PInvoke.MF_MT_SUBTYPE, s_nv12);
        AttrSetU64EntryPoint(vIn, MF_MT_FRAME_SIZE, ((ulong)cw << 32) | (uint)ch);
        AttrSetU64EntryPoint(vIn, MF_MT_FRAME_RATE, ((ulong)fpsNum << 32) | fpsDen);
        AttrSetU32(vIn, MF_MT_DEFAULT_STRIDE, (uint)cw);
        return vIn;
    }

    private static unsafe void CopyAudioSample(
        IMFSinkWriter writer, uint aS, byte* pSrc, uint cbSrc, long time, long dur)
    {
        MFCreateMemoryBuffer(cbSrc, out var dst);
        try
        {
            byte* pDst;
            dst.Lock(&pDst, null, null);
            try { Buffer.MemoryCopy(pSrc, pDst, cbSrc, cbSrc); }
            finally { dst.Unlock(); }
            dst.SetCurrentLength(cbSrc);
            MFCreateSample(out var s);
            try
            {
                s.AddBuffer(dst);
                s.SetSampleTime(time);
                s.SetSampleDuration(dur);
                writer.WriteSample(aS, s);
            }
            finally { Marshal.FinalReleaseComObject(s); }
        }
        finally { Marshal.FinalReleaseComObject(dst); }
    }

    private static int s_frameCountLocal; // per-run overlay timing log counter

    private static unsafe void WriteCroppedFrame(
        IMFSinkWriter writer, uint vS, byte* pSrc, uint cbSrc,
        long srcPitch, long srcUvOffset,
        int cx, int cy, int cw, int ch, long time, long dur,
        EditSpec? edit, bool bt709, long absTicks)
    {
        if (absTicks == 0) s_frameCountLocal = 0;
        MFCreateMemoryBuffer((uint)(cw * ch * 3 / 2), out var dst);
        try
        {
            byte* pDst;
            dst.Lock(&pDst, null, null);
            try
            {
                // Y plane: copy cw x ch rows.
                for (var y = 0; y < ch; y++)
                {
                    var s = pSrc + (cy + y) * srcPitch + cx;
                    var d = pDst + (long)y * cw;
                    Buffer.MemoryCopy(s, d, (uint)cw, (uint)cw);
                }
                // UV plane: NV12 interleaved chroma is one byte per pixel, so a
                // row is cw bytes wide and the x offset is cx (not cx/2).
                var uvSrc = pSrc + srcUvOffset;
                var uvDst = pDst + (long)(cw * ch);
                for (var v = 0; v < ch / 2; v++)
                {
                    var s = uvSrc + (cy / 2 + v) * srcPitch + cx;
                    var d = uvDst + (long)v * cw;
                    Buffer.MemoryCopy(s, d, (uint)cw, (uint)cw);
                }

                if (edit is not null && edit.HasOverlays)
                {
                    var ovSw = System.Diagnostics.Stopwatch.StartNew();
                    OverlayCompositor.Render(pDst, cw, ch, absTicks, edit, bt709);
                    if (s_frameCountLocal < 4) Log($"seg overlay {ovSw.Elapsed.TotalMilliseconds:0.##}ms");
                    s_frameCountLocal++;
                }
            }
            finally { dst.Unlock(); }
            dst.SetCurrentLength((uint)(cw * ch * 3 / 2));
            MFCreateSample(out var s2);
            try
            {
                s2.AddBuffer(dst);
                s2.SetSampleTime(time);
                s2.SetSampleDuration(dur);
                writer.WriteSample(vS, s2);
            }
            finally { Marshal.FinalReleaseComObject(s2); }
        }
        finally { Marshal.FinalReleaseComObject(dst); }
    }

    // ------------------------------------------------------------------
    // Interop helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Real video end in 100 ns units. Tries MF_PD_DURATION and, when it is
    /// absent, demuxes the compressed video samples without decoding them.
    /// </summary>
    private static long GetVideoDurationTicks(IMFSourceReader reader, int vIdx)
    {
        try
        {
            var durGuid = s_mfPdDuration;
            reader.GetPresentationAttribute((uint)vIdx, &durGuid, out var pv);
            var ticks = (long)pv.uhVal;
            if (ticks > 0)
            {
                Log($"duration: MF_PD_DURATION={ticks}");
                return ticks;
            }
        }
        catch (COMException) { /* attribute missing - measure below */ }

        try
        {
            reader.GetNativeMediaType((uint)vIdx, 0, out var native);
            try { reader.SetCurrentMediaType((uint)vIdx, null, native); }
            finally { Marshal.FinalReleaseComObject(native); }
        }
        catch (Exception ex)
        {
            Log($"duration: cannot select compressed type ({ex.GetType().Name} 0x{ex.HResult:X8})");
            return 0;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long last = -1, frameDelta = 0, lastDur = 0;
        try
        {
            while (true)
            {
                Windows.Win32.Media_MediaFoundation_IMFSourceReader_Extensions.ReadSample(
                    reader, (uint)vIdx, 0, out _, out var flags, out var time, out var sample);
                if ((flags & ReaderFlagEndOfStream) != 0) break;
                if (sample == null) continue;
                try
                {
                    if (time > last)
                    {
                        if (last >= 0) frameDelta = time - last;
                        last = time;
                    }
                    sample.GetSampleDuration(out var sd);
                    if (sd > 0) lastDur = sd;
                }
                finally
                {
                    try { Marshal.FinalReleaseComObject(sample); } catch { }
                }
            }
        }
        catch (COMException ce)
        {
            Log($"duration: sample scan failed 0x{ce.HResult:X8}");
        }
        if (last < 0) return 0;
        var end = last + Math.Max(lastDur, frameDelta);
        Log($"duration: scanned last={last} tail={Math.Max(lastDur, frameDelta)} end={end} ({sw.ElapsedMilliseconds} ms)");
        return end;
    }

    private static void AttrSetU64EntryPoint(IMFAttributes t, Guid k, ulong packed) =>
        Media_MediaFoundation_IMFAttributes_Extensions.SetUINT64(t, k, packed);

    private static void AcquireMf()
    {
        DisablePowerThrottling();
        lock (s_mfLock)
        {
            if (s_mfRefs == 0)
            {
                var hr = MFStartup(MfVersion, MfStartupNoSocket);
                if (hr.Failed)
                    throw new InvalidOperationException($"Media Foundation failed to start (0x{hr.Value:X8}).");
            }
            s_mfRefs++;
        }
    }

    // Export speed: Windows silently duty-cycles background processes
    // (EcoQoS) to ~1 CPU slice per second — an encode that takes seconds
    // in the foreground took ~4 minutes once the app was not focused.
    // Opt the process out: the user is actively waiting on this job.
    private static int s_throttleDone; // 0 = not applied, 1 = applied

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint ControlMask;
        public uint StateMask;
        public uint Reserved1; // this build returns BAD_LENGTH for size 8
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess, int ProcessInformationClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static void DisablePowerThrottling()
    {
        if (Interlocked.Exchange(ref s_throttleDone, 1) == 1) return;
        try
        {
            // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling = 4
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                ControlMask = 1 /* PROCESS_POWER_THROTTLING_EXECUTION_SPEED */,
                StateMask = 0 /* StateMask=0 disables the throttling */,
            };
            var ok = SetProcessInformation(GetCurrentProcess(), 4, ref state, 12);
            Log(ok ? "EcoQoS: process opted out of execution-speed throttling"
                   : $"EcoQoS opt-out failed err={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Log("EcoQoS opt-out threw: " + ex.GetType().Name);
        }
    }

    private static void ReleaseMf()
    {
        lock (s_mfLock)
        {
            s_mfRefs--;
            if (s_mfRefs == 0) MFShutdown();
        }
    }

    private static (int v, int a) FindStreams(IMFSourceReader reader)
    {
        int video = -1, audio = -1;
        for (uint i = 0; i < 32; i++)
        {
            try
            {
                reader.GetNativeMediaType(i, 0, out var t);
                Windows.Win32.Media_MediaFoundation_IMFMediaType_Extensions.GetMajorType(t, out var major);
                if (major == PInvoke.MFMediaType_Video && video < 0) video = (int)i;
                if (major == PInvoke.MFMediaType_Audio && audio < 0) audio = (int)i;
                Marshal.FinalReleaseComObject(t);
                if (video >= 0 && audio >= 0) break;
            }
            catch (COMException)
            {
                break;
            }
        }
        return (video, audio);
    }

    private static (uint width, uint height) GetFrameSize(IMFMediaType type)
    {
        var packed = AttrGetU64Opt(type, MF_MT_FRAME_SIZE);
        return ((uint)(packed >> 32), (uint)(packed & 0xFFFFFFFFu));
    }

    private static (uint num, uint den) GetFrameRate(IMFMediaType type)
    {
        var packed = AttrGetU64Opt(type, MF_MT_FRAME_RATE);
        if ((packed >> 32) != 0)
            return ((uint)(packed >> 32), Math.Max((uint)(packed & 0xFFFFFFFFu), 1u));
        return (30, 1);
    }

    // Decoders may report a stride that differs from the real buffer pitch and
    // may pad the frame height (e.g. 612 -> 624, 248x180 -> pitch 256x192).
    // Derive the real pitch and chroma offset from the buffer length.
    private static (long Pitch, long UvOffset) GetSourceLayout(long stride, long height, uint bufferLength)
    {
        var nominal = stride * height;
        if ((long)bufferLength * 2 == nominal * 3) return (stride, nominal);

        var heights = new List<long> { height };
        for (long a = 2; a <= 128; a <<= 1)
        {
            var padded = (height + a - 1) / a * a;
            if (padded != height && (padded & 1) == 0 && !heights.Contains(padded)) heights.Add(padded);
        }

        (long Pitch, long UvOffset)? fallback = null;
        foreach (var h in heights)
        {
            var denom = 3L * h;
            if (((long)bufferLength * 2) % denom != 0) continue;
            var pitch = (long)bufferLength * 2 / denom;
            if (pitch == stride) return (stride, stride * h);
            if ((pitch & 1) == 0 && pitch > stride && pitch <= stride + 256)
                fallback ??= (pitch, pitch * h);
        }
        return fallback ?? (stride, nominal);
    }
    private static long GetStride(IMFMediaType type, uint width)
    {
        var stride = AttrGetU32Opt(type, MF_MT_DEFAULT_STRIDE);
        return stride >= width ? stride : width;
    }

    private static IMFMediaType NewVideoType()
    {
        MFCreateMediaType(out var t);
        AttrSetGuid(t, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        AttrSetGuid(t, PInvoke.MF_MT_SUBTYPE, s_nv12);
        return t;
    }
}
