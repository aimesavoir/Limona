using System.Drawing;
using System.Text;

namespace Limona.Services;

/// <summary>
/// Resolves color emoji bitmaps. System.Drawing cannot render the color emoji
/// font (Segoe UI Emoji is drawn monochrome by GDI+), so emoji are sourced as
/// PNGs: bundled Twemoji assets first, then a per-emoji download cache for
/// emoji typed by the user that are not in the bundled set.
/// </summary>
public static class EmojiImages
{
    private static readonly string BundledDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Emoji");

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Limona", "EmojiCache");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private const string CdnBase = "https://cdn.jsdelivr.net/gh/twitter/twemoji@14.0.2/assets/72x72/";

    /// <summary>
    /// Canonical file key: lowercase hex code points joined by '-', with
    /// variation selectors (U+FE0F) stripped — matches the bundled names.
    /// </summary>
    public static string Key(string emoji)
    {
        var sb = new StringBuilder();
        foreach (var rune in emoji.EnumerateRunes())
        {
            if (rune.Value == 0xFE0F) continue;
            if (sb.Length > 0) sb.Append('-');
            sb.Append(rune.Value.ToString("x"));
        }
        return sb.ToString();
    }

    /// <summary>Existing PNG path for the emoji (bundled or cached), or null.</summary>
    public static string? Resolve(string emoji)
    {
        var key = Key(emoji);
        if (key.Length == 0) return null;

        var bundled = Path.Combine(BundledDir, key + ".png");
        if (File.Exists(bundled)) return bundled;

        var cached = Path.Combine(CacheDir, key + ".png");
        return File.Exists(cached) ? cached : null;
    }

    /// <summary>Loads the color emoji as a bitmap, or null when unavailable. Caller disposes.</summary>
    public static Image? LoadColor(string emoji)
    {
        var path = Resolve(emoji);
        if (path is null) return null;
        try
        {
            // Copy into memory so the file is never locked.
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            return Image.FromStream(stream);
        }
        catch (Exception ex)
        {
            Interop.Dbg.Log("EmojiImages.LoadColor THREW: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Downloads the emoji PNG into the cache when it is neither bundled nor
    /// cached. Returns the local path, or null when the download fails.
    /// </summary>
    public static async Task<string?> EnsureAsync(string emoji)
    {
        var existing = Resolve(emoji);
        if (existing is not null) return existing;

        var key = Key(emoji);
        if (key.Length == 0) return null;

        try { Directory.CreateDirectory(CacheDir); } catch { }

        var target = Path.Combine(CacheDir, key + ".png");
        foreach (var candidate in new[] { key, key + "-fe0f" })
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(CdnBase + candidate + ".png").ConfigureAwait(false);
                if (bytes.Length < 100) continue;
                await File.WriteAllBytesAsync(target, bytes).ConfigureAwait(false);
                Interop.Dbg.Log($"EmojiImages: cached '{key}'");
                return target;
            }
            catch
            {
                // try the next candidate name
            }
        }

        Interop.Dbg.Log($"EmojiImages: no color asset for '{key}'");
        return null;
    }
}
