using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Limona.Interop;
using Limona.Services;
using Windows.Foundation;

using MQ = Microsoft.UI.Dispatching;

namespace Limona.Menu;

/// <summary>
/// "Extract Audio" from a video: the user picks MP3 (compressed) or WAV
/// (uncompressed), then the sound track is pulled out via the audio pipeline.
/// </summary>
internal sealed class VideoExtractWindow : Window
{
    private static VideoExtractWindow? s_active;

    private readonly string _sourcePath;
    private readonly string _name;
    private bool _working;

    private TextBlock _sourceInfo = null!;
    private TextBlock _status = null!;
    private TextBlock _progressPct = null!;
    private Border _progressHost = null!;
    private ColumnDefinition _fillColumn = null!;
    private ColumnDefinition _emptyColumn = null!;
    private Button _mp3Button = null!;
    private Button _wavButton = null!;
    private Button _cancelButton = null!;

    public static void ShowForFile(string sourcePath)
    {
        s_active = ToolWindowCoordinator.Open(() => new VideoExtractWindow(sourcePath));
    }

    private VideoExtractWindow(string sourcePath)
    {
        _sourcePath = sourcePath;
        _name = Path.GetFileName(sourcePath);

        WindowFit.SetTitle(this, "Extract Audio");
        _sourceInfo = MakeHintText();
        _status = MakeStatusText();
        _progressPct = new TextBlock
        {
            Text = "0 %",
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 42,
            FontSize = 12,
            Foreground = Brush(MenuTheme.HintColor),
        };
        _mp3Button = MakeFormatButton("MP3", MenuTheme.AccentColor,
            "Compressed · small file · works everywhere");
        _wavButton = MakeFormatButton("WAV", "#FF7CCD8A",
            "Uncompressed · studio quality · big file");
        _cancelButton = new Button { Content = "Cancel", MinWidth = 96 };

        _mp3Button.Click += (_, _) => _ = ExtractAsync(".mp3", new AudioConvertSettings(
            BitrateKbps: 192, SampleRate: 48000, BitsPerSample: 16, Channels: 0), "MP3");
        _wavButton.Click += (_, _) => _ = ExtractAsync(".wav", new AudioConvertSettings(
            BitrateKbps: 0, SampleRate: 48000, BitsPerSample: 16, Channels: 0), "WAV");
        _cancelButton.Click += (_, _) => Close();

        Content = BuildLayout();
        WindowFit.Apply(this, (FrameworkElement)Content!);

        _ = PrepareAsync();
    }

    private async System.Threading.Tasks.Task PrepareAsync()
    {
        LocalizationUi.SetFormattedText(_sourceInfo, "Source: {0} — reading info...", _name);
        try
        {
            var bytes = new FileInfo(_sourcePath).Length;
            var duration = await VideoConverter.GetDurationAsync(_sourcePath);
            var sizeText = FormatKb(bytes);
            if (duration > TimeSpan.Zero)
                LocalizationUi.SetFormattedText(_sourceInfo,
                    "Source: {0} — {1} — {2} video", _name, sizeText, FormatTime(duration));
            else
                LocalizationUi.SetFormattedText(_sourceInfo,
                    "Source: {0} — {1} — video", _name, sizeText);
        }
        catch (Exception ex)
        {
            Dbg.Log("VideoExtractWindow prepare THREW: " + ex);
            LocalizationUi.SetFormattedText(_sourceInfo, "Source: {0}", _name);
        }
    }

    private async System.Threading.Tasks.Task ExtractAsync(string targetExt, AudioConvertSettings settings, string formatName)
    {
        if (_working) return;
        _working = true;
        _mp3Button.IsEnabled = false;
        _wavButton.IsEnabled = false;
        _progressHost.Visibility = Visibility.Visible;
        UpdateProgress(0);
        SetStatus("Extracting {0} audio…", MenuTheme.AccentColor, formatName);

        try
        {
            var step = new Progress<double>(UpdateProgress);
            var resultPath = await System.Threading.Tasks.Task.Run(
                () => AudioConverter.ConvertAsync(_sourcePath, targetExt, settings, step));
            UpdateProgress(1.0);

            var savedName = Path.GetFileName(resultPath);
            var savedBytes = new FileInfo(resultPath).Length;
            SetStatus("Saved: {0} · {1}", "#FF9BE29B", savedName, FormatKb(savedBytes));
            try { Win32.MessageBeep(0x00000040); } catch { }

            var closeTimer = MQ.DispatcherQueue.GetForCurrentThread().CreateTimer();
            closeTimer.Interval = TimeSpan.FromMilliseconds(1600);
            closeTimer.IsRepeating = false;
            closeTimer.Tick += (_, _) => { closeTimer.Stop(); Close(); };
            closeTimer.Start();
        }
        catch (Exception ex)
        {
            Dbg.Log("VideoExtractWindow convert THREW: " + ex);
            SetStatus("Extraction failed: {0}", "#FFED6A5E", Shorten(ex.Message));
            _progressHost.Visibility = Visibility.Collapsed;
            _mp3Button.IsEnabled = true;
            _wavButton.IsEnabled = true;
            _working = false;
        }
    }

    private void UpdateProgress(double fraction)
    {
        if (_fillColumn.Width.GridUnitType != GridUnitType.Star) return;
        var pct = Math.Clamp(fraction, 0.0, 1.0) * 100.0;
        _fillColumn.Width = new GridLength(Math.Max(pct, pct > 0 ? 1.5 : 0.001), GridUnitType.Star);
        _emptyColumn.Width = new GridLength(Math.Max(100 - pct, 0.001), GridUnitType.Star);
        _progressPct.Text = ((int)Math.Round(pct)) + " %";
    }

    private UIElement BuildLayout()
    {
        var root = new Border
        {
            Background = Brush(MenuTheme.HubFill),
            Padding = new Thickness(20),
        };

        var grid = new Grid(); // 0 info, 1 format choices, 2 progress, 3 status, 4 cancel
        for (var i = 0; i < 5; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        grid.RowSpacing = 14;

        _sourceInfo.FontSize = 12;
        Grid.SetRow(_sourceInfo, 0);
        grid.Children.Add(_sourceInfo);

        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var mp3Group = MakeFormatGroup("MP3", "Compressed · small file", _mp3Button);
        var wavGroup = MakeFormatGroup("WAV", "Uncompressed · big file", _wavButton);
        choices.Children.Add(mp3Group);
        choices.Children.Add(wavGroup);
        Grid.SetRow(choices, 1);
        grid.Children.Add(choices);

        var progressRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _progressHost = MakeProgressHost();
        progressRow.Children.Add(_progressHost);
        progressRow.Children.Add(_progressPct);
        _progressHost.Visibility = Visibility.Collapsed;
        Grid.SetRow(progressRow, 2);
        grid.Children.Add(progressRow);

        Grid.SetRow(_status, 3);
        grid.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
        };
        buttons.Children.Add(_cancelButton);
        Grid.SetRow(buttons, 4);
        grid.Children.Add(buttons);

        root.Child = grid;
        return root;
    }

    private static StackPanel MakeFormatGroup(string name, string hint, Button button)
    {
        return new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Width = 170,
            Children =
            {
                new TextBlock
                {
                    Text = name,
                    FontSize = 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = Brush(name == "MP3" ? MenuTheme.AccentColor : "#FF7CCD8A"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = hint,
                    FontSize = 11,
                    Foreground = Brush(MenuTheme.HintColor),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                },
                button,
            },
        };
    }

    private static Button MakeFormatButton(string name, string accentHex, string tooltip)
    {
        var button = new Button
        {
            Content = Limona.Services.Loc.F("Extract {0}", name),
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 150,
            MinHeight = 38,
            CornerRadius = new CornerRadius(8),
            Background = Brush(accentHex),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x14, 0x18)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private Border MakeProgressHost()
    {
        _fillColumn = new ColumnDefinition { Width = new GridLength(0.001, GridUnitType.Star) };
        _emptyColumn = new ColumnDefinition { Width = new GridLength(99.999, GridUnitType.Star) };
        var fill = new Border
        {
            Background = Brush(MenuTheme.AccentColor),
            CornerRadius = new CornerRadius(4),
        };
        var fillGrid = new Grid();
        fillGrid.ColumnDefinitions.Add(_fillColumn);
        fillGrid.ColumnDefinitions.Add(_emptyColumn);
        Grid.SetColumn(fill, 0);
        fillGrid.Children.Add(fill);

        _progressPct.Text = "0 %";
        return new Border
        {
            Width = 300,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = Brush("#FF2A2F3E"),
            Padding = new Thickness(2),
            Child = new Grid { Children = { fillGrid } },
        };
    }

    private static TextBlock MakeHintText() => new()
    {
        FontSize = 11,
        Foreground = Brush(MenuTheme.HintColor),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static TextBlock MakeStatusText() => new()
    {
        FontSize = 12,
        Visibility = Visibility.Collapsed,
    };

    private void SetStatus(string message, string colorHex, params object?[] args)
    {
        if (args.Length == 0) LocalizationUi.SetText(_status, message);
        else LocalizationUi.SetFormattedText(_status, message, args);
        _status.Foreground = Brush(colorHex);
        _status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours:0}:{time.Minutes:00}:{time.Seconds:00}"
        : $"{time.Minutes:0}:{time.Seconds:00}";

    private static string FormatKb(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:F2} MB"
        : $"{bytes / 1024.0:F1} KB";

    private static string Shorten(string message)
    {
        var first = message.Split('\n')[0];
        return first.Length > 64 ? first[..64] + "…" : first;
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
