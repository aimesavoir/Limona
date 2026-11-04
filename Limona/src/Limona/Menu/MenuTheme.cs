namespace Limona.Menu;

/// <summary>Visual constants (colors + geometry) of the radial overlay.</summary>
public static class MenuTheme
{
    // Geometry (design units at 96 DPI).
    public const double GlobalScale = 0.72;

    public static readonly double DiscRadius = 172.0 * GlobalScale;     // slice ring outer boundary (~124 px)
    public static readonly double Size   = DiscRadius * 2.0;            // window canvas side
    public static readonly double Center = DiscRadius;                  // center of the canvas

    public static readonly double HubRadius = 74.0 * GlobalScale;                    // central hub area
    public static readonly double SectorInnerRadius = HubRadius + 8.0 * GlobalScale; // slice inner radius
    public static readonly double SectorOuterVisualRadius = DiscRadius - 4.0 * GlobalScale; // slice outer visual radius
    public static readonly double SectorOuterRadius = DiscRadius;                    // hit-test outer limit
    public static readonly double RingRadius = (SectorInnerRadius + SectorOuterVisualRadius) / 2.0; // icon center

    public static readonly double SliceCornerRadius = 7.0 * GlobalScale;
    public const double SectorHalfSpanDeg = 26.0;

    public static readonly double IconSize = 28.0 * GlobalScale;
    public static readonly double HubLabelWidth = 150.0 * GlobalScale;
    public static readonly double HubHintWidth  = 140.0 * GlobalScale;

    public const double IconStrokeWidth = 2.4;

    // Colors (ARGB hex, 8-digit = AARRGGBB).
    // SectorBaseFill / HubFill / AccentColor are user-changeable
    // (Settings → Menu theme) and persisted via AppSettings.
    public static string SectorBaseFill = "#FF181A22";
    public static string HubFill        = "#FA14171F";
    public static string AccentColor    = "#FFF26F1D";

    /// <summary>Warm gold — secondary highlight (hover rims, selected swatches).</summary>
    public const string AccentGold      = "#FFF2B835";

    /// <summary>Deep red-orange — destructive actions and errors.</summary>
    public const string AccentDeep      = "#FFF9471B";

    public const string IconHoverColor = "#FF121418";

    public static string IconColor = "#FFFFFFFF";
    public static string LabelColor = "#FFE8EAF2";
    public static string HintColor = "#78B8C4D8";

    /// <summary>Persistent slice outline + hub ring.</summary>
    public static string HubRing = "#35FFFFFF";
    public static string SectorBaseStroke = "#35FFFFFF";
    public static string SectorHoverStroke = "#FFF2B835";

    /// <summary>
    /// Applies a user-chosen wheel background (ARGB hex) and keeps the hub
    /// tinted from the same RGB with its translucent alpha.
    /// </summary>
    public static void SetMenuBackground(string argbHex)
    {
        SectorBaseFill = argbHex;
        HubFill = "#FA" + argbHex[3..]; // hub keeps its translucency (#AA RR GG BB → #FA + RGB)

        var r = Convert.ToInt32(argbHex[3..5], 16) / 255.0;
        var g = Convert.ToInt32(argbHex[5..7], 16) / 255.0;
        var b = Convert.ToInt32(argbHex[7..9], 16) / 255.0;
        var luminance = 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);
        var lightBackground = luminance > 0.45;

        IconColor = lightBackground ? "#FF4A4A4A" : "#FFFFFFFF";
        LabelColor = lightBackground ? "#FF4A4A4A" : "#FFE8EAF2";
        HintColor = lightBackground ? "#FF666666" : "#78B8C4D8";
        HubRing = lightBackground ? "#50000000" : "#35FFFFFF";
        SectorBaseStroke = lightBackground ? "#40000000" : "#35FFFFFF";
    }

    private static double Linearize(double channel) =>
        channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    /// <summary>
    /// Applies a user-chosen accent (highlight/label) color.
    /// </summary>
    public static void SetMenuAccent(string argbHex)
    {
        AccentColor = argbHex;

        var (a, r, g, b) = Parse(argbHex);
        var accent = Windows.UI.Color.FromArgb(a, r, g, b);
        var light = Windows.UI.Color.FromArgb(0xFF, Blend(r, 255, 0.35), Blend(g, 255, 0.35), Blend(b, 255, 0.35));
        var lighter = Windows.UI.Color.FromArgb(0xFF, Blend(r, 255, 0.65), Blend(g, 255, 0.65), Blend(b, 255, 0.65));
        var dark = Windows.UI.Color.FromArgb(0xFF, Blend(r, 0, 0.2), Blend(g, 0, 0.2), Blend(b, 0, 0.2));
        var darker = Windows.UI.Color.FromArgb(0xFF, Blend(r, 0, 0.4), Blend(g, 0, 0.4), Blend(b, 0, 0.4));
        var resources = Microsoft.UI.Xaml.Application.Current.Resources;

        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorLight1"] = light;
        resources["SystemAccentColorLight2"] = lighter;
        resources["SystemAccentColorDark1"] = dark;
        resources["SystemAccentColorDark2"] = darker;
        SetAccentBrush(resources, "SystemControlBackgroundAccentBrush", accent);
        SetAccentBrush(resources, "SystemControlForegroundAccentBrush", accent);
        SetAccentBrush(resources, "SystemControlHighlightAccentBrush", accent);
        SetAccentBrush(resources, "SystemControlHighlightAltAccentBrush", dark);
        SetAccentBrush(resources, "SystemControlDisabledAccentBrush", light);
        SetAccentBrush(resources, "SystemControlHighlightListAccentLowBrush", light);
        SetAccentBrush(resources, "SystemControlHighlightListAccentMediumBrush", accent);
        SetAccentBrush(resources, "SystemControlHighlightListAccentHighBrush", dark);
        SetAccentBrush(resources, "SystemControlHighlightAltListAccentHighBrush", darker);
    }

    private static void SetAccentBrush(
        Microsoft.UI.Xaml.ResourceDictionary resources,
        string key,
        Windows.UI.Color color) =>
        resources[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

    private static (byte A, byte R, byte G, byte B) Parse(string hex) =>
        (Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16),
            Convert.ToByte(hex[5..7], 16), Convert.ToByte(hex[7..9], 16));

    private static byte Blend(byte value, byte target, double amount) =>
        (byte)Math.Round(value + (target - value) * amount);
}
