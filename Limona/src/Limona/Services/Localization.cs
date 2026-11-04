using System.Globalization;

namespace Limona.Services;

/// <summary>Languages Limona can display its interface in.</summary>
public enum AppLanguage
{
    English,
    Arabic,
    French,
    Spanish,
}

/// <summary>
/// Very small runtime localizer. English source text is used as the lookup
/// key, so any string that has not been translated yet simply stays English
/// instead of disappearing. Each entry holds the French, Arabic and Spanish
/// renderings of the same English text.
///
/// Usage in UI code: <c>Loc.T("Settings")</c> or <c>Loc.F("Saved: {0}", name)</c>.
/// </summary>
public static partial class Loc
{
    private static AppLanguage s_current = AppLanguage.English;

    public static AppLanguage Current => s_current;

    /// <summary>Right-to-left layout is required for Arabic.</summary>
    public static bool IsRtl => s_current == AppLanguage.Arabic;

    /// <summary>Returns true when the English source text has a translation entry.</summary>
    public static bool HasTranslation(string english) =>
        !string.IsNullOrEmpty(english) && s_table.ContainsKey(NormalizeKey(english));

    /// <summary>Raised after the active language changes (on the UI thread).</summary>
    public static event Action? Changed;

    /// <summary>Native name shown in the tray language submenu.</summary>
    public static string NativeName(AppLanguage language) => language switch
    {
        AppLanguage.English => "English",
        AppLanguage.Arabic => "العربية",
        AppLanguage.French => "Français",
        AppLanguage.Spanish => "Español",
        _ => "English",
    };

    /// <summary>Two-letter code stored in settings.json.</summary>
    public static string Code(AppLanguage language) => language switch
    {
        AppLanguage.Arabic => "ar",
        AppLanguage.French => "fr",
        AppLanguage.Spanish => "es",
        _ => "en",
    };

    public static bool TryParse(string? code, out AppLanguage language)
    {
        switch ((code ?? "").Trim().ToLowerInvariant())
        {
            case "ar": case "arabic": language = AppLanguage.Arabic; return true;
            case "fr": case "french": language = AppLanguage.French; return true;
            case "es": case "spanish": case "spain": language = AppLanguage.Spanish; return true;
            case "en": case "english": language = AppLanguage.English; return true;
            default: language = AppLanguage.English; return false;
        }
    }

    public static void SetLanguage(AppLanguage language, bool notify = true)
    {
        if (s_current == language) return;
        s_current = language;
        if (notify) Changed?.Invoke();
    }

    /// <summary>Translates an English UI string into the active language.</summary>
    public static string T(string english)
    {
        if (string.IsNullOrEmpty(english) || s_current == AppLanguage.English) return english;
        var key = NormalizeKey(english);
        if (!s_table.TryGetValue(key, out var value)) return english;
        var translated = s_current switch
        {
            AppLanguage.French => value.Fr,
            AppLanguage.Arabic => value.Ar,
            AppLanguage.Spanish => value.Es,
            _ => english,
        };
        return string.IsNullOrEmpty(translated) ? english : translated;
    }

    /// <summary>Treats the typographic ellipsis and three dots as the same key.</summary>
    private static string NormalizeKey(string text) => text.Replace("…", "...");

    /// <summary>Translates a composite English format string, then formats it.</summary>
    public static string F(string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);

    /// <summary>Formats a byte count with the active language's number grouping.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = Current switch
        {
            AppLanguage.French => ["o", "Ko", "Mo", "Go"],
            AppLanguage.Arabic => ["بايت", "ك.ب", "م.ب", "غ.ب"],
            AppLanguage.Spanish => ["B", "KB", "MB", "GB"],
            _ => ["B", "KB", "MB", "GB"],
        };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return string.Format(CultureInfo.CurrentCulture, unit == 0 ? "{0:0} {1}" : "{0:0.#} {1}", value, units[unit]);
    }
}
