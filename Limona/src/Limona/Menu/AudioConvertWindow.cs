using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Limona.Interop;
using Limona.Services;
using Windows.Foundation;
using Windows.Graphics;

using MQ = Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace Limona.Menu;

/// <summary>
/// Settings window opened by an audio "Convert to X" slice. The target format
/// decides which controls are shown; pressing Convert runs the Windows Media
/// Foundation (or Concentus / OGG) pipeline from AudioConverter.
/// </summary>
internal sealed class AudioConvertWindow : Window
{
    private static AudioConvertWindow? s_active;

    private readonly string _sourcePath;
    private readonly string _targetExt; // ".mp3" / ".wav" / ".m4a" / ".ogg"
    private readonly string _formatName;
    private bool _converting;

    private ComboBox _bitrateBox = null!;      // MP3 / M4A / OGG
    private ComboBox _sampleRateBox = null!;   // WAV
    private ComboBox _depthBox = null!;        // WAV
    private ComboBox _channelsBox = null!;     // WAV / OGG
    private Grid _optionalRows = null!;
    private TextBlock _sourceInfo = null!;
    private TextBlock _status = null!;
    private TextBlock _progressPct = null!;
    private Button _convertButton = null!;
    private Button _cancelButton = null!;
    private Border _progressHost = null!;

    /// <summary>Opens the conversion settings window (single instance at a time).</summary>
    public static void ShowForFile(string sourcePath, string targetExt, string formatName)
    {
        s_active = ToolWindowCoordinator.Open(
            () => new AudioConvertWindow(sourcePath, targetExt, formatName));
    }

    private AudioConvertWindow(string sourcePath, string targetExt, string formatName)
    {
        _sourcePath = sourcePath;
        _targetExt = targetExt;
        _formatName = formatName;

        WindowFit.SetTitle(this, "Convert to " + formatName);
        _optionalRows = BuildSettingsRows();
        _sourceInfo = MakeHintText();
        _status = MakeStatusText();
        _convertButton = MakeAccentButton("Convert");
        _cancelButton = MakeCancelButton();
        _progressPct = new TextBlock
        {
            Text = "0 %",
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 42,
            FontSize = 12,
            Foreground = Brush(MenuTheme.HintColor),
        };
        _convertButton.Click += (_, _) => _ = OnConvertClicked();
        _cancelButton.Click += (_, _) => Close();

        Content = BuildLayout();
        WindowFit.Apply(this, (FrameworkElement)Content!);

        _ = PrepareAsync();
    }

    // ------------------------------------------------------------------
    // Preparation: source info line
    // ------------------------------------------------------------------

    private async System.Threading.Tasks.Task PrepareAsync()
    {
        var name = System.IO.Path.GetFileName(_sourcePath);
        LocalizationUi.SetFormattedText(_sourceInfo, "Source: {0} — reading info...", name);
        try
        {
            var (duration, bytes) = await System.Threading.Tasks.Task.Run(async () =>
            {
                var duration = await AudioConverter.GetDurationAsync(_sourcePath);
                var bytes = new System.IO.FileInfo(_sourcePath).Length;
                return (duration, bytes);
            });
            LocalizationUi.SetFormattedText(_sourceInfo,
                "Source: {0} — {1} — {2} — convert to {3}",
                name, FormatTime(duration), FormatKb(bytes), _formatName);
        }
        catch (Exception ex)
        {
            Dbg.Log("AudioConvertWindow prepare THREW: " + ex);
            LocalizationUi.SetFormattedText(_sourceInfo, "Source: {0}", name);
        }
    }

    // ------------------------------------------------------------------
    // Settings rows per target format
    // ------------------------------------------------------------------

    private Grid BuildSettingsRows()
    {
        var grid = new Grid();
        var isWav = _targetExt == ".wav";
        var bitrateLabel = _targetExt == ".ogg" ? "Opus bitrate" : "Bitrate";

        for (var i = 0; i < 2; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var row0 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };

        if (isWav)
        {
            _sampleRateBox = MakeCombo(new[] { "44100 Hz", "48000 Hz" }, 1);
            _depthBox = MakeCombo(new[] { "16-bit", "24-bit", "32-bit float" }, 0);
            row0.Children.Add(SettingLabel("Sample rate"));
            row0.Children.Add(_sampleRateBox);
            row0.Children.Add(SettingLabel("Bit depth"));
            row0.Children.Add(_depthBox);
        }
        else
        {
            _bitrateBox = _targetExt == ".ogg"
                ? MakeCombo(new[] { "64 kbps", "96 kbps", "128 kbps", "192 kbps", "256 kbps" }, 2)
                : MakeCombo(new[] { "64 kbps", "96 kbps", "128 kbps", "192 kbps", "256 kbps", "320 kbps" }, 3);
            row0.Children.Add(SettingLabel(bitrateLabel));
            row0.Children.Add(_bitrateBox);
        }
        Grid.SetRow(row0, 0);
        grid.Children.Add(row0);

        // Channels row (WAV + OGG only); invisible for MP3 / M4A which keep the
        // source channel layout.
        var row1 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        _channelsBox = MakeCombo(new[] { "Keep source", "Mono", "Stereo" }, 0);
        row1.Children.Add(SettingLabel("Channels"));
        row1.Children.Add(_channelsBox);
        Grid.SetRow(row1, 1);
        grid.Children.Add(row1);

        if (!isWav && _targetExt != ".ogg")
        {
            row1.Visibility = Visibility.Collapsed;
        }

        return grid;
    }

    private static TextBlock SettingLabel(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brush(MenuTheme.HintColor),
        FontSize = 12,
    };

    private static ComboBox MakeCombo(string[] items, int selectedIndex)
    {
        var box = new ComboBox { MinWidth = 130 };
        foreach (var item in items) box.Items.Add(item);
        box.SelectedIndex = selectedIndex;
        return box;
    }

    // ------------------------------------------------------------------
    // Convert
    // ------------------------------------------------------------------

    private int ParsedBitrateKbps() => int.TryParse(
        (_bitrateBox.SelectedItem as string ?? "").Split(' ')[0],
        System.Globalization.CultureInfo.InvariantCulture, out var kbps) ? kbps : 192;

    private AudioConvertSettings BuildSettings() => new(
        BitrateKbps: _bitrateBox is null ? 0 : ParsedBitrateKbps(), // bitrate combo only exists for MP3/M4A/OGG
        SampleRate: uint.Parse((_sampleRateBox?.SelectedItem as string ?? "48000 Hz").Split(' ')[0],
            System.Globalization.CultureInfo.InvariantCulture),
        BitsPerSample: (_depthBox?.SelectedItem as string) switch
        {
            "24-bit" => 24u,
            "32-bit float" => 32u,
            _ => 16u,
        },
        Channels: (_channelsBox?.SelectedItem as string) switch
        {
            "Mono" => 1,
            "Stereo" => 2,
            _ => 0,
        });

    private async System.Threading.Tasks.Task OnConvertClicked()
    {
        if (_converting) return;
        _converting = true;
        _convertButton.IsEnabled = false;
        ShowProgress(0);

        try
        {
            var settings = BuildSettings();
            SetStatus("Converting to {0}…", MenuTheme.AccentColor, _formatName);
            var step = new Progress<double>(UpdateProgress);
            var resultPath = await System.Threading.Tasks.Task.Run(
                () => AudioConverter.ConvertAsync(_sourcePath, _targetExt, settings, step));
            UpdateProgress(1.0);

            var savedName = System.IO.Path.GetFileName(resultPath);
            var savedBytes = new System.IO.FileInfo(resultPath).Length;
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
            Dbg.Log("AudioConvertWindow convert THREW: " + ex);
            SetStatus("Convert failed: {0}", "#FFED6A5E", Shorten(ex.Message));
            _progressHost.Visibility = Visibility.Collapsed;
            _convertButton.IsEnabled = true;
            _converting = false;
        }
    }

    // ------------------------------------------------------------------
    // Progress bar (hand-built — WinUI ProgressBar needs theme resources
    // this code-built unpackaged app does not provide)
    // ------------------------------------------------------------------

    private ColumnDefinition _fillColumn = null!;
    private ColumnDefinition _emptyColumn = null!;

    private void ShowProgress(double fraction)
    {
        if (_fillColumn.Width.GridUnitType != GridUnitType.Star) return; // not built yet
        _progressHost.Visibility = Visibility.Visible;
        UpdateProgress(fraction);
    }

    private void UpdateProgress(double fraction)
    {
        var pct = Math.Clamp(fraction, 0.0, 1.0) * 100.0;
        _fillColumn.Width = new GridLength(Math.Max(pct, pct > 0 ? 1.5 : 0.001), GridUnitType.Star);
        _emptyColumn.Width = new GridLength(Math.Max(100 - pct, 0.001), GridUnitType.Star);
        _progressPct.Text = ((int)Math.Round(pct)) + " %";
    }

    // ------------------------------------------------------------------
    // UI construction (code-built, matching MenuTheme)
    // ------------------------------------------------------------------

    private UIElement BuildLayout()
    {
        var root = new Border
        {
            Background = Brush(MenuTheme.HubFill),
            Padding = new Thickness(20),
        };

        var grid = new Grid(); // rows: 0 source-info, 1 settings, 2 progress, 3 status, 4 buttons
        for (var i = 0; i < 5; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        grid.RowSpacing = 14;

        // 0: source info
        _sourceInfo.FontSize = 12;
        Grid.SetRow(_sourceInfo, 0);
        grid.Children.Add(_sourceInfo);

        // 1: settings rows
        Grid.SetRow(_optionalRows, 1);
        grid.Children.Add(_optionalRows);

        // 2: progress bar (hidden until converting)
        var progressRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        _progressHost = MakeProgressHost();
        progressRow.Children.Add(_progressHost);
        progressRow.Children.Add(_progressPct);
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
        buttons.Children.Add(_convertButton);
        Grid.SetRow(buttons, 4);
        grid.Children.Add(buttons);

        root.Child = grid;
        return root;
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
            Child = new Grid
            {
                Children = { fillGrid },
            },
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

    private Button MakeAccentButton(string text) => new()
    {
        Content = text,
        MinWidth = 96,
        Background = Brush(MenuTheme.AccentColor),
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x14, 0x18)),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
    };

    private Button MakeCancelButton() => new()
    {
        Content = "Cancel",
        MinWidth = 96,
    };

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

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
