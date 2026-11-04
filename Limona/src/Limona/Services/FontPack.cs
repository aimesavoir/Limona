using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Limona.Interop;

namespace Limona.Services;

/// <summary>
/// Ships the app's font pack inside <c>Assets\Fonts</c> and registers it for the
/// current Windows user on first launch (no admin rights required).
///
/// Per-user font install = copy the file to %LOCALAPPDATA%\Microsoft\Windows\Fonts
/// and add one value under HKCU\Software\Microsoft\Windows\CurrentVersion\Fonts.
/// Running this in Program.Main, before Application.Start, means DirectWrite sees
/// the families for the very first launch as well.
/// </summary>
public static class FontPack
{
    // Font file in Assets\Fonts → registry display name (one entry per file).
    private static readonly (string File, string Label)[] Bundled =
    {
        ("Cairo-Regular.ttf",        "Cairo (TrueType)"),
        ("Cairo-Bold.ttf",           "Cairo Bold (TrueType)"),
        ("Tajawal-Regular.ttf",      "Tajawal (TrueType)"),
        ("Tajawal-Bold.ttf",         "Tajawal Bold (TrueType)"),
        ("NotoSansArabic-Regular.ttf", "Noto Sans Arabic (TrueType)"),
        ("NotoSansArabic-Bold.ttf",  "Noto Sans Arabic Bold (TrueType)"),
        ("BebasNeue-Regular.ttf",    "Bebas Neue (TrueType)"),
        ("Almarai-Regular.ttf",      "Almarai (TrueType)"),
        ("Almarai-Bold.ttf",         "Almarai Bold (TrueType)"),
        ("ArefRuqaa-Regular.ttf",    "Aref Ruqaa (TrueType)"),
        ("ArefRuqaa-Bold.ttf",       "Aref Ruqaa Bold (TrueType)"),
        ("CascadiaCode.ttf",         "Cascadia Code (TrueType)"),
    };

    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Fonts";
    private const uint WmFontChange = 0x001D;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    /// <summary>
    /// Makes every bundled font available to this user. Safe to call on every
    /// launch: only missing files / registry values are touched.
    /// Returns the number of font files copied this run.
    /// </summary>
    public static int EnsureInstalled(bool force = false)
    {
        var sourceDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        if (!Directory.Exists(sourceDir)) return 0;

        var userFonts = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Fonts");
        Directory.CreateDirectory(userFonts);

        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        if (key is null) return 0;

        var copied = 0;
        var changed = false;

        foreach (var (file, label) in AllFonts(sourceDir))
        {
            var source = Path.Combine(sourceDir, file);
            if (!File.Exists(source)) continue;

            var target = Path.Combine(userFonts, file);
            // Never overwrite: an installed font file is memory-mapped by Windows
            // and the copy would fail (and there is no need to). If our file is
            // already there, just make sure it is registered.
            var needCopy = !File.Exists(target);

            if (needCopy)
            {
                try
                {
                    File.Copy(source, target, overwrite: false);
                    copied++;
                    changed = true;
                }
                catch (Exception ex)
                {
                    Dbg.Log("FontPack copy failed for " + file + ": " + ex.Message);
                    continue;
                }
            }

            var current = key.GetValue(label) as string;
            var needRegister = force
                || !string.Equals(current, target, StringComparison.OrdinalIgnoreCase);

            if (needRegister)
            {
                try
                {
                    key.SetValue(label, target, RegistryValueKind.String);
                    changed = true;
                }
                catch (Exception ex)
                {
                    Dbg.Log("FontPack registry failed for " + file + ": " + ex.Message);
                }
            }

            if (needCopy || needRegister)
                AddFontResourceEx(target, 0, IntPtr.Zero);
        }

        if (changed)
        {
            SendMessageTimeout(HwndBroadcast, WmFontChange, IntPtr.Zero, IntPtr.Zero,
                SmtoAbortIfHung, 1000, out _);
            Dbg.Log($"FontPack: {copied} font file(s) installed for the current user");
        }

        return copied;
    }

    /// <summary>Bundled entries plus any .ttf/.otf the user copied into
    /// Assets\Fonts (licensed fonts they own). Extra files get their display
    /// name from the font's own name table.</summary>
    private static System.Collections.Generic.IEnumerable<(string File, string Label)> AllFonts(string dir)
    {
        foreach (var entry in Bundled)
            yield return entry;

        string[] extras;
        try
        {
            extras = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly);
        }
        catch { yield break; }

        foreach (var path in extras)
        {
            var name = Path.GetFileName(path);
            var isFont = name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                      || name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);
            if (!isFont) continue;

            var known = false;
            foreach (var entry in Bundled)
                if (string.Equals(entry.File, name, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
            if (known) continue;

            yield return (name, LabelFor(path));
        }
    }

    private static string LabelFor(string path)
    {
        var suffix = path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ? " (OpenType)" : " (TrueType)";
        try
        {
            using var privateFonts = new System.Drawing.Text.PrivateFontCollection();
            privateFonts.AddFontFile(path);
            if (privateFonts.Families.Length > 0)
                return privateFonts.Families[0].Name + suffix;
        }
        catch (Exception ex)
        {
            Dbg.Log("FontPack could not read name of " + Path.GetFileName(path) + ": " + ex.Message);
        }
        return Path.GetFileNameWithoutExtension(path) + suffix;
    }

    /// <summary>True when the family is visible to GDI+ / the system.</summary>
    public static bool IsInstalled(string family)
    {
        try
        {
            using var collection = new System.Drawing.Text.InstalledFontCollection();
            foreach (var f in collection.Families)
                if (string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { }
        return false;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string fileName, uint flags, IntPtr reserved);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
