using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Limona.Interop;
using Limona.Services;
using Windows.Foundation;
using Windows.Graphics;

using MQ = Microsoft.UI.Dispatching;

namespace Limona.Menu;

/// <summary>
/// Conversion settings window for Video: supports MP4, MOV, WMV, GIF.
/// Full quality ladder (144p–4K), progress bar, tooltips on all labels,
/// fixed-safe layout preventing buttons from being pushed out of the window.
/// Uses FfmpegService (FFmpeg) for universal format and codec support.
/// </summary>
internal sealed class VideoConvertWindow : Window
{
    private static VideoConvertWindow? s_active;

    private readonly string _sourcePath;
    private string _targetFormat;
    private bool _converting;

    private ComboBox _formatBox = null!;
    private ComboBox _qualityBox = null!;
    private TextBlock _sourceInfo = null!;
    private TextBlock _status = null!;
    private TextBlock _progressPct = null!;
    private Button _convertButton = null!;
    private Button _cancelButton = null!;
    private Border _progressHost = null!;
    private ColumnDefinition _fillColumn = null!;
    private ColumnDefinition _emptyColumn = null!;

    private static readonly string[] s_supportedFormats = ["MP4", "MOV", "WMV", "GIF"];

    public static void ShowForFile(string sourcePath, string targetFormat = "MP4")
    {
        s_active = ToolWindowCoordinator.Open(() => new VideoConvertWindow(sourcePath, targetFormat));
    }

    private VideoConvertWindow(string sourcePath, string targetFormat)
    {
        _sourcePath = sourcePath;
        _targetFormat = NormalizeFormat(targetFormat);

        WindowFit.SetTitle(this, "Convert to " + _targetFormat);

        _formatBox   = MakeFormatCombo(_targetFormat);
        _qualityBox  = MakeQualityCombo(_targetFormat);
        _sourceInfo  = MakeWrapText(11, MenuTheme.HintColor);
        _status      = MakeWrapText(12, MenuTheme.HintColor);
        _status.MinHeight = 36; // reserve 2 lines of status text to guarantee zero layout shift
        _status.Opacity = 0;    // invisible until conversion or message appears

        _convertButton = MakeAccentButton($"Convert to {_targetFormat}");
        LocalizationUi.SetContent(_convertButton, "Convert to {0}", _targetFormat);
        _cancelButton  = MakeCancelButton();
        _progressPct = new TextBlock
        {
            Text = "0 %",
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth  = 46,
            MinHeight = 18,   // reserve row height even when text is empty
            FontSize  = 12,
            Foreground = Brush(MenuTheme.HintColor),
            Opacity   = 0,    // hidden until conversion starts
        };

        _formatBox.SelectionChanged += (_, _) =>
        {
            if (_formatBox.SelectedItem is ComboBoxItem item && item.Tag is string fmt)
            {
                _targetFormat = fmt;
                WindowFit.SetTitle(this, "Convert to " + _targetFormat);
                LocalizationUi.SetContent(_convertButton, "Convert to {0}", _targetFormat);
                UpdateSourceInfoHint();
            }
        };

        _convertButton.Click += (_, _) => _ = OnConvertClicked();
        _cancelButton.Click  += (_, _) => Close();

        Content = BuildLayout();
        // Sized generously (540x350 min) so progress, status, and buttons always fit comfortably
        WindowFit.Apply(this, (FrameworkElement)Content!);

        _ = PrepareAsync();
    }

    private static string NormalizeFormat(string fmt)
    {
        var upper = (fmt ?? "MP4").Trim().ToUpperInvariant();
        return upper is "MOV" or "WMV" or "GIF" ? upper : "MP4";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Async init
    // ─────────────────────────────────────────────────────────────────────────

    private long _sourceBytes;

    private async System.Threading.Tasks.Task PrepareAsync()
    {
        var name = Path.GetFileName(_sourcePath);
        if (!File.Exists(_sourcePath) || new FileInfo(_sourcePath).Length == 0)
        {
            SetSourceInfo("Source: {0} — ⚠️ File is empty (0 bytes)", name);
            SetStatus("Cannot convert: source file has 0 bytes.", "#FFED6A5E");
            _convertButton.IsEnabled = false;
            return;
        }

        SetSourceInfo("Source: {0} — reading info…", name);
        try
        {
            // Ensure FFmpeg is ready (may download on first run)
            await FfmpegService.EnsureReadyAsync(msg =>
                _sourceInfo.DispatcherQueue.TryEnqueue(() =>
                    SetSourceInfo("Source: {0} — {1}", name, msg)));

            _sourceBytes = await System.Threading.Tasks.Task.Run(async () =>
            {
                await VideoConverter.GetDurationAsync(_sourcePath);
                return new FileInfo(_sourcePath).Length;
            });

            UpdateSourceInfoHint();
        }
        catch (Exception ex)
        {
            Dbg.Log("VideoConvertWindow prepare THREW: " + ex);
            SetSourceInfo("Source: {0}", name);
        }
    }

    private void UpdateSourceInfoHint()
    {
        var name = Path.GetFileName(_sourcePath);
        var sizeStr = _sourceBytes > 0 ? FormatSize(_sourceBytes) : "";
        var codecHint = _targetFormat switch
        {
            "MOV" => "QuickTime · H.264 + AAC",
            "WMV" => "Windows Media Video · WMV2",
            "GIF" => "Animated GIF · 15 fps palette",
            _     => "H.264 + AAC MP4",
        };

        if (string.IsNullOrEmpty(sizeStr))
            SetSourceInfo("Source: {0} — target: {1}", name, LocalizationUi.Localized(codecHint));
        else
            SetSourceInfo("Source: {0} — {1} — target: {2}",
                name, sizeStr, LocalizationUi.Localized(codecHint));
    }

    private void SetSourceInfo(string format, params object?[] args)
    {
        if (args.Length == 0) LocalizationUi.SetText(_sourceInfo, format);
        else LocalizationUi.SetFormattedText(_sourceInfo, format, args);
        ToolTipService.SetToolTip(_sourceInfo, _sourceInfo.Text);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Convert
    // ─────────────────────────────────────────────────────────────────────────

    private VideoConverter.Mp4Quality SelectedQuality() =>
        (VideoConverter.Mp4Quality)(_qualityBox.SelectedIndex < 0 ? 4 : _qualityBox.SelectedIndex);

    private async System.Threading.Tasks.Task OnConvertClicked()
    {
        if (_converting) return;

        if (!File.Exists(_sourcePath) || new FileInfo(_sourcePath).Length == 0)
        {
            SetStatus("Cannot convert: source file has 0 bytes.", "#FFED6A5E");
            return;
        }

        _converting = true;
        _convertButton.IsEnabled = false;
        _formatBox.IsEnabled = false;
        _qualityBox.IsEnabled = false;
        _progressHost.Opacity = 1;   // reveal, space was already reserved
        _progressPct.Opacity  = 1;
        UpdateProgress(0);

        try
        {
            var quality = SelectedQuality();
            SetStatus("Converting to {0}…", MenuTheme.AccentColor, _targetFormat);
            var step = new Progress<double>(UpdateProgress);

            var resultPath = await VideoConverter.ConvertVideoAsync(_sourcePath, _targetFormat, quality, step);
            UpdateProgress(1.0);

            var savedBytes = new FileInfo(resultPath).Length;
            var savedName  = Path.GetFileName(resultPath);
            SetStatus("✓  Saved: {0} · {1}", "#FF9BE29B", savedName, FormatSize(savedBytes));
            try { Win32.MessageBeep(0x00000040); } catch { }

            var timer = MQ.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval   = TimeSpan.FromMilliseconds(2000);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => { timer.Stop(); Close(); };
            timer.Start();
        }
        catch (Exception ex)
        {
            Dbg.Log("VideoConvertWindow convert THREW: " + ex);
            string userMsg;
            if (ex.Message.Contains("-1094995529") || ex.Message.Contains("moov atom not found") || ex.Message.Contains("Invalid data found"))
            {
                userMsg = "Video file is corrupt or incomplete (missing moov atom / invalid header).";
            }
            else if (ex.Message.Contains("No such file"))
            {
                userMsg = "Video file not found.";
            }
            else
            {
                userMsg = ex.Message.Split('\n')[0];
            }
            SetStatus("✗  Conversion failed: {0}", "#FFED6A5E", userMsg);
            _progressHost.Opacity = 0;
            _progressPct.Opacity = 0;
            _convertButton.IsEnabled = true;
            _formatBox.IsEnabled = true;
            _qualityBox.IsEnabled = true;
            _converting = false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Progress
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateProgress(double fraction)
    {
        if (_fillColumn.Width.GridUnitType != GridUnitType.Star) return;
        var pct = Math.Clamp(fraction, 0.0, 1.0) * 100.0;
        _fillColumn.Width  = new GridLength(Math.Max(pct, pct > 0 ? 1.5 : 0.001), GridUnitType.Star);
        _emptyColumn.Width = new GridLength(Math.Max(100 - pct, 0.001), GridUnitType.Star);
        _progressPct.Text  = ((int)Math.Round(pct)) + " %";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Layout
    // ─────────────────────────────────────────────────────────────────────────

    private UIElement BuildLayout()
    {
        var root = new Border
        {
            Background = Brush(MenuTheme.HubFill),
            Padding    = new Thickness(24),
            MinWidth   = 540,
            MinHeight  = 350,   // generous height so progress, status, and buttons never clip
        };

        // 2-row grid: Row 0 is flexible content (*), Row 1 is anchored buttons (Auto)
        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var contentStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing     = 12,
        };

        // 1. Source file info (wraps, tooltip)
        contentStack.Children.Add(_sourceInfo);

        // 2. Thin separator
        var sep = new Border
        {
            Height     = 1,
            Background = Brush("#22FFFFFF"),
            Margin     = new Thickness(0, 2, 0, 2),
        };
        contentStack.Children.Add(sep);

        // 3. Settings row: Format picker + Quality ladder picker
        var settingsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 16,
        };

        // Format selector
        var formatPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var formatLabel = new TextBlock
        {
            Text = "Format",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize   = 12,
            Foreground = Brush(MenuTheme.HintColor),
        };
        ToolTipService.SetToolTip(formatLabel, "Choose output video container: MP4, MOV, WMV, or animated GIF.");
        formatPanel.Children.Add(formatLabel);
        formatPanel.Children.Add(_formatBox);
        settingsRow.Children.Add(formatPanel);

        // Quality selector
        var qualityPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var qualityLabel  = new TextBlock
        {
            Text = "Quality",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize   = 12,
            Foreground = Brush(MenuTheme.HintColor),
        };
        ToolTipService.SetToolTip(qualityLabel,
            "Choose resolution and target bitrate.\n" +
            "Higher quality = larger file size.\n" +
            "Source is never upscaled beyond its original resolution.");
        qualityPanel.Children.Add(qualityLabel);
        qualityPanel.Children.Add(_qualityBox);
        settingsRow.Children.Add(qualityPanel);

        contentStack.Children.Add(settingsRow);

        // 4. Progress bar + percentage (always present, reserved space via Opacity)
        var progressRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _progressHost = MakeProgressHost();
        _progressHost.Opacity = 0;   // reserve space but invisible until conversion starts
        progressRow.Children.Add(_progressHost);
        progressRow.Children.Add(_progressPct);
        contentStack.Children.Add(progressRow);

        // 5. Status text (wraps, tooltip updated dynamically, 2 lines reserved)
        contentStack.Children.Add(_status);

        Grid.SetRow(contentStack, 0);
        mainGrid.Children.Add(contentStack);

        // 6. Action buttons anchored at the bottom
        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 10,
            Margin              = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(_cancelButton);
        buttons.Children.Add(_convertButton);

        Grid.SetRow(buttons, 1);
        mainGrid.Children.Add(buttons);

        root.Child = mainGrid;
        return root;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Factory helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static ComboBox MakeFormatCombo(string initialFormat)
    {
        var box = new ComboBox { MinWidth = 100 };
        int selectIdx = 0;
        for (var i = 0; i < s_supportedFormats.Length; i++)
        {
            var fmt = s_supportedFormats[i];
            var item = new ComboBoxItem
            {
                Content = fmt,
                Tag     = fmt,
            };
            var hint = fmt switch
            {
                "MP4" => "Standard MP4 (H.264 + AAC) · playable anywhere",
                "MOV" => "QuickTime movie (.mov) · native on macOS/iOS",
                "WMV" => "Windows Media Video (.wmv) · Windows native",
                "GIF" => "Animated GIF image · loops continuously",
                _     => fmt,
            };
            ToolTipService.SetToolTip(item, hint);
            box.Items.Add(item);

            if (string.Equals(fmt, initialFormat, StringComparison.OrdinalIgnoreCase))
                selectIdx = i;
        }
        box.SelectedIndex = selectIdx;
        return box;
    }

    private static ComboBox MakeQualityCombo(string targetFormat)
    {
        var box = new ComboBox { MinWidth = 260 };
        foreach (var q in VideoConverter.AllQualities)
        {
            var item = new ComboBoxItem
            {
                Content = VideoConverter.QualityLabel(q),
                Tag     = q,
            };
            ToolTipService.SetToolTip(item, VideoConverter.QualityTooltip(q));
            box.Items.Add(item);
        }
        // Default: 720p (index 4) for video, or 480p (index 3) for GIF
        box.SelectedIndex = string.Equals(targetFormat, "GIF", StringComparison.OrdinalIgnoreCase) ? 3 : 4;
        return box;
    }

    private Border MakeProgressHost()
    {
        _fillColumn  = new ColumnDefinition { Width = new GridLength(0.001, GridUnitType.Star) };
        _emptyColumn = new ColumnDefinition { Width = new GridLength(99.999, GridUnitType.Star) };
        var fill = new Border
        {
            Background   = Brush(MenuTheme.AccentColor),
            CornerRadius = new CornerRadius(4),
        };
        var fillGrid = new Grid();
        fillGrid.ColumnDefinitions.Add(_fillColumn);
        fillGrid.ColumnDefinitions.Add(_emptyColumn);
        Grid.SetColumn(fill, 0);
        fillGrid.Children.Add(fill);

        return new Border
        {
            Width        = 380,
            Height       = 12,
            CornerRadius = new CornerRadius(6),
            Background   = Brush("#FF2A2F3E"),
            Padding      = new Thickness(2),
            Child        = new Grid { Children = { fillGrid } },
        };
    }

    private static TextBlock MakeWrapText(double fontSize, string colorHex) => new()
    {
        FontSize     = fontSize,
        Foreground   = Brush(colorHex),
        TextWrapping = TextWrapping.WrapWholeWords,
        TextTrimming = TextTrimming.None,
        MaxWidth     = 490,
        MinHeight    = 20,   // reserves line height in the layout
    };

    private void SetStatus(string message, string colorHex, params object?[] args)
    {
        if (args.Length == 0) LocalizationUi.SetText(_status, message);
        else LocalizationUi.SetFormattedText(_status, message, args);
        _status.Foreground = Brush(colorHex);
        _status.Opacity    = message.Length == 0 ? 0 : 1;  // always reserves its row height
        ToolTipService.SetToolTip(_status, _status.Text);
    }

    private Button MakeAccentButton(string text) => new()
    {
        Content     = text,
        MinWidth    = 130,
        Background  = Brush(MenuTheme.AccentColor),
        Foreground  = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x14, 0x18)),
        FontWeight  = Microsoft.UI.Text.FontWeights.SemiBold,
    };

    private Button MakeCancelButton() => new()
    {
        Content  = Limona.Services.Loc.T("Cancel"),
        MinWidth = 90,
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Utilities
    // ─────────────────────────────────────────────────────────────────────────

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / 1024.0 / 1024:F1} MB";
        return $"{bytes / 1024.0:F0} KB";
    }

    private static SolidColorBrush Brush(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        var a = (byte)((value >> 24) & 0xFF);
        var r = (byte)((value >> 16) & 0xFF);
        var g = (byte)((value >>  8) & 0xFF);
        var b = (byte)(value & 0xFF);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
    }
}
