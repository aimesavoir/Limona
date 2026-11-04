using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Limona.Interop;
using Limona.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;

using MQ = Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace Limona.Menu;

/// <summary>
/// Window opened by the "Resize" pie slice: change the picture's width/height
/// and/or save it at an EXACT target file size (e.g. 30 KB) using a JPEG
/// quality search padded to the exact byte count.
/// </summary>
internal sealed class ResizeWindow : Window
{
    private const int PreviewMaxSide = 640;

    private static ResizeWindow? s_active;

    private readonly string _sourcePath;
    private readonly string _fileName;
    private uint _sourceW, _sourceH;
    private long _sourceBytes;
    private bool _saving;
    private int _busyCount;

    private TextBlock _titleText = null!;
    private Image _previewImage = null!;
    private TextBlock _dimInfo = null!;
    private TextBox _widthBox = null!;
    private TextBox _heightBox = null!;
    private CheckBox _lockAspect = null!;
    private TextBox _targetBox = null!;
    private ComboBox _unitBox = null!;
    private TextBlock _sizeHint = null!;
    private TextBlock _status = null!;
    private Button _saveButton = null!;
    private Button _cancelButton = null!;
    private Border _previewHost = null!;
    private Border _busyBadge = null!;
    private Microsoft.UI.Xaml.Shapes.Ellipse _spinnerArc = null!;
    private RotateTransform _spinnerRotation = null!;
    private MQ.DispatcherQueueTimer? _spinnerTimer;

    /// <summary>Opens the resize window for a file (single instance at a time).</summary>
    public static void ShowForFile(string sourcePath)
    {
        s_active = ToolWindowCoordinator.Open(() => new ResizeWindow(sourcePath));
    }

    private ResizeWindow(string sourcePath)
    {
        _sourcePath = sourcePath;
        _fileName = System.IO.Path.GetFileName(sourcePath);

        WindowFit.SetTitle(this, "Resize — " + _fileName);
        _titleText = MakeTitle();
        _previewImage = MakePreviewImage();
        _previewHost = MakePreviewHost();
        _dimInfo = MakeHintText();
        _widthBox = MakeNumberBox("0");
        _heightBox = MakeNumberBox("0");
        _lockAspect = MakeLockAspect();
        _targetBox = MakeNumberBox("");
        _unitBox = MakeUnitBox();
        _sizeHint = MakeHintText();
        _status = MakeStatusText();
        _saveButton = MakeAccentButton("Save");
        _cancelButton = MakeCancelButton();
        _cancelButton.Click += (_, _) => Close();
        _saveButton.Click += (_, _) => _ = OnSaveClicked();
        // Live aspect sync: typing in width/height updates the other box as you type.
        // WinUI raises TextChanged asynchronously, so a double guard is needed:
        // 1) never rewrite the box that currently has focus (caret scrambling);
        // 2) final synch on focus loss after each box edit.
        _widthBox.TextChanged += (_, _) => SyncLockedDimension(_widthBox, _heightBox);
        _heightBox.TextChanged += (_, _) => SyncLockedDimension(_heightBox, _widthBox);
        _widthBox.LostFocus += (_, _) => SyncLockedDimension(_widthBox, _heightBox);
        _heightBox.LostFocus += (_, _) => SyncLockedDimension(_heightBox, _widthBox);

        Content = BuildLayout();
        WindowFit.Apply(this, (FrameworkElement)Content!);

        _ = PrepareAsync();
    }

    // ------------------------------------------------------------------
    // Preparation: source dimensions + pixels for the preview
    // ------------------------------------------------------------------

    private async System.Threading.Tasks.Task PrepareAsync()
    {
        BeginBusy();
        try
        {
            SetStatus("", MenuTheme.HintColor);
            var (w, h) = await System.Threading.Tasks.Task.Run(
                () => ImageConverter.GetImageDimensionsAsync(_sourcePath));
            _sourceW = w;
            _sourceH = h;
            _sourceBytes = new System.IO.FileInfo(_sourcePath).Length;

            _widthBox.Text = w.ToString();
            _heightBox.Text = h.ToString();
            LocalizationUi.SetFormattedText(_dimInfo,
                "Original: {0} × {1} px · {2} · source: {3}", w, h, FormatKb(_sourceBytes), _fileName);
            LocalizationUi.SetText(_sizeHint,
                "Exact size: JPEG quality search, padded to the exact byte count. Empty = lossless PNG.");

            var (pixels, pw, ph, _, _) = await System.Threading.Tasks.Task.Run(
                () => ImageConverter.DecodeScaledPixelsAsync(_sourcePath, PreviewMaxSide));
            var sb = await System.Threading.Tasks.Task.Run(
                () => ImageConverter.CreateSoftwareBitmapFromPixels(pixels, pw, ph));
            await SetPreviewSourceAsync(sb);
            sb.Dispose();
            SetStatus("", MenuTheme.HintColor);
        }
        catch (Exception ex)
        {
            Dbg.Log("ResizeWindow prepare THREW: " + ex);
            SetStatus("Failed: {0}", "#FFED6A5E", Shorten(ex.Message));
        }
        finally
        {
            EndBusy();
        }
    }

    private async System.Threading.Tasks.Task SetPreviewSourceAsync(SoftwareBitmap bitmap)
    {
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        _previewImage.Source = source;
    }

    // ------------------------------------------------------------------
    // Aspect-ratio lock
    // ------------------------------------------------------------------

    private int _syncing;

    private void SyncLockedDimension(TextBox source, TextBox other)
    {
        if (_lockAspect.IsChecked != true || _sourceW == 0 || _sourceH == 0) return;
        // The queued TextChanged echo would recompute OUR box while the user is
        // typing in it — never write into the focused box.
        if (other.FocusState != FocusState.Unfocused) return;
        if (!int.TryParse(source.Text, out var value) || value <= 0) return; // mid-typing: wait
        var computed = source == _widthBox
            ? (int)Math.Max(1, Math.Round(value * (double)_sourceH / _sourceW))
            : (int)Math.Max(1, Math.Round(value * (double)_sourceW / _sourceH));
        if (int.TryParse(other.Text, out var current) && current == computed) return; // no churn
        _syncing++;
        try { other.Text = computed.ToString(); }
        finally { _syncing--; }
    }

    // ------------------------------------------------------------------
    // Save: dims change and/or exact-size JPEG encode
    // ------------------------------------------------------------------

    private async System.Threading.Tasks.Task OnSaveClicked()
    {
        if (_saving || _sourceW == 0) return;
        if (!int.TryParse(_widthBox.Text, out var newW) || !int.TryParse(_heightBox.Text, out var newH) ||
            newW < 1 || newH < 1 || newW > 20000 || newH > 20000)
        {
            SetStatus("Enter valid width/height (1–20000).", "#FFED6A5E");
            return;
        }

        long? targetBytes = null;
        var targetText = _targetBox.Text.Trim();
        if (targetText.Length > 0)
        {
            if (!double.TryParse(targetText, System.Globalization.CultureInfo.InvariantCulture, out var value)
                || value <= 0)
            {
                SetStatus("Enter a valid target size, e.g. 30.", "#FFED6A5E");
                return;
            }
            var unit = _unitBox.SelectedItem as string ?? "KB";
            targetBytes = unit == "MB"
                ? (long)Math.Round(value * 1024 * 1024)
                : (long)Math.Round(value * 1024);
            if (targetBytes < 512)
            {
                SetStatus("Target size is too small (minimum 0.5 KB).", "#FFED6A5E");
                return;
            }
        }

        _saving = true;
        _saveButton.IsEnabled = false;
        BeginBusy();

        try
        {
            var sourcePath = _sourcePath;
            var dir = System.IO.Path.GetDirectoryName(sourcePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var baseName = System.IO.Path.GetFileNameWithoutExtension(sourcePath);

            bool dimensionChanged = newW != (int)_sourceW || newH != (int)_sourceH;

            if (targetBytes is null)
            {
                SetStatus("Saving in full quality…", MenuTheme.AccentColor);
                var lossless = System.IO.Path.GetExtension(sourcePath)
                    .Equals(".png", StringComparison.OrdinalIgnoreCase);
                string savedName = "";
                long savedBytes = 0;
                await System.Threading.Tasks.Task.Run(async () =>
                {
                    var (pixels, wSrc, hSrc, _, _) = await ImageConverter.DecodeScaledPixelsAsync(sourcePath, 0);
                    var proc = dimensionChanged
                        ? ImageConverter.BilinearResizeRgba(pixels, (int)wSrc, (int)hSrc, newW, newH)
                        : pixels;
                    var wantPng = lossless || !IsJpegSource(sourcePath);
                    byte[] bytes = wantPng
                        ? await ImageConverter.EncodePngBytesAsync(proc, (uint)newW, (uint)newH)
                        : await ImageConverter.EncodeJpegBytesAsync(proc, (uint)newW, (uint)newH, 0.92);
                    var destPath = ImageConverter.GetUniqueOutputPath(dir, baseName + "_resized", wantPng ? ".png" : ".jpg");
                    File.WriteAllBytes(destPath, bytes);
                    savedName = System.IO.Path.GetFileName(destPath);
                    savedBytes = bytes.Length;
                });
                SetStatus("Saved: {0} · {1} · {2}×{3}", "#FF9BE29B",
                    savedName, FormatKb(savedBytes), newW, newH);
            }
            else
            {
                var target = targetBytes.Value;
                SetStatus("Compressing to exactly {0}…", MenuTheme.AccentColor, FormatKb(target));
                string savedName = "";
                await System.Threading.Tasks.Task.Run(async () =>
                {
                    var (pixels, wSrc, hSrc, _, _) = await ImageConverter.DecodeScaledPixelsAsync(sourcePath, 0);
                    var bytes = await EncodeToExactSizeAsync(
                        pixels, (int)wSrc, (int)hSrc, newW, newH, target);
                    var destPath = ImageConverter.GetUniqueOutputPath(dir, baseName + "_resized", ".jpg");
                    File.WriteAllBytes(destPath, bytes);
                    savedName = System.IO.Path.GetFileName(destPath);
                });
                SetStatus("Saved: {0} · exactly {1} bytes · {2}×{3}", "#FF9BE29B",
                    savedName, target, newW, newH);
            }

            try { Win32.MessageBeep(0x00000040); } catch { }

            var closeTimer = MQ.DispatcherQueue.GetForCurrentThread().CreateTimer();
            closeTimer.Interval = TimeSpan.FromMilliseconds(1600);
            closeTimer.IsRepeating = false;
            closeTimer.Tick += (_, _) => { closeTimer.Stop(); Close(); };
            closeTimer.Start();
        }
        catch (Exception ex)
        {
            Dbg.Log("ResizeWindow save THREW: " + ex);
            SetStatus("Save failed: {0}", "#FFED6A5E", Shorten(ex.Message));
            _saveButton.IsEnabled = true;
            _saving = false;
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>
    /// JPEG quality binary search until the encode fits into the target byte
    /// budget, shrinking dimensions as a last resort; then zero-padding after
    /// the JPEG EOI marker reaches the EXACT requested file size (file bytes
    /// beyond EOI are ignored by all decoders).
    /// </summary>
    private static async System.Threading.Tasks.Task<byte[]> EncodeToExactSizeAsync(
        byte[] srcPixels, int sw, int sh, int dw, int dh, long target)
    {
        var limit = target - Math.Max(64, target / 128); // small headroom for the exact-size padding

        while (true)
        {
            var pixels = dw == sw && dh == sh
                ? srcPixels
                : ImageConverter.BilinearResizeRgba(srcPixels, sw, sh, dw, dh);

            byte[]? best = null;
            var lo = 0.05;
            var hi = 0.95;
            for (var iter = 0; iter <= 13; iter++)
            {
                var q = (lo + hi) / 2.0;
                var bytes = await ImageConverter.EncodeJpegBytesAsync(pixels, (uint)dw, (uint)dh, q);
                if (bytes.Length <= limit)
                {
                    best = bytes;
                    lo = q;
                    if (hi - lo < 0.015) break;
                }
                else
                {
                    hi = q;
                }
            }

            if (best is not null)
            {
                var result = new byte[target];
                Array.Copy(best, result, best.Length);
                return result; // trailing zeros pad to the exact size
            }

            // Even the lowest quality overshoots: shrink dimensions and retry.
            if (dw <= 64 || dh <= 64)
                throw new InvalidOperationException("This image cannot be compressed that small.");
            dw = Math.Max(64, dw * 3 / 4);
            dh = Math.Max(64, dh * 3 / 4);
        }
    }

    // ------------------------------------------------------------------
    // Busy spinner (WinUI ProgressRing crashes in this app — hand-built)
    // ------------------------------------------------------------------

    private void BeginBusy()
    {
        _busyCount++;
        UpdateBusyVisual();
    }

    private void EndBusy()
    {
        if (_busyCount > 0) _busyCount--;
        UpdateBusyVisual();
    }

    private void UpdateBusyVisual()
    {
        var busy = _busyCount > 0;
        _busyBadge.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            _spinnerTimer ??= CreateSpinnerTimer();
            _spinnerTimer.Start();
        }
        else
        {
            _spinnerTimer?.Stop();
        }
    }

    private MQ.DispatcherQueueTimer CreateSpinnerTimer()
    {
        var timer = MQ.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(33);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => _spinnerRotation.Angle = (_spinnerRotation.Angle + 12) % 360;
        return timer;
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

        var grid = new Grid(); // rows: 0 title, 1 preview(star), 2 dims, 3 size, 4 info, 5 status, 6 buttons
        for (var i = 0; i < 7; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
        }
        grid.RowSpacing = 10;

        // 0: title
        Grid.SetRow(_titleText, 0);
        grid.Children.Add(_titleText);

        // 1: preview image with busy overlay
        var previewGrid = new Grid
        {
            MinHeight = 500, // generous interactive picture area
        };
        previewGrid.Children.Add(new Border
        {
            Background = Brush("#FF1B1E2A"),
            CornerRadius = new CornerRadius(10),
            Child = new Grid
            {
                Padding = new Thickness(10),
                Children = { _previewImage },
            },
        });
        _busyBadge = MakeBusyBadge();
        var overlay = new Grid { IsHitTestVisible = false };
        overlay.Children.Add(_busyBadge);
        previewGrid.Children.Add(overlay);
        Grid.SetRow(previewGrid, 1);
        grid.Children.Add(previewGrid);

        // 2: width / height / aspect lock
        var dimRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        dimRow.Children.Add(new TextBlock { Text = "Width", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(MenuTheme.HintColor), FontSize = 12 });
        dimRow.Children.Add(_widthBox);
        dimRow.Children.Add(new TextBlock { Text = "Height", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(MenuTheme.HintColor), FontSize = 12, Margin = new Thickness(8, 0, 0, 0) });
        dimRow.Children.Add(_heightBox);
        dimRow.Children.Add(_lockAspect);
        Grid.SetRow(dimRow, 2);
        grid.Children.Add(dimRow);

        var sizeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        sizeRow.Children.Add(new TextBlock { Text = "Target size", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(MenuTheme.HintColor), FontSize = 12 });
        _targetBox.MinWidth = 110;
        sizeRow.Children.Add(_targetBox);
        _unitBox.MinWidth = 74;
        sizeRow.Children.Add(_unitBox);
        Grid.SetRow(sizeRow, 3);
        grid.Children.Add(sizeRow);

        var infoStack = new StackPanel { Spacing = 2 };
        infoStack.Children.Add(_dimInfo);
        infoStack.Children.Add(_sizeHint);
        Grid.SetRow(infoStack, 4);
        grid.Children.Add(infoStack);

        Grid.SetRow(_status, 5);
        grid.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
        };
        buttons.Children.Add(_cancelButton);
        buttons.Children.Add(_saveButton);
        Grid.SetRow(buttons, 6);
        grid.Children.Add(buttons);

        root.Child = grid;
        return root;
    }

    private TextBlock MakeTitle() => new()
    {
        Text = "Resize — Image dimensions & file size",
        FontSize = 15,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Brush(MenuTheme.LabelColor),
    };

    private Image MakePreviewImage() => new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        MaxHeight = 480,
    };

    private Border MakePreviewHost() => new();

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

    private TextBox MakeNumberBox(string initial) => new()
    {
        Text = initial,
        MinWidth = 92,
    };

    private CheckBox MakeLockAspect() => new()
    {
        Content = "Keep aspect",
        IsChecked = true,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brush(MenuTheme.HintColor),
    };

    private ComboBox MakeUnitBox()
    {
        var box = new ComboBox();
        box.Items.Add("KB");
        box.Items.Add("MB");
        box.SelectedIndex = 0;
        return box;
    }

    private Button MakeAccentButton(string text)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 96,
            Background = Brush(MenuTheme.AccentColor),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x14, 0x18)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        return button;
    }

    private Button MakeCancelButton() => new()
    {
        Content = "Cancel",
        MinWidth = 96,
    };

    private Border MakeBusyBadge()
    {
        var accent = Brush(MenuTheme.AccentColor);
        var track = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 40,
            Height = 40,
            Stroke = Brush(MenuTheme.HintColor),
            StrokeThickness = 4,
            Opacity = 0.35,
        };
        _spinnerArc = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 40,
            Height = 40,
            Stroke = accent,
            StrokeThickness = 5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashArray = new DoubleCollection { 8.5, 16.7 },
        };
        _spinnerRotation = new RotateTransform { Angle = 0 };
        _spinnerArc.RenderTransform = _spinnerRotation;
        _spinnerArc.RenderTransformOrigin = new Point(0.5, 0.5);

        return new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xC0, 0x1B, 0x1E, 0x2A)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Grid
            {
                Width = 40,
                Height = 40,
                Children = { track, _spinnerArc },
            },
        };
    }

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

    private static bool IsJpegSource(string path) =>
        System.IO.Path.GetExtension(path) is ".jpg" or ".jpeg" or ".jfif";

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
