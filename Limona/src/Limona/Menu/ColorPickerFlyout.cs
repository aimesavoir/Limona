using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using Windows.Foundation;
using Windows.UI;

namespace Limona.Menu;

/// <summary>
/// Modern HSV colour picker built from plain XAML primitives:
/// saturation/value square, hue strip, alpha strip, hex input and presets.
/// Matches the app's dark theme instead of the stock light flyout controls.
/// </summary>
public static class ColorPickerFlyout
{
    private static readonly uint[] PresetColors =
    {
        0xFFFFFFFF, 0xFFE6E9F0, 0xFF9AA3B5, 0xFF4B5264, 0xFF15171E, 0xFF000000,
        0xFFE84D4D, 0xFFFF7A45, 0xFFFFC93C, 0xFF4CAF50, 0xFF35AAA5, 0xFF4E80D8,
        0xFF8D69C8, 0xFFD2549B, 0xFFFF78B7, 0xFF8D6E63,
    };

    /// <summary>A rounded colour chip that opens the picker when clicked.</summary>
    public sealed class SwatchButton
    {
        private readonly Border _fill;

        internal SwatchButton(Button button, Border fill)
        {
            Button = button;
            _fill = fill;
        }

        public Button Button { get; }

        public void SetColor(uint argb) => _fill.Background = new SolidColorBrush(Unpack(argb));
    }

    public static SwatchButton MakeSwatch(uint argb, Action<uint> onChanged, string tooltip)
    {
        var fill = new Border
        {
            Width = 44,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Background = new SolidColorBrush(Unpack(argb)),
            IsHitTestVisible = false,
        };
        var button = new Button
        {
            Content = fill,
            Padding = new Thickness(5),
            MinWidth = 0,
            MinHeight = 32,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);

        var flyout = Create(argb, color =>
        {
            fill.Background = new SolidColorBrush(Unpack(color));
            onChanged(color);
        });
        button.Click += (_, _) => flyout.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
        return new SwatchButton(button, fill);
    }

    private static Flyout Create(uint initial, Action<uint> onChanged)
    {
        const double svW = 236;
        const double svH = 150;
        const double barW = 236;
        const double barH = 14;

        var start = Unpack(initial);
        byte a = start.A, r = start.R, g = start.G, b = start.B;
        RgbToHsv(r, g, b, out var h, out var s, out var v);

        // ---- saturation / value square ----------------------------------
        var svBase = new Rectangle { Width = svW, Height = svH, Fill = new SolidColorBrush(ColorFromHsv(h, 1, 1)) };
        var svWhite = new Rectangle
        {
            Width = svW,
            Height = svH,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 1 },
                },
            },
        };
        var svBlack = new Rectangle
        {
            Width = svW,
            Height = svH,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x00, 0x00, 0x00, 0x00), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x00, 0x00, 0x00), Offset = 1 },
                },
            },
        };
        var svThumb = new Ellipse
        {
            Width = 14,
            Height = 14,
            Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0)),
        };
        var sv = new Canvas { Width = svW, Height = svH };
        sv.Children.Add(svBase);
        sv.Children.Add(svWhite);
        sv.Children.Add(svBlack);
        sv.Children.Add(svThumb);

        // ---- hue strip ---------------------------------------------------
        var hueFill = new Rectangle
        {
            Width = barW,
            Height = barH,
            RadiusX = 7,
            RadiusY = 7,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0xFF, 0xFF, 0x00, 0x00), Offset = 0.0 / 6 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00), Offset = 1.0 / 6 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x00, 0xFF, 0x00), Offset = 2.0 / 6 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF), Offset = 3.0 / 6 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x00, 0x00, 0xFF), Offset = 4.0 / 6 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0xFF, 0x00, 0xFF), Offset = 5.0 / 6 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0xFF, 0x00, 0x00), Offset = 6.0 / 6 },
                },
            },
        };
        var hueThumb = MakeBarThumb();
        var hue = new Canvas { Width = barW, Height = 20 };
        Canvas.SetTop(hueFill, 3);
        hue.Children.Add(hueFill);
        hue.Children.Add(hueThumb);

        // ---- alpha strip ---------------------------------------------------
        var alphaBase = new Rectangle
        {
            Width = barW,
            Height = barH,
            RadiusX = 7,
            RadiusY = 7,
            Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x3F, 0x4B)),
        };
        var alphaGradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0x00, r, g, b), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0xFF, r, g, b), Offset = 1 },
            },
        };
        var alphaFill = new Rectangle { Width = barW, Height = barH, RadiusX = 7, RadiusY = 7, Fill = alphaGradient };
        var alphaThumb = MakeBarThumb();
        var alpha = new Canvas { Width = barW, Height = 20 };
        Canvas.SetTop(alphaBase, 3);
        Canvas.SetTop(alphaFill, 3);
        alpha.Children.Add(alphaBase);
        alpha.Children.Add(alphaFill);
        alpha.Children.Add(alphaThumb);

        // ---- header: preview chip, hex field, Done -------------------------
        var preview = new Border
        {
            Width = 44,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Background = new SolidColorBrush(Unpack(initial)),
        };
        var hexFieldBg = new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x11, 0x18));
        var hexFieldFg = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xEA, 0xF2));
        var hex = new TextBox
        {
            Width = 108,
            MinHeight = 30,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Text = Hex(a, r, g, b),
            Background = hexFieldBg,
            Foreground = hexFieldFg,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 0, 8, 0),
        };
        // The stock TextBox template pulls its colours from theme resources,
        // so override them locally to keep the field dark like the panel.
        hex.Resources["TextControlBackground"] = hexFieldBg;
        hex.Resources["TextControlBackgroundPointerOver"] = hexFieldBg;
        hex.Resources["TextControlBackgroundFocused"] = hexFieldBg;
        hex.Resources["TextControlForeground"] = hexFieldFg;
        hex.Resources["TextControlForegroundPointerOver"] = hexFieldFg;
        hex.Resources["TextControlForegroundFocused"] = hexFieldFg;
        hex.Resources["TextControlBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        hex.Resources["TextControlBorderBrushPointerOver"] = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        hex.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xA6, 0x23));
        var done = new Button
        {
            Content = Limona.Services.Loc.T("Done"),
            MinHeight = 30,
            MinWidth = 64,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xA6, 0x23)),
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x14, 0x17, 0x1F)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(preview);
        Grid.SetColumn(hex, 1);
        header.Children.Add(hex);
        Grid.SetColumn(done, 2);
        header.Children.Add(done);

        // ---- presets --------------------------------------------------------
        // 8 columns x 24 px + 7 gaps x 5 px = 227 px, safely inside the 236 px panel.
        var presets = new Grid { ColumnSpacing = 5, RowSpacing = 5 };
        for (var i = 0; i < 8; i++)
            presets.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var row = 0; row < 2; row++)
            presets.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < PresetColors.Length; i++)
        {
            var presetColor = Unpack(PresetColors[i]);
            var swatch = new Button
            {
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(presetColor),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
            };
            var presetHex = Hex(presetColor.A, presetColor.R, presetColor.G, presetColor.B);
            ToolTipService.SetToolTip(swatch, presetHex);
            LocalizationUi.SetAutomationName(swatch, "Preset {0}", presetHex);
            swatch.Click += (_, _) =>
            {
                a = presetColor.A;
                r = presetColor.R;
                g = presetColor.G;
                b = presetColor.B;
                RgbToHsv(r, g, b, out h, out s, out v);
                Refresh();
            };
            Grid.SetColumn(swatch, i % 8);
            Grid.SetRow(swatch, i / 8);
            presets.Children.Add(swatch);
        }

        // ---- live state ------------------------------------------------------
        void Refresh()
        {
            var current = ColorFromHsv(h, s, v);
            r = current.R;
            g = current.G;
            b = current.B;

            svBase.Fill = new SolidColorBrush(ColorFromHsv(h, 1, 1));
            Canvas.SetLeft(svThumb, Math.Clamp(s * svW - 7, 0, svW - 14));
            Canvas.SetTop(svThumb, Math.Clamp((1 - v) * svH - 7, 0, svH - 14));

            Canvas.SetLeft(hueThumb, Math.Clamp(h / 360.0 * barW - 3, 0, barW - 6));
            Canvas.SetTop(hueThumb, 0);

            alphaGradient.GradientStops[0].Color = Color.FromArgb(0x00, r, g, b);
            alphaGradient.GradientStops[1].Color = Color.FromArgb(0xFF, r, g, b);
            Canvas.SetLeft(alphaThumb, Math.Clamp(a / 255.0 * barW - 3, 0, barW - 6));
            Canvas.SetTop(alphaThumb, 0);

            preview.Background = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            hex.Text = Hex(a, r, g, b);
            onChanged(Pack(a, r, g, b));
        }

        void ApplyHex()
        {
            var text = (hex.Text ?? string.Empty).Trim().TrimStart('#');
            byte pa = a, pr = r, pg = g, pb = b;
            if (text.Length == 6)
            {
                if (byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out var rr) &&
                    byte.TryParse(text[2..4], System.Globalization.NumberStyles.HexNumber, null, out var gg) &&
                    byte.TryParse(text[4..6], System.Globalization.NumberStyles.HexNumber, null, out var bb))
                {
                    pr = rr;
                    pg = gg;
                    pb = bb;
                }
            }
            else if (text.Length == 8)
            {
                if (byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out var aa) &&
                    byte.TryParse(text[2..4], System.Globalization.NumberStyles.HexNumber, null, out var rr) &&
                    byte.TryParse(text[4..6], System.Globalization.NumberStyles.HexNumber, null, out var gg) &&
                    byte.TryParse(text[6..8], System.Globalization.NumberStyles.HexNumber, null, out var bb))
                {
                    pa = aa;
                    pr = rr;
                    pg = gg;
                    pb = bb;
                }
            }
            a = pa;
            r = pr;
            g = pg;
            b = pb;
            RgbToHsv(r, g, b, out h, out s, out v);
            Refresh();
        }

        hex.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                ApplyHex();
                e.Handled = true;
            }
        };
        hex.LostFocus += (_, _) => ApplyHex();

        AttachDrag(sv, p =>
        {
            s = Math.Clamp(p.X / svW, 0, 1);
            v = 1 - Math.Clamp(p.Y / svH, 0, 1);
            Refresh();
        });
        AttachDrag(hue, p =>
        {
            h = Math.Clamp(p.X / barW, 0, 1) * 360.0;
            Refresh();
        });
        AttachDrag(alpha, p =>
        {
            a = (byte)Math.Round(Math.Clamp(p.X / barW, 0, 1) * 255.0);
            Refresh();
        });

        var panel = new StackPanel { Width = 236, Spacing = 10 };
        panel.Children.Add(header);
        panel.Children.Add(sv);
        panel.Children.Add(hue);
        panel.Children.Add(alpha);
        panel.Children.Add(presets);

        var content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x14, 0x17, 0x1F)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Child = panel,
        };

        LocalizationUi.Watch(content);
        var flyout = new Flyout { Content = content, Placement = FlyoutPlacementMode.Bottom };
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        presenter.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 420.0));
        flyout.FlyoutPresenterStyle = presenter;

        done.Click += (_, _) => flyout.Hide();

        Refresh();
        return flyout;
    }

    private static Border MakeBarThumb() => new()
    {
        Width = 6,
        Height = 20,
        CornerRadius = new CornerRadius(3),
        Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x78, 0x00, 0x00, 0x00)),
        BorderThickness = new Thickness(1),
        IsHitTestVisible = false,
    };

    private static void AttachDrag(FrameworkElement area, Action<Point> onPosition)
    {
        area.PointerPressed += (_, e) =>
        {
            area.CapturePointer(e.Pointer);
            onPosition(e.GetCurrentPoint(area).Position);
            e.Handled = true;
        };
        area.PointerMoved += (_, e) =>
        {
            if (area.PointerCaptures.Count > 0) onPosition(e.GetCurrentPoint(area).Position);
        };
        area.PointerReleased += (_, e) => area.ReleasePointerCapture(e.Pointer);
        area.PointerCanceled += (_, _) => area.ReleasePointerCaptures();
    }

    private static Color Unpack(uint argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF));

    private static uint Pack(byte a, byte r, byte g, byte b) =>
        ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;

    private static string Hex(byte a, byte r, byte g, byte b) => $"#{a:X2}{r:X2}{g:X2}{b:X2}";

    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;
        v = max;
        s = max <= 0 ? 0 : delta / max;
        if (delta <= 0)
        {
            h = 0;
            return;
        }
        if (max == rf) h = 60 * (((gf - bf) / delta) % 6);
        else if (max == gf) h = 60 * ((bf - rf) / delta + 2);
        else h = 60 * ((rf - gf) / delta + 4);
        if (h < 0) h += 360;
    }

    private static Color ColorFromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        var m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromArgb(0xFF,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
