using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Limona.Interop;
using Limona.Services;

namespace Limona.Menu;

/// <summary>
/// Modern help window: animated demo, "same for files" explanation and the
/// live list of supported file types. Scrollable body, pinned Close button.
/// </summary>
internal sealed class HelpWindow : Window
{
    private static HelpWindow? s_active;

    public static void Show()
    {
        s_active = ToolWindowCoordinator.Open(() => new HelpWindow());
    }

    private HelpWindow()
    {
        WindowFit.SetTitle(this, "Limona — Help");

        var root = new Border
        {
            Background = Brush(MenuTheme.HubFill),
            Child = BuildLayout(),
        };

        Content = root;
        WindowFit.ApplyFixed(this, root, 560, 820);
        this.Closed += (_, _) => s_active = null;
    }

    private UIElement BuildLayout()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // footer

        grid.Children.Add(BuildHeader());
        Grid.SetRow((FrameworkElement)grid.Children[grid.Children.Count - 1], 0);

        var body = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(20, 16, 20, 16),
        };
        body.Content = BuildBody();
        grid.Children.Add(body);
        Grid.SetRow(body, 1);

        grid.Children.Add(BuildFooter());
        Grid.SetRow((FrameworkElement)grid.Children[grid.Children.Count - 1], 2);

        return grid;
    }

    private UIElement BuildHeader()
    {
        var border = new Border
        {
            Background = Brush("#1AFFFFFF"),
            BorderBrush = Brush("#14FFFFFF"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(22, 18, 22, 16),
        };
        var stack = new StackPanel { Spacing = 2 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(new Border
        {
            Background = Brush(MenuTheme.AccentColor),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "LIMONA",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = Brush("#FF121418"),
            },
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = "Help",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(MenuTheme.LabelColor),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(titleRow);
        stack.Children.Add(new TextBlock
        {
            Text = "How to use the wheel, and every file type Limona understands.",
            FontSize = 12.5,
            Foreground = Brush(MenuTheme.HintColor),
        });
        border.Child = stack;
        return border;
    }

    private UIElement BuildBody()
    {
        var stack = new StackPanel { Spacing = 14 };

        stack.Children.Add(SectionCard(
            "Open the menu in a folder",
            "Hold Ctrl and press the Right mouse button on a folder (or an empty spot inside it). " +
            "The Limona wheel opens right at the cursor with folder actions: customize the icon, " +
            "compress to ZIP, show the size, copy the path and open a terminal here.",
            MakeDemoGif()));

        stack.Children.Add(SectionCard(
            "The same works for files",
            "Press Ctrl + Right-click on any supported file and the wheel adapts automatically: " +
            "the actions change to match the file type — convert, edit, compress and more. " +
            "The result file is written next to the original one.",
            null));

        // Supported types
        var typesCard = MakeCard();
        var typesStack = new StackPanel { Spacing = 10 };
        typesStack.Children.Add(new TextBlock
        {
            Text = "Supported file types",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(MenuTheme.AccentColor),
        });
        typesStack.Children.Add(GroupLine("Folders", "any folder on the disk",
            "icon packs, covers, badges · compress to ZIP · size · copy path · terminal"));

        foreach (var (name, extensions, actions) in MenuCatalog.ExtensionGroups())
        {
            typesStack.Children.Add(GroupLine(name, string.Join("  ", extensions), actions));
        }
        typesStack.Children.Add(new TextBlock
        {
            Text = "Files without an extension or outside these lists are untouched: " +
                   "the wheel simply stays closed for them.",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(MenuTheme.HintColor),
            Margin = new Thickness(0, 4, 0, 0),
        });
        typesCard.Child = typesStack;
        stack.Children.Add(typesCard);

        return stack;
    }

    private UIElement BuildFooter()
    {
        var border = new Border
        {
            Background = Brush("#1AFFFFFF"),
            BorderBrush = Brush("#14FFFFFF"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(22, 12, 22, 14),
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var aboutButton = new Button
        {
            Content = "About",
            MinWidth = 120,
            MinHeight = 36,
            Background = Brush("#1AFFFFFF"),
            Foreground = Brush(MenuTheme.LabelColor),
            HorizontalAlignment = HorizontalAlignment.Right,
            CornerRadius = new CornerRadius(8),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        aboutButton.Click += async (_, _) => await ShowAboutAsync();
        var closeButton = new Button
        {
            Content = "Close",
            MinWidth = 120,
            MinHeight = 36,
            Background = Brush(MenuTheme.AccentColor),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x14, 0x18)),
            HorizontalAlignment = HorizontalAlignment.Right,
            CornerRadius = new CornerRadius(8),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        closeButton.Click += (_, _) => Close();
        buttons.Children.Add(aboutButton);
        buttons.Children.Add(closeButton);
        border.Child = buttons;
        return border;
    }

    private async Task ShowAboutAsync()
    {
        var content = new StackPanel { Spacing = 12, MinWidth = 340 };
        content.Children.Add(new TextBlock
        {
            Text = "Limona",
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = Brush(MenuTheme.AccentColor),
        });

        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var year = DateTime.Now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var versionText = new TextBlock
        {
            FontSize = 13,
            Foreground = Brush(MenuTheme.HintColor),
        };
        LocalizationUi.SetFormattedText(versionText, "Version {0} · © {1} {2}", version, year, "MECATROTEC");
        content.Children.Add(versionText);

        content.Children.Add(new TextBlock
        {
            Text = "A practical toolkit for managing files and folders, with image, audio, and video tools in one place.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(MenuTheme.LabelColor),
        });
        var creatorText = new TextBlock
        {
            Text = "Created by {0}",
            FontSize = 13,
            Foreground = Brush(MenuTheme.LabelColor),
        };
        LocalizationUi.SetFormattedText(creatorText, "Created by {0}", "Mohamed Baroudi");
        content.Children.Add(creatorText);

        var contactLabel = new TextBlock
        {
            Text = "Contact",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(MenuTheme.LabelColor),
            Margin = new Thickness(0, 4, 0, 0),
        };
        content.Children.Add(contactLabel);
        content.Children.Add(new TextBlock
        {
            Text = "Email",
            FontSize = 12,
            Foreground = Brush(MenuTheme.HintColor),
        });
        var email = new HyperlinkButton
        {
            Content = "baroudi.inc@gmail.com",
            NavigateUri = new Uri("mailto:baroudi.inc@gmail.com"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0),
        };
        content.Children.Add(email);
        content.Children.Add(new TextBlock
        {
            Text = "WhatsApp",
            FontSize = 12,
            Foreground = Brush(MenuTheme.HintColor),
        });
        var whatsapp = new HyperlinkButton
        {
            Content = "+212 643 025 834",
            NavigateUri = new Uri("https://wa.me/212643025834"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0),
        };
        content.Children.Add(whatsapp);
        LocalizationUi.Watch(content);

        var dialog = new ContentDialog
        {
            Title = "About Limona",
            Content = content,
            CloseButtonText = Loc.T("Close"),
            XamlRoot = ((FrameworkElement)Content!).XamlRoot,
            RequestedTheme = ElementTheme.Dark,
        };
        void ApplyDialogLanguage()
        {
            dialog.Title = Loc.T("About Limona");
            dialog.CloseButtonText = Loc.T("Close");
        }
        ApplyDialogLanguage();
        Loc.Changed += ApplyDialogLanguage;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            Loc.Changed -= ApplyDialogLanguage;
        }
    }

    private Border MakeCard() => new()
    {
        Background = Brush("#14FFFFFF"),
        BorderBrush = Brush("#1FFFFFFF"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(16, 14, 16, 14),
    };

    private UIElement SectionCard(string title, string paragraph, UIElement? extra)
    {
        var card = MakeCard();
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(MenuTheme.AccentColor),
        });
        stack.Children.Add(new TextBlock
        {
            Text = paragraph,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(MenuTheme.LabelColor),
        });
        if (extra is not null) stack.Children.Add(extra);
        card.Child = stack;
        return card;
    }

    internal static UIElement? MakeDemoGif(double width = 360)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Help", "howto.gif");
            if (!File.Exists(path))
            {
                Dbg.Log("HelpWindow: demo gif missing at " + path);
                return null;
            }
            var bitmap = new BitmapImage(new Uri(path));
            var frame = new Border
            {
                Background = Brush("#0A000000"),
                BorderBrush = Brush("#26FFFFFF"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
                Child = new Image
                {
                    Source = bitmap,
                    Width = width,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FlowDirection = FlowDirection.LeftToRight,
                },
            };
            return frame;
        }
        catch (Exception ex)
        {
            Dbg.Log("HelpWindow gif THREW: " + ex.Message);
            return null;
        }
    }

    /// <summary>One supported-type group: bold name, extension list and actions.</summary>
    private static StackPanel GroupLine(string name, string extensions, string actions)
    {
        var host = new StackPanel { Spacing = 3 };
        host.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(MenuTheme.LabelColor),
        });
        host.Children.Add(new TextBlock
        {
            Text = extensions,
            FontSize = 11.5,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(MenuTheme.AccentColor),
        });
        host.Children.Add(new TextBlock
        {
            Text = actions,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(MenuTheme.HintColor),
        });
        return host;
    }

    private static SolidColorBrush Brush(string hex) => new(ParseColor(hex));

    private static Windows.UI.Color ParseColor(string hex)
    {
        var v = hex.TrimStart('#');
        return Windows.UI.Color.FromArgb(
            Convert.ToByte(v[0..2], 16),
            Convert.ToByte(v[2..4], 16),
            Convert.ToByte(v[4..6], 16),
            Convert.ToByte(v[6..8], 16));
    }
}
