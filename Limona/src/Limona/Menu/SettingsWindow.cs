using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Limona.Services;

namespace Limona.Menu;

/// <summary>
/// Compact theme window: a live miniature of the radial wheel beside the
/// background/accent pickers, and an OK button to close. Startup and other
/// behaviour are managed by the app itself (never user-configurable).
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? s_active;

    private static readonly string[] BackPalette =
    [
        "#FF181A22", // obsidian (default)
        "#FF10192B", // deep blue
        "#FF241312", // wine
        "#FF122420", // teal dark
        "#FF211327", // plum dark
        "#FF000000", // black
        "#FFFFFFFF", // white
    ];

    private static readonly string[] AccentPalette =
    [
        "#FFF26F1D", // orange (default)
        "#FFF2B835", // golden orange
        "#FFF9471B", // red orange
        "#FF286968",
        "#FFE8175C",
        "#FFFF76C6",
        "#FFFF2F8C",
        "#FFFF001F",
    ];

    private readonly List<(Button Button, string Hex)> _backSwatches = new();
    private readonly List<(Button Button, string Hex)> _accentSwatches = new();
    private Button _okButton = null!;

    private const double WheelPx = 150.0;

    /// <summary>Opens the settings window (single instance at a time).</summary>
    public static void Show()
    {
        s_active = ToolWindowCoordinator.Open(() => new SettingsWindow());
    }

    private SettingsWindow()
    {
        WindowFit.SetTitle(this, "Limona — Theme");

        Content = new Border
        {
            Background = Brush(MenuTheme.HubFill),
            Padding = new Thickness(22, 18, 22, 18),
            Child = BuildLayout(),
        };

        WindowFit.Apply(this, (FrameworkElement)Content!);
        this.Closed += (_, _) => s_active = null;
    }

    private UIElement BuildLayout()
    {
        var stack = new StackPanel { Spacing = 16 };

        // Theme: wheel on the left, color pickers on the right.
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 20,
        };
        row.Children.Add(MakeWheelHost());
        row.Children.Add(MakeColorPickers());
        stack.Children.Add(row);

        // OK — bottom of the page, closes the window.
        _okButton = new Button
        {
            Content = "OK",
            MinWidth = 160,
            Padding = new Thickness(0, 7, 0, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brush(MenuTheme.AccentColor),
            Foreground = Brush(MenuTheme.IconHoverColor),
            CornerRadius = new CornerRadius(6),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        _okButton.Click += (_, _) => Close();
        stack.Children.Add(_okButton);

        RefreshSwatchSelection();
        return stack;
    }

    private ContentControl _wheelHost = null!;

    private FrameworkElement MakeWheelHost()
    {
        _wheelHost = new ContentControl { Content = MakeWheelContent() };
        return new Border
        {
            Width = WheelPx,
            Height = WheelPx,
            BorderBrush = Brush(MenuTheme.HubRing),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(WheelPx / 2),
            Child = new Canvas
            {
                Width = WheelPx,
                Height = WheelPx,
                Children = { _wheelHost },
            },
        };
    }

    private UIElement MakeColorPickers()
    {
        var col = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };

        col.Children.Add(new TextBlock
        {
            Text = "Background",
            FontSize = 12,
            Foreground = Brush(MenuTheme.HintColor),
        });
        var backRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var hex in BackPalette)
        {
            var button = MakeSwatchButton(hex);
            backRow.Children.Add(button);
            _backSwatches.Add((button, hex));
        }
        col.Children.Add(backRow);

        col.Children.Add(new TextBlock
        {
            Text = "Accent",
            FontSize = 12,
            Foreground = Brush(MenuTheme.HintColor),
        });
        var accentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var hex in AccentPalette)
        {
            var button = MakeSwatchButton(hex);
            accentRow.Children.Add(button);
            _accentSwatches.Add((button, hex));
        }
        col.Children.Add(accentRow);

        return col;
    }

    private Button MakeSwatchButton(string hex)
    {
        var button = new Button
        {
            Content = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 28,
                Height = 28,
                Fill = Brush(hex),
                Stroke = Brush(hex.Equals("#FFFFFFFF", StringComparison.OrdinalIgnoreCase)
                    ? "#FF707070"
                    : "#00000000"),
                StrokeThickness = 1,
            },
            Padding = new Thickness(0),
            MinWidth = 34,
            MinHeight = 34,
        };
        button.Click += (_, _) => OnThemePicked(hex);
        return button;
    }

    private void OnThemePicked(string hex)
    {
        Limona.Interop.Dbg.Log("Settings theme swatch clicked: " + hex);
        try
        {
            if (BackPalette.Contains(hex))
            {
                AppSettings.SetMenuBackground(hex);
            }
            else if (AccentPalette.Contains(hex))
            {
                AppSettings.SetMenuAccent(hex);
            }
            else
            {
                Limona.Interop.Dbg.Log("Settings swatch: no palette match for " + hex);
                return;
            }

            LimonaWindow.RefreshTheme();
            RefreshSwatchSelection();
            _wheelHost.Content = MakeWheelContent();
            _okButton.Background = Brush(MenuTheme.AccentColor);
            Limona.Interop.Dbg.Log("Settings theme applied: " + hex);
        }
        catch (Exception ex)
        {
            Limona.Interop.Dbg.Log("OnThemePicked THREW: " + ex);
            throw;
        }
    }

    /// <summary>Highlights the currently selected swatch in each row.</summary>
    private void RefreshSwatchSelection()
    {
        HighlightRow(_backSwatches, MenuTheme.SectorBaseFill);
        HighlightRow(_accentSwatches, MenuTheme.AccentColor);
    }

    private static void HighlightRow(List<(Button Button, string Hex)> row, string current)
    {
        var i = row.FindIndex(s => s.Hex.Equals(current, StringComparison.OrdinalIgnoreCase));
        for (var k = 0; k < row.Count; k++)
        {
            var selected = k == i;
            row[k].Button.BorderThickness = selected ? new Thickness(2) : new Thickness(0);
            row[k].Button.BorderBrush = Brush(selected ? MenuTheme.AccentGold : "#00000000");
        }
    }

    // ------------------------------------------------------------------
    // Miniature replica of the radial wheel (used as the live preview).
    // ------------------------------------------------------------------

    /// <summary>
    /// Draws at the real design units and scales down, so the preview always
    /// matches 1:1 with what the wheel will look like. One wedge is lit with
    /// the solid accent color.
    /// </summary>
    private UIElement MakeWheelContent()
    {
        var scale = WheelPx / MenuTheme.Size;

        var wheel = new Canvas { Width = MenuTheme.Size, Height = MenuTheme.Size };
        wheel.RenderTransform = new ScaleTransform
        {
            ScaleX = scale,
            ScaleY = scale,
        };

        var entries = MenuCatalog.DefaultEntries;
        var count = entries.Length;
        var span = 360.0 / count;

        for (var i = 0; i < count; i++)
        {
            var a = -90.0 + i * span;          // slice center angle: item 0 at top, clockwise
            var halfSpan = span / 2.0 - 4.0;

            // One wedge (right of the top slice) lit with the accent color.
            var lit = i == 1;
            var wedge = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = WedgePreviewGeometry(a, halfSpan),
                Fill = lit ? Brush(MenuTheme.AccentColor) : Brush(MenuTheme.SectorBaseFill),
                Stroke = Brush(lit ? MenuTheme.SectorHoverStroke : MenuTheme.SectorBaseStroke),
                StrokeThickness = 1.0,
            };
            wheel.Children.Add(wedge);

            var iconPoint = PointAtR(a, MenuTheme.RingRadius);
            var icon = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = entries[i].Icon(),
                Stroke = Brush(lit ? MenuTheme.IconHoverColor : MenuTheme.IconColor),
                StrokeThickness = MenuTheme.IconStrokeWidth,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = MenuTheme.IconSize,
                Height = MenuTheme.IconSize,
                Stretch = Stretch.Uniform,
            };
            Canvas.SetLeft(icon, iconPoint.X - MenuTheme.IconSize / 2);
            Canvas.SetTop(icon, iconPoint.Y - MenuTheme.IconSize / 2);
            wheel.Children.Add(icon);

            var badge = new TextBlock
            {
                Text = entries[i].Badge,
                FontSize = 9.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush(MenuTheme.IconColor),
                Width = 80,
                TextAlignment = TextAlignment.Center,
            };
            Canvas.SetLeft(badge, iconPoint.X - 40);
            Canvas.SetTop(badge, iconPoint.Y + MenuTheme.IconSize / 2);
            wheel.Children.Add(badge);
        }

        // Hub disc + label
        var hub = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = MenuTheme.HubRadius * 2,
            Height = MenuTheme.HubRadius * 2,
            Fill = Brush(MenuTheme.HubFill),
            Stroke = Brush(MenuTheme.HubRing),
            StrokeThickness = 1.2,
        };
        Canvas.SetLeft(hub, MenuTheme.Center - MenuTheme.HubRadius);
        Canvas.SetTop(hub, MenuTheme.Center - MenuTheme.HubRadius);
        wheel.Children.Add(hub);

        var hubLabel = new TextBlock
        {
            Text = "MENU",
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(MenuTheme.AccentColor),
            Width = MenuTheme.HubRadius * 2,
            TextAlignment = TextAlignment.Center,
        };
        Canvas.SetLeft(hubLabel, MenuTheme.Center - MenuTheme.HubRadius);
        Canvas.SetTop(hubLabel, MenuTheme.Center - 9);
        wheel.Children.Add(hubLabel);

        return wheel;
    }

    /// <summary>Simplified annular sector (no corner rounding) for the preview wheel.</summary>
    private static Geometry WedgePreviewGeometry(double centerAngle, double halfSpanDeg)
    {
        var a1 = centerAngle - halfSpanDeg;
        var a2 = centerAngle + halfSpanDeg;
        var rIn = MenuTheme.SectorInnerRadius;
        var rOut = MenuTheme.SectorOuterVisualRadius;

        var p1 = PointAtR(a1, rIn);
        var p2 = PointAtR(a1, rOut);
        var p3 = PointAtR(a2, rOut);
        var p4 = PointAtR(a2, rIn);

        var figure = new PathFigure { StartPoint = p1, IsClosed = true };
        figure.Segments.Add(new LineSegment { Point = p2 });
        figure.Segments.Add(new ArcSegment
        {
            Point = p3,
            Size = new Windows.Foundation.Size(rOut, rOut),
            IsLargeArc = (a2 - a1) > 180,
            SweepDirection = SweepDirection.Clockwise,
        });
        figure.Segments.Add(new LineSegment { Point = p4 });
        figure.Segments.Add(new ArcSegment
        {
            Point = p1,
            Size = new Windows.Foundation.Size(rIn, rIn),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Counterclockwise,
        });

        return new PathGeometry { Figures = { figure } };
    }

    /// <summary>Point on a circle around MenuTheme.Center at a given direction (degrees).</summary>
    private static Windows.Foundation.Point PointAtR(double angleDeg, double radius)
    {
        var rad = angleDeg * Math.PI / 180.0;
        return new Windows.Foundation.Point(
            MenuTheme.Center + Math.Cos(rad) * radius,
            MenuTheme.Center + Math.Sin(rad) * radius);
    }

    private static SolidColorBrush Brush(string hex) => new(ParseColor(hex));

    private static Windows.UI.Color ParseColor(string hex)
    {
        if (hex.Length != 9 || hex[0] != '#' || hex[1..].Any(c => !Uri.IsHexDigit(c)))
        {
            Limona.Interop.Dbg.Log("ParseColor: malformed color '" + hex + "' — using obsidian fallback");
            hex = "#FF181A22";
        }
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Windows.UI.Color.FromArgb(
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
    }
}
