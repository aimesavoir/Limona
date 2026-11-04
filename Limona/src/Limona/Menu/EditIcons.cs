using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;

namespace Limona.Menu;

/// <summary>
/// Loads PNG icons from Assets/EditIcons/ (bundled) or %LOCALAPPDATA%\Limona\EditIcons/.
/// Falls back to a text glyph if the file is missing.
/// </summary>
public static class EditIcons
{
    public static string[] SearchDirs =>
    [
        Path.Combine(AppContext.BaseDirectory, "Assets", "EditIcons"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Limona", "EditIcons"),
    ];

    public static string? Resolve(string name)
    {
        foreach (var dir in SearchDirs)
        {
            var path = Path.Combine(dir, name + ".png");
            if (File.Exists(path)) return path;
            path = Path.Combine(dir, name + ".jpg");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static UIElement Load(string name, int size = 20, string fallbackGlyph = "")
    {
        var path = Resolve(name);
        if (path is not null)
        {
            try
            {
                var img = new Image
                {
                    Source = new BitmapImage(new Uri(path)),
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Uniform,
                };
                return img;
            }
            catch { }
        }
        // Fallback: text glyph
        return new TextBlock
        {
            Text = string.IsNullOrEmpty(fallbackGlyph) ? "?" : fallbackGlyph,
            FontSize = size - 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(MenuTheme.LabelColor),
        };
    }

    private static SolidColorBrush Brush(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        var a = (byte)((value >> 24) & 0xFF);
        var r = (byte)((value >> 16) & 0xFF);
        var g = (byte)((value >> 8) & 0xFF);
        var b = (byte)(value & 0xFF);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
    }
}
