using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Limona.Services;

/// <summary>
/// Persisted user preferences (loaded at startup, saved whenever the user
/// changes something in Settings). Stored as JSON under
/// %LOCALAPPDATA%\Limona\settings.json.
/// </summary>
public static class AppSettings
{
    private sealed class Model
    {
        public string MenuBackground { get; set; } = "#FF181A22";
        public string MenuAccent { get; set; } = "#FFF26F1D";
        public bool ShowWelcomeAtStartup { get; set; } = true;
        /// <summary>UI language code: en, fr, ar, es.</summary>
        public string Language { get; set; } = "en";
        /// <summary>0 = palette from before the orange re-brand.</summary>
        public int ColorsVersion { get; set; }
    }

    private const string DefaultMenuAccent = "#FFF26F1D";
    private const string PreviousDefaultMenuAccent = "#FFF5A623";
    private const int CurrentColorsVersion = 1;

    private static string? _menuBackground;
    private static string? _menuAccent;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Limona", "settings.json");

    public static string MenuBackground => _menuBackground ?? "#FF181A22";
    public static string MenuAccent => _menuAccent ?? DefaultMenuAccent;
    public static bool ShowWelcomeAtStartup { get; private set; } = true;

    /// <summary>Loads saved colors into MenuTheme (call once at startup).</summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                // No saved settings: still brand the WinUI controls with the
                // app accent (system brushes default to the Windows blue).
                Limona.Menu.MenuTheme.SetMenuBackground(MenuBackground);
                Limona.Menu.MenuTheme.SetMenuAccent(MenuAccent);
                return;
            }
            var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(FilePath));
            if (model is null) return;

            ShowWelcomeAtStartup = model.ShowWelcomeAtStartup;

            if (Loc.TryParse(model.Language, out var savedLanguage))
                Loc.SetLanguage(savedLanguage, notify: false);

            // One-time re-brand: older installs may still carry another accent
            // (e.g. the previous amber or a hand-picked color). The new orange
            // palette becomes the app-wide main color; later user picks stick.
            var needsPaletteMigration = model.ColorsVersion < CurrentColorsVersion;
            if (needsPaletteMigration)
            {
                model.MenuAccent = DefaultMenuAccent;
                model.ColorsVersion = CurrentColorsVersion;
            }

            if (IsValidHex(model.MenuBackground))
            {
                _menuBackground = model.MenuBackground;
                Limona.Menu.MenuTheme.SetMenuBackground(model.MenuBackground);
            }
            var menuAccent = model.MenuAccent.Equals(PreviousDefaultMenuAccent, StringComparison.OrdinalIgnoreCase)
                ? DefaultMenuAccent
                : model.MenuAccent;
            if (IsValidHex(menuAccent))
            {
                _menuAccent = menuAccent;
                Limona.Menu.MenuTheme.SetMenuAccent(menuAccent);
                if (needsPaletteMigration ||
                    !menuAccent.Equals(model.MenuAccent, StringComparison.OrdinalIgnoreCase))
                    Save();
            }
        }
        catch (Exception ex)
        {
            Interop.Dbg.Log("AppSettings.Load THREW: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Windows startup (obligatory — the app is useless as a one-off launch,
    // so every start (re)registers itself in the HKCU Run key. This also
    // self-heals when the exe moves to a new path.
    // ------------------------------------------------------------------

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Limona";

    private static string GetExePath() =>
        Path.Combine(AppContext.BaseDirectory, "Limona.exe");

    public static void EnsureWindowsStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                Interop.Dbg.Log("EnsureWindowsStartup: Run key not writable");
                return;
            }
            key.SetValue(RunValueName, $"\"{GetExePath()}\"");
            Interop.Dbg.Log("EnsureWindowsStartup: registered '" + GetExePath() + "'");
        }
        catch (Exception ex)
        {
            Interop.Dbg.Log("EnsureWindowsStartup THREW: " + ex.Message);
        }
    }

    public static void SetMenuBackground(string argbHex)
    {
        _menuBackground = argbHex;
        Limona.Menu.MenuTheme.SetMenuBackground(argbHex);
        Save();
    }

    public static void SetMenuAccent(string argbHex)
    {
        _menuAccent = argbHex;
        Limona.Menu.MenuTheme.SetMenuAccent(argbHex);
        Save();
    }

    public static void SetShowWelcomeAtStartup(bool show)
    {
        ShowWelcomeAtStartup = show;
        Save();
    }

    /// <summary>Changes the UI language immediately and persists it.</summary>
    public static void SetLanguage(AppLanguage language)
    {
        Loc.SetLanguage(language);
        Save();
    }

    public static void SetLanguageFromInstaller(AppLanguage language)
    {
        var model = File.Exists(FilePath)
            ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject
                ?? throw new InvalidDataException("The Limona settings file is not a JSON object.")
            : new JsonObject();
        model["Language"] = Loc.Code(language);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, model.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var model = new Model
            {
                MenuBackground = MenuBackground,
                MenuAccent = MenuAccent,
                ShowWelcomeAtStartup = ShowWelcomeAtStartup,
                Language = Loc.Code(Loc.Current),
                ColorsVersion = CurrentColorsVersion,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Interop.Dbg.Log("AppSettings.Save THREW: " + ex.Message);
        }
    }

    private static bool IsValidHex(string hex) =>
        hex.Length == 9 && hex[0] == '#' && HexDigits(hex[1..]);

    private static bool HexDigits(string s)
    {
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }
}
