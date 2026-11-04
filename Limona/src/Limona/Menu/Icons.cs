using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Limona.Menu;

/// <summary>
/// Placeholder vector icons (24x24, stroke-only) built programmatically.
/// Each entry returns a fresh geometry instance for its <see cref="Microsoft.UI.Xaml.Shapes.Path"/>.
/// </summary>
public static class Icons
{
    public static Geometry Images() => Geo(
        Fig(4, 5, true, Seg(20, 5), Seg(20, 19), Seg(4, 19)),
        Fig(4, 15, false, Seg(9, 10.5), Seg(12.5, 14), Seg(15, 11.5), Seg(20, 15.5)),
        Fig(11.9, 8.4, false, Cir(13.6, 8.4, 1.7)));

    /// <summary>Color eyedropper for the Edit Image window palette bar.</summary>
    public static Geometry Dropper() => Geo(
        Fig(4, 20, false, Seg(10.7, 13.3)),
        Fig(12.2, 14.8, true, Seg(20, 7), Seg(17, 4), Seg(9.2, 11.8)));

    public static Geometry Documents() => Geo(
        Fig(6, 4, true, Seg(14, 4), Seg(19, 9), Seg(19, 20), Seg(6, 20)),
        Fig(14, 4, false, Seg(14, 9), Seg(19, 9)),
        Fig(9, 13, false, Seg(16, 13)),
        Fig(9, 16, false, Seg(14, 16)));

    public static Geometry Audio() => Geo(
        Fig(4.2, 18.5, false, Cir(6.6, 18.5, 2.4)),
        Fig(14.9, 15.5, false, Cir(17.2, 15.5, 2.3)),
        Fig(8.9, 18.5, false, Seg(8.9, 6.5), Seg(19.4, 4.3), Seg(19.4, 15.4)));

    public static Geometry Video() => Geo(
        Fig(3.5, 12, false, Cir(12, 12, 8.5)),
        Fig(10, 8.6, true, Seg(16.2, 12), Seg(10, 15.4)));

    public static Geometry Other() => Geo(
        Fig(2.6, 10.3, false, Cir(4.5, 10.3, 1.9)),
        Fig(10.1, 10.3, false, Cir(12, 10.3, 1.9)),
        Fig(17.7, 10.3, false, Cir(19.6, 10.3, 1.9)));

    public static Geometry Settings() => Geo(
        Fig(5, 8, false, Seg(19, 8)),
        Fig(5, 13, false, Seg(19, 13)),
        Fig(5, 18, false, Seg(19, 18)),
        Fig(12.3, 8, false, Cir(13.9, 8, 1.6)),
        Fig(5.4, 13, false, Cir(7, 13, 1.6)),
        Fig(10.3, 18, false, Cir(11.9, 18, 1.6)));

    // --- Dynamic format icons ---

    public static Geometry Png() => Geo(
        Fig(4, 4, true, Seg(20, 4), Seg(20, 20), Seg(4, 20)),
        Fig(13.8, 7.8, false, Cir(15.2, 7.8, 1.4)),
        Fig(4, 16, false, Seg(9, 11), Seg(13, 15), Seg(16, 12.5), Seg(20, 16.5)));

    public static Geometry Jpg() => Geo(
        Fig(3, 7.5, true, Seg(7, 7.5), Seg(8.5, 5), Seg(15.5, 5), Seg(17, 7.5), Seg(21, 7.5), Seg(21, 19.5), Seg(3, 19.5)),
        Fig(9.2, 13.5, false, Cir(12, 13.5, 2.8)));

    public static Geometry Bmp() => Geo(
        Fig(4, 4, true, Seg(10.5, 4), Seg(10.5, 10.5), Seg(4, 10.5)),
        Fig(13.5, 4, true, Seg(20, 4), Seg(20, 10.5), Seg(13.5, 10.5)),
        Fig(4, 13.5, true, Seg(10.5, 13.5), Seg(10.5, 20), Seg(4, 20)),
        Fig(13.5, 13.5, true, Seg(20, 13.5), Seg(20, 20), Seg(13.5, 20)));

    public static Geometry Ico() => Geo(
        Fig(4, 4, true, Seg(20, 4), Seg(20, 20), Seg(4, 20)),
        Fig(4, 9, false, Seg(20, 9)),
        Fig(6.8, 6.5, false, Cir(7.8, 6.5, 1.0)),
        Fig(10.8, 6.5, false, Cir(11.8, 6.5, 1.0)),
        Fig(12, 11.5, false, Seg(12, 17.5)),
        Fig(9, 14.5, false, Seg(15, 14.5)));

    public static Geometry Gif() => Geo(
        Fig(3.5, 5, true, Seg(20.5, 5), Seg(20.5, 19), Seg(3.5, 19)),
        Fig(10, 8.5, true, Seg(15.5, 12), Seg(10, 15.5)));

    public static Geometry Pdf() => Geo(
        Fig(5, 3.5, true, Seg(13.5, 3.5), Seg(19, 9), Seg(19, 20.5), Seg(5, 20.5)),
        Fig(13.5, 3.5, false, Seg(13.5, 9), Seg(19, 9)),
        Fig(8.5, 13, false, Seg(15.5, 13)),
        Fig(8.5, 16.5, false, Seg(13.5, 16.5)));

    /// <summary>Remove Background icon: photo frame with transparent checkerboard hint + erase wand.</summary>
    public static Geometry RemoveBg() => Geo(
        // Outer frame
        Fig(3.5, 3.5, true, Seg(20.5, 3.5), Seg(20.5, 20.5), Seg(3.5, 20.5)),
        // Dashed inner transparency hint (two diagonal checker squares, 4x4)
        Fig(3.5, 3.5, true, Seg(8, 3.5), Seg(8, 8), Seg(3.5, 8)),
        Fig(8, 8, true, Seg(12.5, 8), Seg(12.5, 12.5), Seg(8, 12.5)),
        // Magic wand / eraser diagonal stroke from bottom-left to top-right
        Fig(6, 18, false, Seg(18, 6)),
        // Wand tip sparkle (small cross)
        Fig(17, 5, false, Seg(19, 7)),
        Fig(17, 7, false, Seg(19, 5)));

    /// <summary>Resize icon: photo frame with a double-headed diagonal arrow.</summary>
    public static Geometry Resize() => Geo(
        // Outer frame
        Fig(3.5, 3.5, true, Seg(20.5, 3.5), Seg(20.5, 20.5), Seg(3.5, 20.5)),
        // Diagonal arrow shaft
        Fig(8.4, 15.6, false, Seg(15.6, 8.4)),
        // Top-right arrowhead
        Fig(12.2, 8.4, false, Seg(15.6, 8.4), Seg(15.6, 11.8)),
        // Bottom-left arrowhead
        Fig(11.8, 15.6, false, Seg(8.4, 15.6), Seg(8.4, 12.2)));

    // --- Folder action icons ---

    /// <summary>Folder shape (outline + tab).</summary>
    public static Geometry FolderIcon() => Geo(
        Fig(3, 5.5, true, Seg(9.6, 5.5), Seg(11.6, 7.8), Seg(21, 7.8), Seg(21, 18.5), Seg(3, 18.5)));

    /// <summary>ZIP archive: box with a zipper down the middle.</summary>
    public static Geometry Zip() => Geo(
        Fig(4.5, 6.5, true, Seg(19.5, 6.5), Seg(19.5, 19), Seg(4.5, 19), Seg(4.5, 6.5)),
        Fig(12, 8, false, Seg(12, 11)),
        Fig(12, 12.5, false, Seg(12, 15)),
        Fig(12, 16.5, false, Seg(12, 17.5)),
        Fig(10.6, 13.2, false, Seg(13.4, 13.2)),
        Fig(10.6, 15.7, false, Seg(13.4, 15.7)));

    /// <summary>Copy path: two stacked squares.</summary>
    public static Geometry CopyPath() => Geo(
        Fig(9.5, 9.5, true, Seg(19.5, 9.5), Seg(19.5, 19.5), Seg(9.5, 19.5)),
        Fig(7, 14, false, Seg(7, 7), Seg(7, 6.5), Seg(14.5, 6.5), Seg(14.5, 7)),
        Fig(4.5, 16.5, false, Seg(4.5, 10)));

    /// <summary>Folder size: baseline + bar chart of different heights.</summary>
    public static Geometry Ruler() => Geo(
        Fig(4, 19, false, Seg(20, 19)),
        Fig(6, 19, false, Seg(6, 13.5)),
        Fig(10.5, 19, false, Seg(10.5, 8.5)),
        Fig(15, 19, false, Seg(15, 15)),
        Fig(19, 19, false, Seg(19, 5.5)));

    /// <summary>Terminal: window with prompt chevron and cursor.</summary>
    public static Geometry Terminal() => Geo(
        Fig(3.5, 4.5, true, Seg(20.5, 4.5), Seg(20.5, 19.5), Seg(3.5, 19.5)),
        Fig(7, 9.5, false, Seg(9.5, 12), Seg(7, 14.5)),
        Fig(11.5, 15.5, false, Seg(16, 15.5)));

    // --- Player transport icons (Material Design, filled 24x24) ---

    /// <summary>Play: rounded play triangle.</summary>
    public static Geometry Play() => Material(
        "M8 5.5v13c0 .82.9 1.31 1.57.9l10.2-6.5c.63-.4.63-1.4 0-1.8L9.57 4.6C8.9 4.19 8 4.68 8 5.5z");

    /// <summary>Pause: two rounded vertical bars.</summary>
    public static Geometry Pause() => Material(
        "M6 19h4V5H6v14zm8-14v14h4V5h-4z");

    /// <summary>Stop: solid rounded square.</summary>
    public static Geometry Stop() => Material(
        "M7 18h10c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2H7c-1.1 0-2 .9-2 2v8c0 1.1.9 2 2 2z");

    /// <summary>Undo: curved back arrow (Material "undo").</summary>
    public static Geometry Undo() => Material(
        "M12.5 8c-2.65 0-5.05.99-6.9 2.6L2 7v9h9l-3.62-3.62c1.39-1.16 3.16-1.88 5.12-1.88 3.54 0 6.55 2.31 7.6 5.5l2.37-.78C21.08 11.03 17.15 8 12.5 8z");

    /// <summary>Trim: scissors (Material "content_cut").</summary>
    public static Geometry Cut() => Material(
        "M9.64 7.64c.23-.5.36-1.05.36-1.64 0-2.21-1.79-4-4-4S2 3.79 2 6s1.79 4 4 4c.59 0 1.14-.13 1.64-.36L10 12l-2.36 2.36C7.14 14.13 6.59 14 6 14c-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4c0-.59-.13-1.14-.36-1.64L12 14l7 7h3v-1L9.64 7.64zM6 8c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zm0 12c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zm6-7.5c-.28 0-.5-.22-.5-.5s.22-.5.5-.5.5.22.5.5-.22.5-.5.5zM19 3l-6 6 2 2 7-7V3h-3z");

    /// <summary>Volume: speaker with sound waves (Material "volume_up").</summary>
    public static Geometry Volume() => Material(
        "M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0 0 14 7.97v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z");

    /// <summary>Crop: Material "crop" frame corners.</summary>
    public static Geometry Crop() => Material(
        "M17 15h2V7c0-1.1-.9-2-2-2H9v2h8v8zM7 17V9H5v2H3v2h2v6c0 1.1.9 2 2 2h8v2h2v-2h2v-2H7z");

    // --- Image editor icons (Material Design, filled 24x24) ---

    /// <summary>Rotate clockwise 90°.</summary>
    public static Geometry RotateCw() => Material(
        "M7.11 8.53L5.7 7.11C4.8 8.27 4.24 9.61 4.07 11h2.02c.14-.87.49-1.72 1.02-2.47zM6.09 13H4.07c.17 1.39.72 2.73 1.62 3.89l1.41-1.42c-.52-.75-.87-1.59-1.01-2.47zm1.01 5.32c1.16.9 2.51 1.44 3.89 1.61V17.9c-.87-.15-1.71-.49-2.46-1.03L7.1 18.32zM13 4.07V1.6L9.5 5.1 13 8.6V6.09c2.84.48 5 2.94 5 5.91s-2.16 5.43-5 5.91v2.02c3.95-.49 7-3.85 7-7.93s-3.05-7.44-7-7.93z");

    /// <summary>Rotate counter-clockwise 90°.</summary>
    public static Geometry RotateCcw() => Material(
        "M16.89 8.53l1.41-1.42C19.2 8.27 19.76 9.61 19.93 11h-2.02c-.14-.87-.49-1.72-1.02-2.47zM17.91 13h2.02c-.17 1.39-.72 2.73-1.62 3.89l-1.41-1.42c.52-.75.87-1.59 1.01-2.47zm-1.01 5.32c-1.16.9-2.51 1.44-3.89 1.61V17.9c.87-.15 1.71-.49 2.46-1.03l1.43 1.45zM11 4.07V1.6l3.5 3.5L11 8.6V6.09c-2.84.48-5 2.94-5 5.91s2.16 5.43 5 5.91v2.02c-3.95-.49-7-3.85-7-7.93s3.05-7.44 7-7.93z");

    /// <summary>Flip horizontal: vertical mirror axis with triangle.</summary>
    public static Geometry FlipH() => Material(
        "M15 21h2V3h-2v18zM3 5.5v13L9 12 3 5.5zM21 3h-2v18h2V3z");

    /// <summary>Flip vertical: horizontal mirror axis with triangle.</summary>
    public static Geometry FlipV() => Material(
        "M3 15h18v2H3v-2zM5.5 3l13 6L5.5 21V3zM3 21h2V3H3v18z");

    /// <summary>Check mark (save/apply).</summary>
    public static Geometry Check() => Material(
        "M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z");

    /// <summary>Close (X).</summary>
    public static Geometry CloseIcon() => Material(
        "M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z");

    /// <summary>Reset (circular arrow).</summary>
    public static Geometry ResetIcon() => Material(
        "M12 5V1L7 6l5 5V7c3.31 0 6 2.69 6 6s-2.69 6-6 6-6-2.69-6-6H4c0 4.42 3.58 8 8 8s8-3.58 8-8-3.58-8-8-8z");

    /// <summary>Parses an SVG/Material path string into a fresh Geometry instance.</summary>
    private static Geometry Material(string pathData) =>
        (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(
            typeof(Geometry), pathData);

    // --------------------------------------------------------------
    // Construction helpers
    // -------------------------------------------------------------

    private static PathGeometry Geo(params PathFigure[] figures)
    {
        var geometry = new PathGeometry();
        foreach (var figure in figures)
        {
            geometry.Figures.Add(figure);
        }
        return geometry;
    }

    private static PathFigure Fig(double startX, double startY, bool closed, params PathSegment[] segments)
    {
        var figure = new PathFigure
        {
            StartPoint = new Point(startX, startY),
            IsClosed = closed,
        };
        foreach (var segment in segments)
        {
            figure.Segments.Add(segment);
        }
        return figure;
    }

    private static PathSegment Seg(double x, double y) => new LineSegment { Point = new Point(x, y) };

    /// <summary>A full circle expressed as four quarter arcs.</summary>
    private static PathSegment[] Cir(double cx, double cy, double r)
    {
        var w = new Point(cx - r, cy);
        var n = new Point(cx, cy - r);
        var e = new Point(cx + r, cy);
        var s = new Point(cx, cy + r);

        static ArcSegment Arc(Point to, double r) => new()
        {
            Point = to,
            Size = new Size(r, r),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise,
        };

        return new PathSegment[] { Arc(n, r), Arc(e, r), Arc(s, r), Arc(w, r) };
    }
}
