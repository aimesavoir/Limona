using System.Diagnostics;
using System.IO;
using Limona.Interop;

namespace Limona.Services;

/// <summary>
/// AI background removal support: the U2-NetP ONNX model is executed by the
/// bundled Python worker (rembg_worker.py) via OpenCV DNN, which produces a
/// full-resolution normalized grayscale mask. Composition (cutout threshold +
/// edge feathering) is done locally in C# so the preview window can restyle
/// the result instantly. 100% local: no cloud API.
/// </summary>
internal static class BackgroundRemover
{
    // Path to the bundled Python worker, shipped next to the executable.
    private static readonly string ScriptPath = Path.Combine(
        AppContext.BaseDirectory, "Services", "rembg_worker.py");

    // Model folder: %LOCALAPPDATA%\Limona\models\u2netp.onnx
    private static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Limona", "models", "u2netp.onnx");

    public static bool IsModelReady => File.Exists(ModelPath);

    /// <summary>
    /// Runs the Python worker on <paramref name="sourcePath"/> and returns the
    /// path of a temporary grayscale mask PNG (normalized 0..255, full resolution).
    /// </summary>
    public static async Task<string> GenerateMaskAsync(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source file not found", sourcePath);

        if (!IsModelReady)
            throw new InvalidOperationException(
                $"AI model not found: {ModelPath}\n" +
                "Please download u2netp.onnx from:\n" +
                "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx\n" +
                $"and place it in: {Path.GetDirectoryName(ModelPath)}");

        var maskPath = Path.Combine(Path.GetTempPath(), $"rm_bg_mask_{Guid.NewGuid():N}.png");
        var tmpOut   = Path.Combine(Path.GetTempPath(), $"rm_bg_out_{Guid.NewGuid():N}.png");

        var python = FindPython()
            ?? throw new InvalidOperationException("Python executable not found on PATH. Please install Python 3.9+.");

        var script = ScriptPath;
        if (!File.Exists(script))
        {
            // Fallback: script might be in source tree during development
            var devScript = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Services", "rembg_worker.py");
            if (File.Exists(devScript))
                script = Path.GetFullPath(devScript);
            else
                throw new FileNotFoundException("rembg_worker.py not found", script);
        }

        Dbg.Log($"BackgroundRemover: generating mask for {sourcePath}");

        var psi = new ProcessStartInfo
        {
            FileName  = python,
            Arguments = $"\"{script}\" \"{sourcePath}\" \"{tmpOut}\" \"{maskPath}\"",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi };
        var stderr = new System.Text.StringBuilder();

        proc.Start();
        proc.BeginOutputReadLine();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data);  };
        proc.BeginErrorReadLine();

        // Timeout: 90 seconds — first inference loads the model (~3 s), subsequent ones are fast
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90));
        await proc.WaitForExitAsync(cts.Token);

        try { if (File.Exists(tmpOut)) File.Delete(tmpOut); } catch { }

        if (proc.ExitCode != 0)
        {
            var msg = stderr.ToString().Trim();
            Dbg.Log($"BackgroundRemover mask generation failed: {msg}");
            throw new InvalidOperationException($"Background removal failed: {msg}");
        }

        if (!File.Exists(maskPath))
            throw new IOException("AI mask was not generated.");

        Dbg.Log($"BackgroundRemover: mask ready {maskPath} ({new FileInfo(maskPath).Length} bytes)");
        return maskPath;
    }

    /// <summary>
    /// Edge-feathering: two clamped sliding-window box-blur passes (horizontal +
    /// vertical), applied twice, approximate a soft Gaussian without Dijkstra complexity.
    /// </summary>
    public static void BoxBlurPass(byte[] data, int width, int height, int radius, CancellationToken ct = default)
    {
        if (radius <= 0) return;
        var span = 2 * radius + 1;
        var intermediate = new int[data.LongLength];

        // Horizontal
        for (var y = 0; y < height; y++)
        {
            if ((y & 31) == 0) ct.ThrowIfCancellationRequested();
            var row = y * width;
            var sum = 0;
            for (var k = -radius; k <= radius; k++) sum += data[row + Math.Clamp(k, 0, width - 1)];
            for (var x = 0; x < width; x++)
            {
                intermediate[row + x] = sum / span;
                sum += data[row + Math.Clamp(x + radius + 1, 0, width - 1)]
                     - data[row + Math.Clamp(x - radius, 0, width - 1)];
            }
        }

        // Vertical
        for (var x = 0; x < width; x++)
        {
            var sum = 0;
            for (var k = -radius; k <= radius; k++) sum += intermediate[Math.Clamp(k, 0, height - 1) * width + x];
            for (var y = 0; y < height; y++)
            {
                data[y * width + x] = (byte)(sum / span);
                sum += intermediate[Math.Clamp(y + radius + 1, 0, height - 1) * width + x]
                     - intermediate[Math.Clamp(y - radius, 0, height - 1) * width + x];
            }
        }
    }

    /// <summary>
    /// Shapes the raw AI mask before cutout: smoothing, contrast and edge shift
    /// (positive = expand silhouette, negative = shrink it). Non-destructive:
    /// works on a copy.
    /// </summary>
    public static byte[] ShapeMask(byte[] mask, uint width, uint height, int smoothness, int contrast, int shiftEdge, CancellationToken ct = default)
    {
        var w = (int)width;
        var h = (int)height;
        var shaped = (byte[])mask.Clone();

        // 1) Smoothing: removes AI noise from the outline before any shaping.
        var smoothR = Math.Clamp(smoothness / 10, 0, 24);
        if (smoothR >= 1)
        {
            BoxBlurPass(shaped, w, h, smoothR, ct);
            BoxBlurPass(shaped, w, h, smoothR, ct);
        }

        // 2) Contrast: steepens the ramp around the mid tone (crisper edges).
        if (contrast > 0)
        {
            var k = 1.0 + contrast / 12.5;
            for (var i = 0; i < shaped.Length; i++)
            {
                if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                var v = (int)Math.Round((shaped[i] - 128) * k + 128);
                shaped[i] = (byte)Math.Clamp(v, 0, 255);
            }
        }

        // 3) Edge shift: blur the silhouette then binarize at an offset midpoint —
        // a classic dilate/erode approximation that keeps contours smooth.
        if (shiftEdge != 0)
        {
            var blurred = (byte[])shaped.Clone();
            var shiftBlur = Math.Clamp((int)Math.Round(Math.Abs(shiftEdge) / 12.5), 1, 12);
            BoxBlurPass(blurred, w, h, shiftBlur, ct);
            BoxBlurPass(blurred, w, h, shiftBlur, ct);
            var mid = Math.Clamp(128 - shiftEdge * 2.2, 16, 240);
            for (var i = 0; i < shaped.Length; i++)
                shaped[i] = blurred[i] >= mid ? (byte)255 : (byte)0;
        }

        return shaped;
    }

    /// <summary>
    /// Computes the soft alpha profile of the AI subject edge: shapes the mask
    /// (smoothness / contrast / shift), binarizes it with the cutout threshold,
    /// then feathers only that edge. The result is an independent "base" layer;
    /// user erase strokes multiply into it AFTER this step, so feathering can
    /// never bleed back into erased areas.
    /// </summary>
    public static byte[] BlurBase(byte[] mask, uint width, uint height, int threshold, int featherRadius, int smoothness = 0, int contrast = 0, int shiftEdge = 0, CancellationToken ct = default)
    {
        var w = (int)width;
        var h = (int)height;

        var shaped = ShapeMask(mask, (uint)w, (uint)h, smoothness, contrast, shiftEdge, ct);
        var alpha = new byte[w * h];
        for (var i = 0; i < alpha.Length; i++)
            alpha[i] = shaped[i] < threshold ? (byte)0 : (byte)255;

        if (featherRadius > 0)
        {
            BoxBlurPass(alpha, w, h, featherRadius, ct);
            BoxBlurPass(alpha, w, h, featherRadius, ct);
        }
        return alpha;
    }

    /// <summary>
    /// Estimates the flat background color from a 1 px border frame (per-channel
    /// median). Fast histogram version — no allocations per sample.
    /// </summary>
    public static (byte R, byte G, byte B) EstimateBackgroundColor(byte[] bgra, uint width, uint height)
    {
        var w = (int)width;
        var h = (int)height;
        if (w <= 0 || h <= 0) return (255, 255, 255);

        var hr = new int[256];
        var hg = new int[256];
        var hb = new int[256];
        var total = 0;

        void Sample(int x, int y)
        {
            var i = (y * w + x) * 4;
            hb[bgra[i + 0]]++;
            hg[bgra[i + 1]]++;
            hr[bgra[i + 2]]++;
            total++;
        }

        for (var x = 0; x < w; x++) { Sample(x, 0); Sample(x, h - 1); }
        for (var y = 1; y < h - 1; y++) { Sample(0, y); Sample(w - 1, y); }

        static byte Median(int[] hist, int count)
        {
            var half = count / 2;
            var acc = 0;
            for (var v = 0; v < 256; v++)
            {
                acc += hist[v];
                if (acc > half) return (byte)v;
            }
            return 255;
        }

        return (Median(hr, total), Median(hg, total), Median(hb, total));
    }

    /// <summary>
    /// Keys out a flat background by flood-filling from the image borders.
    /// The key color is either explicit (user picked it) or estimated from the
    /// border frame. Every pixel connected to the borders whose color is within
    /// tolerance is removed; interior areas of the same color never reach the
    /// borders, so they are preserved (e.g. a white shirt on a white backdrop).
    /// Returns null when off.
    /// </summary>
    public static byte[]? DetectBackground(
        byte[] bgra, uint width, uint height, int tolerancePercent,
        (byte R, byte G, byte B)? keyColor = null, bool contiguous = true, CancellationToken ct = default)
    {
        if (tolerancePercent <= 0 || bgra.Length == 0) return null;
        var w = (int)width;
        var h = (int)height;
        var n = w * h;
        var tol = Math.Clamp(6 + tolerancePercent * 70 / 100, 6, 76);

        byte mr, mg, mb;
        if (keyColor is { } kc)
        {
            (mr, mg, mb) = (kc.R, kc.G, kc.B);
        }
        else
        {
            (mr, mg, mb) = EstimateBackgroundColor(bgra, width, height);
        }

        var key = new byte[n];
        var queue = new int[n];
        var head = 0;
        var tail = 0;

        bool IsBg(int idx)
        {
            var i4 = idx * 4;
            return Math.Abs(bgra[i4 + 2] - mr) <= tol &&
                   Math.Abs(bgra[i4 + 1] - mg) <= tol &&
                   Math.Abs(bgra[i4 + 0] - mb) <= tol;
        }

        // Global mode: every pixel of that color goes, enclosed or not.
        if (!contiguous)
        {
            for (var i = 0; i < n; i++)
            {
                if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                if (IsBg(i)) key[i] = 255;
            }
            BoxBlurPass(key, w, h, 1, ct);
            return key;
        }

        void TrySeed(int idx)
        {
            if (key[idx] == 0 && IsBg(idx))
            {
                key[idx] = 255;
                queue[tail++] = idx;
            }
        }

        for (var x = 0; x < w; x++) { TrySeed(x); TrySeed((h - 1) * w + x); }
        for (var y = 0; y < h; y++) { TrySeed(y * w); TrySeed(y * w + w - 1); }

        while (head < tail)
        {
            if ((head & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            var idx = queue[head++];
            var x = idx % w;
            var y = idx / w;
            if (x > 0) TrySeed(idx - 1);
            if (x < w - 1) TrySeed(idx + 1);
            if (y > 0) TrySeed(idx - w);
            if (y < h - 1) TrySeed(idx + w);
        }

        // 1 px softening so keyed holes don't have a razor-jagged rim.
        BoxBlurPass(key, w, h, 1, ct);
        return key;
    }

    /// <summary>
    /// Composites the displayed/saved alpha: base layer x manual erase weights
    /// x background-key, then snaps confident values. Writes the alpha channel
    /// into <paramref name="bgra"/> in place.
    /// Erase weights: 255 = keep, 0 = fully erase, mid = partial (soft stroke rim).
    /// bgKey: 255 = keyed-out background, 0 = untouched.
    /// </summary>
    public static void ApplyCompose(byte[] bgra, byte[] blurredBase, byte[]? eraseMask, uint width, uint height, byte[]? bgKey = null)
    {
        var n = (int)width * (int)height;
        for (var i = 0; i < n; i++)
        {
            var a = blurredBase[i];
            var e = eraseMask?[i] ?? (byte)255;
            if (e < 255) a = (byte)(a * e / 255);
            var bgr = bgKey?[i] ?? (byte)0;
            if (bgr > 0) a = (byte)(a * (255 - bgr) / 255);
            if (a < 6) a = 0;
            else if (a > 249) a = 255;
            bgra[i * 4 + 3] = a;
        }
    }

    /// <summary>
    /// One-shot compose (shape + blur base + erase multiply + background key).
    /// Used for saving full-quality results; the live preview composes the same
    /// math in two cached stages.
    /// </summary>
    public static byte[] Compose(byte[] bgra, byte[] mask, uint width, uint height, int threshold, int featherRadius, byte[]? eraseMask = null, int smoothness = 0, int contrast = 0, int shiftEdge = 0, byte[]? bgKey = null, CancellationToken ct = default)
    {
        var baseAlpha = BlurBase(mask, width, height, threshold, featherRadius, smoothness, contrast, shiftEdge, ct);
        ApplyCompose(bgra, baseAlpha, eraseMask, width, height, bgKey);
        return bgra;
    }

    private static string? FindPython()
    {
        // Check well-known locations first
        var candidates = new[]
        {
            @"C:\Users\MSI\AppData\Local\Programs\Python\Python314\python.exe",
            @"C:\Users\MSI\AppData\Local\Programs\Python\Python313\python.exe",
            @"C:\Users\MSI\AppData\Local\Programs\Python\Python312\python.exe",
            @"C:\Users\MSI\AppData\Local\Programs\Python\Python311\python.exe",
            @"C:\Users\MSI\AppData\Local\Programs\Python\Python310\python.exe",
            @"C:\Users\MSI\AppData\Local\Programs\Python\Python39\python.exe",
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // Fallback: search PATH
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), "python.exe");
                if (File.Exists(exe)) return exe;
            }
            catch { /* ignore bad PATH segments */ }
        }

        return null;
    }
}
