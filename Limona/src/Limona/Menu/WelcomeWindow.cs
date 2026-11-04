using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Limona.Services;

namespace Limona.Menu;

internal sealed class WelcomeWindow : Window
{
    private static WelcomeWindow? s_active;

    public static void Show()
    {
        s_active = ToolWindowCoordinator.Open(() => new WelcomeWindow());
    }

    private WelcomeWindow()
    {
        WindowFit.SetTitle(this, "Welcome to Limona");

        var dontShowAgain = new CheckBox
        {
            Content = "Don't show this again",
            Foreground = Brush(MenuTheme.LabelColor),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        dontShowAgain.Checked += (_, _) => AppSettings.SetShowWelcomeAtStartup(false);
        dontShowAgain.Unchecked += (_, _) => AppSettings.SetShowWelcomeAtStartup(true);

        var closeButton = new Button
        {
            Content = "Got it",
            MinWidth = 110,
            MinHeight = 36,
            Background = Brush(MenuTheme.AccentColor),
            Foreground = Brush("#FF121418"),
            CornerRadius = new CornerRadius(8),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        closeButton.Click += (_, _) => Close();

        var content = new Border
        {
            Background = Brush(MenuTheme.HubFill),
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Welcome to Limona",
                        FontSize = 22,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = Brush(MenuTheme.AccentColor),
                    },
                    new TextBlock
                    {
                        Text = "Hold Ctrl and right-click a supported file or folder to open the action wheel. Hover an action to preview it, then left-click to run it. Press Esc to close.",
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush(MenuTheme.LabelColor),
                    },
                    HelpWindow.MakeDemoGif(350) ?? new TextBlock
                    {
                        Text = "Ctrl + Right-click to open the Limona action wheel.",
                        FontSize = 12,
                        Foreground = Brush(MenuTheme.HintColor),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    dontShowAgain,
                    closeButton,
                },
            },
        };

        Content = content;
        WindowFit.ApplyFixed(this, content, 420, 510);
        Closed += (_, _) => s_active = null;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value));
    }
}
