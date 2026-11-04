using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Limona.Interop;
using Limona.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;

using MQ = Microsoft.UI.Dispatching;

namespace Limona.Menu;

/// <summary>
/// Remove-Background studio window: shows the AI cutout live over a transparency
/// checkerboard, lets the user tune the cutout, key out a flat background color
/// (auto-detected or picked from the image), and refine the result with a soft
/// manual eraser, wheel zooming and click-drag panning before saving.
///
/// Composition runs on a single-flight, coalescing pipeline: slider drags and
/// brush strokes are throttled, superseded work is cancelled, and only the
/// newest frame is uploaded, so the preview stays responsive.
/// </summary>
internal sealed class BackgroundPreviewWindow : Window
{
    private const int PreviewMaxSide = 900;

    private static BackgroundPreviewWindow? s_active;

    private readonly string _sourcePath;
    private readonly string _fileName;

    // ---- image state (preview resolution) --------------------------------
    private string? _maskPath;       // temp full-resolution mask PNG
    private byte[]? _bgra;           // source pixels (never mutated)
    private byte[]? _mask;           // AI mask
    private byte[]? _eraseMask;      // manual eraser weights (255 keep, 0 erase)
    private byte[]? _displayBgra;    // last uploaded (composed) pixels
    private byte[]? _blurredBase;    // cached shaped + feathered AI alpha
    private byte[]? _bgKey;          // cached background-key mask
    private uint _w, _h;

    private bool _loaded, _saving, _closed;

    // ---- background key ---------------------------------------------------
    private (byte R, byte G, byte B)? _keyColor;   // null = auto from borders
    private (byte R, byte G, byte B) _autoKeyColor;
    private bool _hasAutoKeyColor;
    private bool _pickColorMode;

    // ---- compose scheduler ------------------------------------------------
    private int _composeVersion;
    private bool _composeRunning;
    private bool _composePending;
    private bool _heavyPending;
    private bool _uploadPending;
    private bool _prepareBusy;
    private bool _inlineBusyOn;
    private MQ.DispatcherQueueTimer? _recomposeTimer;
    private CancellationTokenSource? _composeCts;
    private WriteableBitmap? _writeableBitmap;
    private MQ.DispatcherQueueTimer? _inlineHideTimer;
    private long _inlineShownAt;
    private const int InlineBusyMinMs = 340;

    // ---- viewport transform ----------------------------------------------
    private double _scale = 1, _tx, _ty;
    private bool _fitPending;
    private bool _pointerDown;
    private uint _pointerId;
    private Point _lastPan, _lastStamp;
    private bool _spacePan, _inViewport, _needNewStroke;
    private Point _trackedPointerPos;
    private InputCursor? _eraseCursor;
    private int _eraseCursorDiameter = -1;
    private static readonly InputCursor PanCursor =
        InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private static readonly InputCursor PickCursor =
        InputSystemCursor.Create(InputSystemCursorShape.Cross);

    // ---- controls ---------------------------------------------------------
    private TextBlock _fileNameText = null!;
    private TextBlock _status = null!;
    private Grid _viewport = null!;
    private Canvas _stage = null!;
    private Image _image = null!;
    private Image _checkerImage = null!;
    private Button _eraseButton = null!;
    private Button _panButton = null!;
    private Button _undoButton = null!;
    private Button _resetButton = null!;
    private Slider _brushSlider = null!;
    private TextBlock _brushValue = null!;
    private Button _fitButton = null!;
    private Slider _thresholdSlider = null!;
    private Slider _featherSlider = null!;
    private Slider _softnessSlider = null!;
    private Slider _smoothnessSlider = null!;
    private Slider _shiftSlider = null!;
    private Slider _contrastSlider = null!;
    private Slider _bgKeySlider = null!;
    private CheckBox _contiguousCheck = null!;
    private TextBlock _smoothnessValue = null!;
    private TextBlock _shiftValue = null!;
    private TextBlock _contrastValue = null!;
    private TextBlock _bgKeyValue = null!;
    private TextBlock _thresholdValue = null!;
    private TextBlock _featherValue = null!;
    private TextBlock _softnessValue = null!;
    private Button _saveButton = null!;
    private Button _cancelButton = null!;
    private Button _keyColorButton = null!;
    private Button _pickColorButton = null!;
    private Border _keyColorChip = null!;
    private TextBlock _keyColorLabel = null!;
    private Flyout? _keyColorFlyout;
    private bool _eraseActive = true;
    private Border _rootBorder = null!;

    // ---- busy / undo ------------------------------------------------------
    private Border _busyBadge = null!;
    private Microsoft.UI.Xaml.Shapes.Ellipse _spinnerArc = null!;
    private Microsoft.UI.Xaml.Shapes.Ellipse _inlineArc = null!;
    private RotateTransform _spinnerRotation = null!;
    private RotateTransform _inlineRotation = null!;
    private StackPanel _inlineBusy = null!;
    private MQ.DispatcherQueueTimer? _spinnerTimer;
    private int _busyCount;
    private readonly List<byte[]> _undoStack = new();
    private const int UndoLimit = 40;
    private MQ.DispatcherQueueTimer? _autoCloseTimer;

    /// <summary>Opens the preview window for a file (single instance at a time).</summary>
    public static void ShowForFile(string sourcePath)
    {
        s_active = ToolWindowCoordinator.Open(() => new BackgroundPreviewWindow(sourcePath));
    }

    private BackgroundPreviewWindow(string sourcePath)
    {
        _sourcePath = sourcePath;
        _fileName = System.IO.Path.GetFileName(sourcePath);

        WindowFit.SetTitle(this, "Remove Background — Studio");
        _fileNameText = MakeFileNameText();
        _status = MakeStatusText();
        _inlineBusy = MakeInlineBusy();
        _image = MakePreviewImage();
        _checkerImage = MakeCheckerImage();
        _stage = MakeStage();
        _busyBadge = MakeBusyBadge();
        _viewport = MakeViewport();
        _eraseButton = MakeToolButton("Erase", erase: true);
        _panButton = MakeToolButton("Pan", erase: false);
        _undoButton = MakeUndoButton();
        _resetButton = MakeResetButton();
        _brushSlider = MakeBrushSlider();
        _brushValue = MakeSliderValue();
        _brushValue.Text = "40 px";
        _brushValue.MinWidth = 52;
        _fitButton = MakeFitButton();
        _thresholdSlider = MakeCutoutSlider();
        _featherSlider = MakeFeatherSlider();
        _smoothnessSlider = MakeSmoothnessSlider();
        _shiftSlider = MakeShiftSlider();
        _contrastSlider = MakeContrastSlider();
        _bgKeySlider = MakeBgKeySlider();
        _contiguousCheck = MakeContiguousCheck();
        _softnessSlider = MakeSoftnessSlider();
        _thresholdValue = MakeSliderValue();
        _featherValue = MakeSliderValue();
        _smoothnessValue = MakeSliderValue();
        _shiftValue = MakeSliderValue();
        _contrastValue = MakeSliderValue();
        _bgKeyValue = MakeSliderValue();
        _softnessValue = MakeSliderValue();
        _softnessValue.Text = "20%";
        _saveButton = MakeSaveButton();
        _cancelButton = MakeCancelButton();
        BuildKeyColorControls();
        _eraseActive = true;
        StyleTool(_eraseButton, true);
        StyleTool(_panButton, false);
        _eraseButton.Click += (_, _) => { if (_loaded) SetTool(true); };
        _panButton.Click += (_, _) => { if (_loaded) SetTool(false); };

        Content = BuildLayout();
        WindowFit.ApplyFixed(this, (FrameworkElement)Content!, 1240, 790);

        _recomposeTimer = MQ.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _recomposeTimer.IsRepeating = false;
        _recomposeTimer.Tick += (_, _) =>
        {
            _recomposeTimer!.Stop();
            _ = PumpComposeAsync();
        };

        this.Closed += (_, _) => OnWindowClosed();

        _rootBorder.KeyDown += OnRootKeyDown;
        _rootBorder.KeyUp += OnRootKeyUp;

        _ = PrepareAsync();
    }

    // ------------------------------------------------------------------
    // Preparation (image decode + AI mask, then first compose)
    // ------------------------------------------------------------------

    private async System.Threading.Tasks.Task PrepareAsync()
    {
        BeginBusy();
        try
        {
            UpdateControls(loaded: false);
            SetStatus("Loading image…", MenuTheme.HintColor);

            var (pixels, w, h, _, _) = await System.Threading.Tasks.Task.Run(
                () => ImageConverter.DecodeScaledPixelsAsync(_sourcePath, PreviewMaxSide));
            _bgra = pixels;
            _w = w;
            _h = h;
            _eraseMask = new byte[w * h];
            Array.Fill(_eraseMask, (byte)255);

            SetStatus("Analyzing background (AI)…", MenuTheme.AccentColor);
            _maskPath = await BackgroundRemover.GenerateMaskAsync(_sourcePath);

            _mask = await System.Threading.Tasks.Task.Run(
                () => ImageConverter.DecodeGrayAsync(_maskPath, w, h));

            _autoKeyColor = BackgroundRemover.EstimateBackgroundColor(_bgra, w, h);
            _hasAutoKeyColor = true;
            UpdateKeyColorChip();

            _fileNameText.Text = _fileName;
            _loaded = true;
            UpdateControls(loaded: true);
            SetStatus("", MenuTheme.HintColor); // ready — keep the window clean
            FitToView();
            _prepareBusy = true; // the busy ring stays until the first frame is displayed
            RequestCompose(heavy: true);
        }
        catch (Exception ex)
        {
            Dbg.Log("BackgroundPreview prepare THREW: " + ex);
            EndBusy();
            SetStatus("Failed: {0}", "#FFED6A5E", Shorten(ex.Message));
            UpdateControls(loaded: false, keepButtonsOff: true);
        }
    }

    // ------------------------------------------------------------------
    // Live re-compositing
    //
    // One pipeline, single-flight: a request marks what is dirty and bumps the
    // version; a running pump notices the bump, cancels the superseded compute
    // and immediately re-runs with the newest slider values. Only the newest
    // frame is uploaded, so dragging never queues dozens of stale frames.
    //
    // 1) BlurBase   — binarized AI mask + shape + feather (cached; "heavy")
    // 2) ApplyCompose — base x eraser x background key (cheap, always)
    // ------------------------------------------------------------------

    private void RequestCompose(bool heavy)
    {
        if (!_loaded || _closed) return;
        if (heavy) _heavyPending = true;
        _composePending = true;
        _composeVersion++;

        try { _composeCts?.Cancel(); } catch { /* already finished */ }

        if (!_composeRunning) ScheduleCompose(heavy ? 26 : 14);
        SetInlineBusy(true);
    }

    private void RequestUpload()
    {
        if (!_loaded || _closed) return;
        _uploadPending = true;
        _composeVersion++; // invalidate any in-flight compose so it can't overwrite the stroke

        try { _composeCts?.Cancel(); } catch { }

        if (!_composeRunning)
        {
            _ = PumpComposeAsync();
        }
    }

    private void ScheduleCompose(int delayMs)
    {
        if (_recomposeTimer is null || _closed) return;
        _recomposeTimer.Stop();
        _recomposeTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _recomposeTimer.Start();
    }

    private async System.Threading.Tasks.Task PumpComposeAsync()
    {
        if (_composeRunning || !_loaded || _closed) return;
        _composeRunning = true;
        try
        {
            while ((_composePending || _uploadPending) && _loaded && !_closed)
            {
                var heavy = _heavyPending;
                _heavyPending = false;
                var doCompose = _composePending;
                _composePending = false;
                _uploadPending = false;

                var version = _composeVersion;
                using var cts = new CancellationTokenSource();
                _composeCts = cts;

                // Snapshot everything on the UI thread.
                var threshold = (int)_thresholdSlider.Value;
                var feather = (int)_featherSlider.Value;
                var smoothness = (int)_smoothnessSlider.Value;
                var shiftEdge = (int)_shiftSlider.Value;
                var contrast = (int)_contrastSlider.Value;
                var bgTolerance = (int)_bgKeySlider.Value;
                var keyColor = _keyColor;
                var contiguous = _contiguousCheck.IsChecked != false;

                var mask = _mask;
                var source = _bgra;
                var erase = _eraseMask;
                var w = _w;
                var h = _h;
                if (mask is null || source is null || erase is null || w == 0) break;

                var cachedBase = heavy ? null : _blurredBase;

                                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (doCompose)
                    {
                        (byte[] Base, byte[]? Key, byte[] Pixels) result;
                        result = await System.Threading.Tasks.Task.Run(() =>
                        {
                            var ct = cts.Token;
                            var baseAlpha = cachedBase ?? BackgroundRemover.BlurBase(
                                mask, w, h, threshold, feather, smoothness, contrast, shiftEdge, ct);

                            var key = bgTolerance > 0
                                ? BackgroundRemover.DetectBackground(source, w, h, bgTolerance, keyColor, contiguous, ct)
                                : null;

                            var pixels = (byte[])source.Clone();
                            BackgroundRemover.ApplyCompose(pixels, baseAlpha, erase, w, h, key);
                            return (baseAlpha, key, pixels);
                        }, cts.Token);

                        if (cts.IsCancellationRequested || version != _composeVersion || _closed) continue;

                        _blurredBase = result.Base;
                        _bgKey = result.Key;
                        _displayBgra = result.Pixels;
                    }
                    else
                    {
                        // Upload-only path: pixels are already up to date (eraser stroke).
                        if (_displayBgra is null) continue;
                    }

                    await UploadAsync();

                    if (_prepareBusy)
                    {
                        _prepareBusy = false;
                        EndBusy();
                        // Let the keyboard shortcuts work without a click first.
                        _rootBorder.Focus(FocusState.Programmatic);
                    }
                }
                catch (OperationCanceledException)
                {
                    continue; // superseded - the loop re-runs with fresh values
                }
                sw.Stop();
                if (sw.ElapsedMilliseconds >= 60)
                    Dbg.Log($"BackgroundPreview compose {sw.ElapsedMilliseconds} ms (heavy={heavy}, {w}x{h})");
            }
        }
        catch (Exception ex)
        {
            Dbg.Log("BackgroundPreview compose THREW: " + ex.GetType().FullName +
                    " hr=0x" + ex.HResult.ToString("X8") + " msg='" + ex.Message + "'\n" + ex.StackTrace);
        }
        finally
        {
            _composeRunning = false;
            if (!_composePending) SetInlineBusy(false);
        }
    }

    /// <summary>Pushes the current <see cref="_displayBgra"/> into the Image.</summary>
    private System.Threading.Tasks.Task UploadAsync()
    {
        if (_displayBgra is null || _closed) return System.Threading.Tasks.Task.CompletedTask;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bytes = _displayBgra;
        var w = _w;
        var h = _h;

        // Create or resize the WriteableBitmap once.
        if (_writeableBitmap is null || _writeableBitmap.PixelWidth != (int)w || _writeableBitmap.PixelHeight != (int)h)
        {
            _writeableBitmap = new WriteableBitmap((int)w, (int)h);
            _image.Source = _writeableBitmap;
        }

        var wb = _writeableBitmap;
        // PixelBuffer must be accessed on the UI thread (STA). Do the premultiply
        // inline - it is fast (~2 ms) and avoids COM cross-thread issues.
        try
        {
            var stream = wb.PixelBuffer.AsStream();
            stream.Seek(0, System.IO.SeekOrigin.Begin);

            var n = (int)(w * h);
            var buf = new byte[bytes.Length];
            System.Buffer.BlockCopy(bytes, 0, buf, 0, bytes.Length);
            for (var i = 0; i < n; i++)
            {
                var a = buf[i * 4 + 3];
                if (a == 0)
                {
                    buf[i * 4 + 0] = 0;
                    buf[i * 4 + 1] = 0;
                    buf[i * 4 + 2] = 0;
                }
                else if (a != 255)
                {
                    buf[i * 4 + 0] = (byte)(buf[i * 4 + 0] * a / 255);
                    buf[i * 4 + 1] = (byte)(buf[i * 4 + 1] * a / 255);
                    buf[i * 4 + 2] = (byte)(buf[i * 4 + 2] * a / 255);
                }
            }
            stream.Write(buf, 0, buf.Length);

            wb.Invalidate();
        }
        catch (Exception ex)
        {
            Dbg.Log($"UploadAsync inner THREW: {ex.GetType().Name}: {ex.Message}");
        }
                return System.Threading.Tasks.Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Manual eraser
    // ------------------------------------------------------------------

    private void StampEraseLine(Point a, Point b)
    {
        if (_eraseMask is null || _displayBgra is null) return;
        var r = (int)Math.Round(_brushSlider.Value / 2.0);
        if (r < 1) r = 1;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        // Denser stamp spacing for soft brushes so the faded trail stays continuous.
        var step = Math.Max(1, r * 0.2);
        var count = Math.Max(1, (int)Math.Ceiling(dist / step));
        for (var i = 0; i <= count; i++)
        {
            var t = count == 0 ? 0 : (double)i / count;
            Stamp(a.X + dx * t, a.Y + dy * t, r);
        }
    }

    /// <summary>
    /// Paints one brush stamp with a softness profile (solid core + smoothstep
    /// rim). The displayed alpha is recomputed from the cached AI base and the
    /// background key, so erasing never resurrects a pixel the AI removed.
    /// </summary>
    private void Stamp(double px, double py, int radius)
    {
        var w = (int)_w;
        var h = (int)_h;
        var cx = (int)Math.Round(px);
        var cy = (int)Math.Round(py);
        if (cx + radius < 0 || cy + radius < 0 || cx - radius >= w || cy - radius >= h) return;

        var soft = Math.Clamp(_softnessSlider.Value / 100.0, 0.0, 1.0);
        // The fade band is only the outer (soft) fraction of the brush radius;
        // even at 0% keep ~1px of anti-aliasing so edges stay clean, not jagged.
        var inner = Math.Max(1.2, radius * (1 - soft));

        var x0 = Math.Max(0, cx - radius);
        var x1 = Math.Min(w - 1, cx + radius);
        var y0 = Math.Max(0, cy - radius);
        var y1 = Math.Min(h - 1, cy + radius);
        for (var y = y0; y <= y1; y++)
        {
            var row = y * w;
            for (var x = x0; x <= x1; x++)
            {
                var ex = x - cx;
                var ey = y - cy;
                var d = Math.Sqrt(ex * ex + ey * ey);
                if (d >= radius) continue;

                // weight: 255 = keep as is, 0 = fully erase
                int weight;
                if (d <= inner) weight = 0;
                else
                {
                    var t = (d - inner) / Math.Max(1e-6, radius - inner);
                    var s = t * t * (3 - 2 * t); // smoothstep
                    weight = (int)Math.Round(255 * s);
                }

                var idx = row + x;
                var cur = _eraseMask![idx];
                if (weight >= cur) continue;

                _eraseMask[idx] = (byte)weight;

                // Display = AI base x eraser x background key (same math as ApplyCompose).
                var a = _blurredBase is { } baseAlpha ? baseAlpha[idx] : (byte)255;
                var av = a * weight / 255;
                var keyed = _bgKey?[idx] ?? 0;
                if (keyed > 0) av = av * (255 - keyed) / 255;
                if (av < 6) av = 0;
                else if (av > 249) av = 255;
                _displayBgra![idx * 4 + 3] = (byte)av;
            }
        }
    }

    // ------------------------------------------------------------------
    // Viewport interaction: erase strokes, color picking, wheel zoom, pan
    // ------------------------------------------------------------------

    private void OnViewportPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_saving || !_loaded) return;
        var point = e.GetCurrentPoint(_viewport);
        _trackedPointerPos = point.Position;
        RefreshSpaceState();
        if (!point.Properties.IsLeftButtonPressed) return;

        // Eyedropper: the next click sets the background key color.
        if (_pickColorMode)
        {
            PickColorAt(point.Position);
            e.Handled = true;
            return;
        }

        _viewport.CapturePointer(e.Pointer);
        _pointerId = e.Pointer.PointerId;
        _pointerDown = true;
        _needNewStroke = false;
        if (IsPanMode()) _lastPan = point.Position;
        else
        {
            PushUndoState(); // one undo step per stroke
            _lastStamp = ToImageCoords(point.Position);
            StampEraseLine(_lastStamp, _lastStamp);
            RequestUpload();
        }
    }

    private void OnViewportMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_viewport);
        _trackedPointerPos = point.Position;
        RefreshSpaceState();
        if (!_pointerDown || e.Pointer.PointerId != _pointerId || _saving || !_loaded) return;
        if (IsPanMode())
        {
            var pos = point.Position;
            _tx += pos.X - _lastPan.X;
            _ty += pos.Y - _lastPan.Y;
            _lastPan = pos;
            ApplyTransform();
            _needNewStroke = true; // a pan jump must never bridge an erase stroke
            return;
        }

        var img = ToImageCoords(point.Position);
        var gap = Math.Sqrt((img.X - _lastStamp.X) * (img.X - _lastStamp.X) +
                            (img.Y - _lastStamp.Y) * (img.Y - _lastStamp.Y));
        var maxBridge = Math.Max(40, _brushSlider.Value * 8);
        if (_needNewStroke || gap > maxBridge)
        {
            // A zoom/pan/space switch or a huge jump happened meanwhile:
            // continue erasing here, but never draw a line back to the old spot.
            _lastStamp = img;
            _needNewStroke = false;
            StampEraseLine(img, img);
        }
        else
        {
            StampEraseLine(_lastStamp, img);
            _lastStamp = img;
        }
        RequestUpload();
    }

    /// <summary>Pan is the Pan tool, or a temporary Space-bar override over the viewport.</summary>
    private bool IsPanMode() => !_eraseActive || _spacePan;

    /// <summary>
    /// Reads the physical Space key state directly; unlike key events this works
    /// without any focus having landed in the window (plain mouse interaction).
    /// </summary>
    private bool IsSpaceHeld()
        => (Win32.GetAsyncKeyState(0x20) & 0x8000) != 0;

    private void RefreshSpaceState()
    {
        var held = IsSpaceHeld();
        if (held == _spacePan) return;
        _spacePan = held;
        if (_pointerDown)
        {
            _lastStamp = ToImageCoords(_trackedPointerPos);
            _needNewStroke = true;
        }
        UpdateViewportCursor();
    }

    private void OnViewportReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _pointerId) return;
        _pointerDown = false;
        try { _viewport.ReleasePointerCapture(e.Pointer); } catch { }
    }

    private void OnViewportWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_saving || !_loaded) return;
        var point = e.GetCurrentPoint(_viewport);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;
        ZoomAt(point.Position.X, point.Position.Y, delta > 0 ? 1.15 : 1.0 / 1.15);
        e.Handled = true;
    }

    private void ZoomAt(double screenX, double screenY, double factor)
    {
        var newScale = Math.Clamp(_scale * factor, 0.02, 16);
        if (Math.Abs(newScale - _scale) < 1e-9) return;
        // Keep the point under the cursor anchored while the scale changes.
        var imgX = (screenX - _tx) / _scale;
        var imgY = (screenY - _ty) / _scale;
        _scale = newScale;
        _tx = screenX - imgX * _scale;
        _ty = screenY - imgY * _scale;
        ApplyTransform();
        if (_pointerDown) _needNewStroke = true; // anchoring changes image coords under the drag
        UpdateViewportCursor(); // the eraser circle is sized in screen pixels
    }

    private void ZoomAtCenter(double factor)
        => ZoomAt(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2, factor);

    private void FitToView()
    {
        var vw = _viewport.ActualWidth;
        var vh = _viewport.ActualHeight;
        if (_w == 0 || vw <= 0 || vh <= 0)
        {
            _fitPending = true;
            return;
        }
        var s = Math.Min(vw / _w, vh / _h) * 0.96;
        _scale = Math.Clamp(s, 0.02, 16);
        _tx = (vw - _w * _scale) / 2;
        _ty = (vh - _h * _scale) / 2;
        _fitPending = false;
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        if (_stage.RenderTransform is CompositeTransform ct)
        {
            ct.ScaleX = _scale;
            ct.ScaleY = _scale;
            ct.TranslateX = _tx;
            ct.TranslateY = _ty;
        }
    }

    private Point ToImageCoords(Point screen)
        => new((screen.X - _tx) / _scale, (screen.Y - _ty) / _scale);

    private void SetTool(bool erase)
    {
        _eraseActive = erase;
        StyleTool(_eraseButton, erase);
        StyleTool(_panButton, !erase);
        UpdateViewportCursor();
    }

    /// <summary>
    /// Chooses the cursor over the viewport: the eraser circle (sized to the
    /// brush in screen pixels), the crosshair while picking a key color, the pan
    /// cursor for the Pan tool / Space override, or the arrow when outside.
    /// </summary>
    private void UpdateViewportCursor()
    {
        if (_viewport is null) return;
        if (!_loaded || _saving || !_inViewport)
        {
            CursorInterop.SetCursor(_viewport, null);
            return;
        }

        if (_pickColorMode)
        {
            CursorInterop.SetCursor(_viewport, PickCursor);
            return;
        }

        if (_spacePan || !_eraseActive)
        {
            CursorInterop.SetCursor(_viewport, PanCursor);
            return;
        }

        var diameter = (int)Math.Round(_brushSlider.Value * _scale);
        diameter = Math.Clamp(diameter, 4, 124);
        if (diameter != _eraseCursorDiameter)
        {
            var old = _eraseCursor;
            _eraseCursor = CursorInterop.CreateCircleCursor(diameter);
            _eraseCursorDiameter = diameter;
            try { old?.Dispose(); } catch { }
        }
        CursorInterop.SetCursor(_viewport, _eraseCursor); // null falls back to the arrow
    }

    private static void StyleTool(Button button, bool active)
    {
        button.Background = active ? Brush(MenuTheme.AccentColor) : Brush(MenuTheme.HubFill);
        button.Foreground = active ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x14, 0x18))
                                   : Brush(MenuTheme.LabelColor);
        button.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    }

    // ------------------------------------------------------------------
    // Background key color
    // ------------------------------------------------------------------

    private void BuildKeyColorControls()
    {
        _keyColorChip = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("#FF3A3F52"),
            Background = Brush("#FFFFFFFF"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _keyColorLabel = new TextBlock
        {
            Text = "Auto",
            FontSize = 12,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(MenuTheme.LabelColor),
        };

        _keyColorButton = new Button
        {
            Padding = new Thickness(6, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { _keyColorChip, _keyColorLabel },
            },
        };
        ToolTipService.SetToolTip(_keyColorButton, "Background color that the key slider removes");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_keyColorButton, "Key color");
        _keyColorButton.Click += (_, _) => ShowKeyColorFlyout();

        _pickColorButton = new Button
        {
            Content = "Pick",
            MinWidth = 58,
        };
        ToolTipService.SetToolTip(_pickColorButton, "Pick the color straight from the image");
        _pickColorButton.Click += (_, _) => StartColorPick();
    }

    private void ShowKeyColorFlyout()
    {
        if (_keyColorFlyout is null)
        {
            var panel = new StackPanel { Spacing = 8, Width = 244 };

            panel.Children.Add(new TextBlock
            {
                Text = "Color to key out",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush(MenuTheme.LabelColor),
            });

            var auto = new Button
            {
                Content = "Auto — from image borders",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            auto.Click += (_, _) => { _keyColorFlyout?.Hide(); SetKeyColor(null); };
            panel.Children.Add(auto);

            var pick = new Button
            {
                Content = "Pick from image",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            pick.Click += (_, _) => { _keyColorFlyout?.Hide(); StartColorPick(); };
            panel.Children.Add(pick);

            var swatches = new Grid { ColumnSpacing = 5, RowSpacing = 5 };
            for (var c = 0; c < 8; c++)
                swatches.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var presets = new (byte R, byte G, byte B)[]
            {
                (255, 255, 255), (242, 242, 242), (214, 214, 214), (128, 128, 128),
                (64, 64, 64), (0, 0, 0), (0, 177, 64), (0, 71, 187),
                (30, 144, 255), (255, 59, 48), (255, 0, 255), (245, 222, 179),
                (222, 196, 160), (24, 32, 64), (120, 80, 50), (128, 128, 0),
            };
            for (var i = 0; i < presets.Length; i++)
            {
                var color = presets[i];
                var chip = new Border
                {
                    Height = 24,
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brush("#FF3A3F52"),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, color.R, color.G, color.B)),
                };
                var button = new Button
                {
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                    BorderThickness = new Thickness(0),
                    Content = chip,
                };
                ToolTipService.SetToolTip(button, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
                button.Click += (_, _) => { _keyColorFlyout?.Hide(); SetKeyColor(color); };
                Grid.SetColumn(button, i % 8);
                Grid.SetRow(button, i / 8);
                swatches.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                swatches.Children.Add(button);
            }
            panel.Children.Add(swatches);

            var hexRow = new Grid { ColumnSpacing = 6 };
            hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var hex = new TextBox { PlaceholderText = "#RRGGBB", FontSize = 12 };
            Grid.SetColumn(hex, 0);
            hexRow.Children.Add(hex);

            var apply = new Button { Content = "Set", MinWidth = 52 };
            void ApplyHex()
            {
                if (!TryParseHex(hex.Text, out var color)) return;
                _keyColorFlyout?.Hide();
                SetKeyColor(color);
            }
            apply.Click += (_, _) => ApplyHex();
            hex.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter) { ApplyHex(); e.Handled = true; }
            };
            Grid.SetColumn(apply, 1);
            hexRow.Children.Add(apply);
            panel.Children.Add(hexRow);

            LocalizationUi.Watch(panel);
            _keyColorFlyout = new Flyout { Content = panel };
        }

        _keyColorFlyout.ShowAt(_keyColorButton);
    }

    private void StartColorPick()
    {
        if (!_loaded) return;
        _pickColorMode = true;
        SetStatus("Click the color to remove…", MenuTheme.AccentColor);
        UpdateViewportCursor();
    }

    private void PickColorAt(Point screen)
    {
        if (_bgra is null || _w == 0) return;
        var img = ToImageCoords(screen);
        var x = (int)Math.Clamp(Math.Round(img.X), 0, (int)_w - 1);
        var y = (int)Math.Clamp(Math.Round(img.Y), 0, (int)_h - 1);
        var i = (y * (int)_w + x) * 4;

        _pickColorMode = false;
        SetStatus("", MenuTheme.HintColor);
        SetKeyColor((_bgra[i + 2], _bgra[i + 1], _bgra[i + 0]));
    }

    private void SetKeyColor((byte R, byte G, byte B)? color)
    {
        _keyColor = color;
        _pickColorMode = false;
        UpdateKeyColorChip();
        UpdateViewportCursor();

        // Picking a color with the key slider at 0 would look like nothing
        // happened — give the slider a sensible starting tolerance.
        if (_bgKeySlider.Value <= 0) _bgKeySlider.Value = 32;
        if (_loaded) RequestCompose(heavy: false);
    }

    private void UpdateKeyColorChip()
    {
        if (_keyColorChip is null) return;
        var c = _keyColor ?? (_hasAutoKeyColor ? _autoKeyColor : ((byte)255, (byte)255, (byte)255));
        _keyColorChip.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
        _keyColorLabel.Text = _keyColor is null ? "Auto" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static bool TryParseHex(string text, out (byte R, byte G, byte B) color)
    {
        color = default;
        var s = (text ?? "").Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6) return false;
        if (!byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)) return false;
        if (!byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)) return false;
        if (!byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return false;
        color = (r, g, b);
        return true;
    }

    // ------------------------------------------------------------------
    // Save (full quality, manual erases + keyed color included)
    // ------------------------------------------------------------------

    private async void OnSaveClicked()
    {
        if (!_loaded || _saving) return;
        _saving = true;
        BeginBusy();

        _saveButton.IsEnabled = false;
        _cancelButton.IsEnabled = false;
        SetStatus("Saving in full quality…", MenuTheme.AccentColor);

        try
        {
            var threshold = (int)_thresholdSlider.Value;
            var feather = (int)_featherSlider.Value;
            var smoothness = (int)_smoothnessSlider.Value;
            var shiftEdge = (int)_shiftSlider.Value;
            var contrast = (int)_contrastSlider.Value;
            var bgTolerance = (int)_bgKeySlider.Value;
            var keyColor = _keyColor;
            var contiguous = _contiguousCheck.IsChecked != false;
            var maskPath = _maskPath!;
            var sourcePath = _sourcePath;
            var eraseMask = _eraseMask!;
            var previewW = _w;
            var previewH = _h;

            var dir = System.IO.Path.GetDirectoryName(sourcePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var baseName = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
            var destPath = ImageConverter.GetUniqueOutputPath(dir, baseName + "_nobg", ".png");

            await System.Threading.Tasks.Task.Run(async () =>
            {
                // Full-resolution recompute so the saved file keeps original detail.
                var (pixels, w, h, _, _) = await ImageConverter.DecodeScaledPixelsAsync(sourcePath, 0);

                // Pixel-destined parameters scale with resolution so preview and
                // saved edges match by look, not by absolute pixel count.
                var scale = Math.Max(w, h) / (double)Math.Max(previewW, previewH);
                var featherFull = feather > 0 ? Math.Max(1, (int)Math.Round(feather * scale)) : 0;
                var smoothnessFull = (int)Math.Round(smoothness * scale); // keeps the /10 radius
                var shiftFull = (int)Math.Round(shiftEdge * scale);

                var mask = await ImageConverter.DecodeGrayAsync(maskPath, w, h);

                // Color-key the flat background straight at full resolution.
                var bgKey = bgTolerance > 0
                    ? BackgroundRemover.DetectBackground(pixels, w, h, bgTolerance, keyColor, contiguous)
                    : null;

                if (eraseMask.Contains((byte)0))
                {
                    var eraseFull = ScaleBilinear(eraseMask, (int)previewW, (int)previewH, (int)w, (int)h);
                    BackgroundRemover.Compose(pixels, mask, w, h, threshold, featherFull, eraseFull, smoothnessFull, contrast, shiftFull, bgKey);
                }
                else
                {
                    BackgroundRemover.Compose(pixels, mask, w, h, threshold, featherFull, null, smoothnessFull, contrast, shiftFull, bgKey);
                }

                var png = await ImageConverter.EncodePngBytesAsync(pixels, w, h);
                File.WriteAllBytes(destPath, png);
            });
            EndBusy();

            LocalizationUi.SetFormattedText(_fileNameText,
                "Saved: {0}", System.IO.Path.GetFileName(destPath));
            SetStatus("Result written next to the original file.", "#FF9BE29B");
            try { Win32.MessageBeep(0x00000040); } catch { }

            _autoCloseTimer = MQ.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _autoCloseTimer.Interval = TimeSpan.FromMilliseconds(1400);
            _autoCloseTimer.IsRepeating = false;
            _autoCloseTimer.Tick += (_, _) => { _autoCloseTimer!.Stop(); Close(); };
            _autoCloseTimer.Start();
        }
        catch (Exception ex)
        {
            Dbg.Log("BackgroundPreview save THREW: " + ex);
            EndBusy();
            SetStatus("Save failed: {0}", "#FFED6A5E", Shorten(ex.Message));
            _saveButton.IsEnabled = true;
            _cancelButton.IsEnabled = true;
            _saving = false;
        }
    }

    /// <summary>
    /// Upscales the preview-resolution eraser mask to full resolution with
    /// bilinear filtering so soft erase gradients stay smooth.
    /// </summary>
    private static byte[] ScaleBilinear(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh];
        for (var y = 0; y < dh; y++)
        {
            var fy = (y + 0.5) * sh / dh - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(fy), 0, sh - 1);
            var y1 = Math.Min(sh - 1, y0 + 1);
            var wy = Math.Clamp(fy - y0, 0, 1);
            var row = y * dw;
            var sy0 = y0 * sw;
            var sy1 = y1 * sw;
            for (var x = 0; x < dw; x++)
            {
                var fx = (x + 0.5) * sw / dw - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(fx), 0, sw - 1);
                var x1 = Math.Min(sw - 1, x0 + 1);
                var wx = Math.Clamp(fx - x0, 0, 1);

                var v00 = src[sy0 + x0];
                var v10 = src[sy0 + x1];
                var v01 = src[sy1 + x0];
                var v11 = src[sy1 + x1];
                var top = v00 + (v10 - v00) * wx;
                var bottom = v01 + (v11 - v01) * wx;
                dst[row + x] = (byte)Math.Clamp(top + (bottom - top) * wy, 0, 255);
            }
        }
        return dst;
    }

    private void OnWindowClosed()
    {
        _closed = true;
        if (s_active == this) s_active = null;
        try { _composeCts?.Cancel(); } catch { }
        _recomposeTimer?.Stop();
        _spinnerTimer?.Stop();
        _autoCloseTimer?.Stop();
        _inlineHideTimer?.Stop();
        CursorInterop.SetCursor(_viewport, null);
        var old = _eraseCursor;
        _eraseCursor = null;
        try { old?.Dispose(); } catch { }
        try
        {
            if (_maskPath is not null && File.Exists(_maskPath)) File.Delete(_maskPath);
        }
        catch { /* temp cleanup is best effort */ }
    }

    // ------------------------------------------------------------------
    // Keyboard shortcuts
    // ------------------------------------------------------------------

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_loaded) return;
        var ctrl = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;

        if (ctrl && e.Key == Windows.System.VirtualKey.Z)
        {
            UndoStroke();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Space when !e.KeyStatus.WasKeyDown:
                _spacePan = true;
                if (_pointerDown && _eraseActive) _lastStamp = ToImageCoords(_trackedPointerPos);
                UpdateViewportCursor();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.E:
                SetTool(true);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.H:
                SetTool(false);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.F:
                FitToView();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Add:
            case (Windows.System.VirtualKey)0xBB: // OEM '+'
                ZoomAtCenter(1.2);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Subtract:
            case (Windows.System.VirtualKey)0xBD: // OEM '-'
                ZoomAtCenter(1 / 1.2);
                e.Handled = true;
                break;

            case (Windows.System.VirtualKey)0xDB: // OEM '['
                _brushSlider.Value = Math.Max(_brushSlider.Minimum, _brushSlider.Value - 5);
                e.Handled = true;
                break;

            case (Windows.System.VirtualKey)0xDD: // OEM ']'
                _brushSlider.Value = Math.Min(_brushSlider.Maximum, _brushSlider.Value + 5);
                e.Handled = true;
                break;
        }
    }

    private void OnRootKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Space) return;
        _spacePan = false;
        if (_loaded && _pointerDown) _lastStamp = ToImageCoords(_trackedPointerPos);
        UpdateViewportCursor();
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // Busy indicator, undo
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

    private void SetInlineBusy(bool on)
    {
        if (on)
        {
            _inlineHideTimer?.Stop();
            if (!_inlineBusyOn)
            {
                _inlineBusyOn = true;
                _inlineShownAt = Environment.TickCount64;
            }
            _inlineBusy.Visibility = Visibility.Visible;
            UpdateBusyVisual();
            return;
        }

        if (!_inlineBusyOn) return;

        // Keep the pill up for a moment even when the recompute is instant, so
        // a slider tick never looks like nothing happened (and never flickers).
        var shownFor = Environment.TickCount64 - _inlineShownAt;
        if (shownFor >= InlineBusyMinMs)
        {
            HideInlineBusy();
            return;
        }

        _inlineHideTimer ??= CreateInlineHideTimer();
        _inlineHideTimer.Stop();
        _inlineHideTimer.Interval = TimeSpan.FromMilliseconds(InlineBusyMinMs - shownFor);
        _inlineHideTimer.Start();
    }

    private void HideInlineBusy()
    {
        _inlineHideTimer?.Stop();
        _inlineBusyOn = false;
        _inlineBusy.Visibility = Visibility.Collapsed;
        UpdateBusyVisual();
    }

    private MQ.DispatcherQueueTimer CreateInlineHideTimer()
    {
        var timer = MQ.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => HideInlineBusy();
        return timer;
    }

    private void UpdateBusyVisual()
    {
        var busy = _busyCount > 0;
        _busyBadge.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        if (busy || _inlineBusyOn)
        {
            _spinnerTimer ??= CreateSpinnerTimer();
            if (!_spinnerTimer.IsRunning) _spinnerTimer.Start();
        }
        else
        {
            _spinnerTimer?.Stop();
        }
    }

    /// <summary>
    /// Hand-built spinner (rotating arc) instead of the WinUI ProgressRing:
    /// its default template crashes this code-built unpackaged app with
    /// "Cannot find a Resource TabViewScrollButtonBackground".
    /// </summary>
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
            StrokeDashArray = new DoubleCollection { 8.5, 16.7 }, // ~1/3 arc (fallback-safe)
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

    private StackPanel MakeInlineBusy()
    {
        _inlineArc = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 14,
            Height = 14,
            Stroke = Brush(MenuTheme.AccentColor),
            StrokeThickness = 2.2,
            StrokeDashArray = new DoubleCollection { 8.5, 16.7 },
            VerticalAlignment = VerticalAlignment.Center,
        };
        _inlineRotation = new RotateTransform { Angle = 0 };
        _inlineArc.RenderTransform = _inlineRotation;
        _inlineArc.RenderTransformOrigin = new Point(0.5, 0.5);

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Children =
            {
                _inlineArc,
                new TextBlock
                {
                    Text = "Processing",
                    FontSize = 11,
                    Margin = new Thickness(7, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brush(MenuTheme.HintColor),
                },
            },
        };
    }

    private MQ.DispatcherQueueTimer CreateSpinnerTimer()
    {
        var timer = MQ.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(33);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            _spinnerRotation.Angle = (_spinnerRotation.Angle + 12) % 360;
            _inlineRotation.Angle = (_inlineRotation.Angle + 12) % 360;
        };
        return timer;
    }

    private Button MakeUndoButton()
    {
        var button = new Button
        {
            Content = "Undo",
            MinWidth = 56,
            IsEnabled = false,
        };
        ToolTipService.SetToolTip(button, "Undo the last erase stroke (Ctrl+Z)");
        button.Click += (_, _) => UndoStroke();
        return button;
    }

    private Button MakeResetButton()
    {
        var button = new Button { Content = "Reset", MinWidth = 60 };
        ToolTipService.SetToolTip(button, "Reset every adjustment and all erase strokes");
        button.Click += (_, _) => ResetAll();
        return button;
    }

    private void ResetAll()
    {
        if (!_loaded || _saving) return;
        _thresholdSlider.Value = 12;
        _featherSlider.Value = 1;
        _smoothnessSlider.Value = 0;
        _shiftSlider.Value = 0;
        _contrastSlider.Value = 0;
        _bgKeySlider.Value = 0;
        _contiguousCheck.IsChecked = true;
        _softnessSlider.Value = 20;
        _brushSlider.Value = 40;
        _keyColor = null;
        _undoStack.Clear();
        if (_eraseMask is not null) Array.Fill(_eraseMask, (byte)255);
        UpdateKeyColorChip();
        UpdateUndoState();
        RequestCompose(heavy: true);
    }

    private void UndoStroke()
    {
        if (!_loaded || _saving || _undoStack.Count == 0 || _eraseMask is null) return;
        var snapshot = _undoStack[_undoStack.Count - 1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _eraseMask = snapshot;
        UpdateUndoState();
        RequestCompose(heavy: false);
    }

    private void PushUndoState()
    {
        if (_eraseMask is null) return;
        _undoStack.Add((byte[])_eraseMask.Clone());
        if (_undoStack.Count > UndoLimit) _undoStack.RemoveAt(0);
        UpdateUndoState();
    }

    private void UpdateUndoState()
    {
        if (_undoButton is not null)
            _undoButton.IsEnabled = _loaded && _undoStack.Count > 0;
    }

    // ------------------------------------------------------------------
    // UI construction (code-built, matching MenuTheme)
    // ------------------------------------------------------------------

    private UIElement BuildLayout()
    {
        var page = new Border
        {
            Background = Brush(MenuTheme.HubFill),
            Padding = new Thickness(14),
            IsTabStop = true,
        };

        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // footer

        // ---- header: file name + inline processing pill ----
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_fileNameText);
        Grid.SetColumn(_inlineBusy, 1);
        header.Children.Add(_inlineBusy);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // ---- body: canvas (left) + inspector (right) ----
        var body = new Grid { ColumnSpacing = 12 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

        var left = new Grid { RowSpacing = 8 };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.Children.Add(BuildToolbar());
        Grid.SetRow(_viewport, 1);
        left.Children.Add(_viewport);
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var inspector = BuildInspector();
        Grid.SetColumn(inspector, 1);
        body.Children.Add(inspector);

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // ---- footer: status + actions ----
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
        };
        buttons.Children.Add(_cancelButton);
        buttons.Children.Add(_saveButton);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        page.Child = root;
        _rootBorder = page;
        return page;
    }

    private UIElement BuildToolbar()
    {
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tools.Children.Add(_eraseButton);
        tools.Children.Add(_panButton);
        tools.Children.Add(_undoButton);

        tools.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 0, 6, 0) });
        tools.Children.Add(_fitButton);
        tools.Children.Add(_resetButton);
        return tools;
    }

    private FrameworkElement BuildInspector()
    {
        var panel = new StackPanel { Spacing = 7, Padding = new Thickness(0, 0, 4, 0) };

        panel.Children.Add(SectionHeader("AI CUTOUT"));
        panel.Children.Add(MakeSliderRow(_thresholdSlider, _thresholdValue, "Cutout"));
        panel.Children.Add(MakeSliderRow(_featherSlider, _featherValue, "Feather"));
        panel.Children.Add(MakeSliderRow(_smoothnessSlider, _smoothnessValue, "Smoothness"));
        panel.Children.Add(MakeSliderRow(_shiftSlider, _shiftValue, "Shift edge"));
        panel.Children.Add(MakeSliderRow(_contrastSlider, _contrastValue, "Contrast"));

        panel.Children.Add(Separator());

        panel.Children.Add(SectionHeader("BACKGROUND KEY"));
        panel.Children.Add(MakeKeyColorRow());
        panel.Children.Add(MakeSliderRow(_bgKeySlider, _bgKeyValue, "Tolerance"));
        panel.Children.Add(new Border
        {
            Margin = new Thickness(96, 0, 0, 0),
            Child = _contiguousCheck,
        });

        panel.Children.Add(Separator());

        panel.Children.Add(SectionHeader("ERASER"));
        panel.Children.Add(MakeSliderRow(_brushSlider, _brushValue, "Brush"));
        panel.Children.Add(MakeSliderRow(_softnessSlider, _softnessValue, "Softness"));

        panel.Children.Add(new TextBlock
        {
            Text = "Wheel = zoom · Space-drag = pan · Ctrl+Z = undo",
            FontSize = 10.5,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(MenuTheme.HintColor),
        });

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    private FrameworkElement MakeKeyColorRow()
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = "Key color",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(MenuTheme.LabelColor),
            VerticalAlignment = VerticalAlignment.Center,
        });

        Grid.SetColumn(_keyColorButton, 1);
        row.Children.Add(_keyColorButton);

        Grid.SetColumn(_pickColorButton, 2);
        row.Children.Add(_pickColorButton);
        return row;
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        CharacterSpacing = 60,
        Foreground = Brush(MenuTheme.HintColor),
        Margin = new Thickness(0, 2, 0, 0),
    };

    private static Border Separator() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 3, 0, 3),
        Background = Brush("#FF2C3140"),
    };

    private Grid MakeViewport()
    {
        var viewport = new Grid
        {
            Background = Brush("#FF16181F"),
            MinHeight = 420,
            MinWidth = 380,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(viewport, "Viewport");
        viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, 0, 0) };
        viewport.SizeChanged += (_, e) =>
        {
            viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            if (_fitPending) FitToView();
        };

        // Transparency checkerboard behind the picture (square cells at any
        // viewport aspect because it is drawn UniformToFill).
        viewport.Children.Add(_checkerImage);

        var stage = _stage;
        viewport.Children.Add(stage);

        // Overlay (busy ring) sits above the picture, never eats input.
        var overlay = new Grid { IsHitTestVisible = false };
        overlay.Children.Add(_busyBadge);
        viewport.Children.Add(overlay);

        viewport.PointerPressed += OnViewportPressed;
        viewport.PointerMoved += OnViewportMoved;
        viewport.PointerReleased += OnViewportReleased;
        viewport.PointerCanceled += OnViewportReleased;
        viewport.PointerCaptureLost += (_, _) => { _pointerDown = false; };
        viewport.PointerWheelChanged += OnViewportWheel;
        viewport.PointerEntered += (_, _) =>
        {
            _inViewport = true;
            if (_pointerDown) _needNewStroke = true;
            UpdateViewportCursor();
        };
        viewport.PointerExited += (_, _) =>
        {
            _inViewport = false;
            UpdateViewportCursor();
        };
        return viewport;
    }

    private Canvas MakeStage()
    {
        var stage = new Canvas();
        var transform = new CompositeTransform();
        stage.RenderTransform = transform;
        Canvas.SetLeft(_image, 0);
        Canvas.SetTop(_image, 0);
        stage.Children.Add(_image);
        return stage;
    }

    private Image MakePreviewImage() => new()
    {
        Stretch = Stretch.None, // the stage transform handles zoom/fit
    };

    private static Image MakeCheckerImage()
    {
        var image = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        _ = LoadCheckerAsync(image);
        return image;
    }

    private static async System.Threading.Tasks.Task LoadCheckerAsync(Image target)
    {
        try
        {
            var bytes = await System.Threading.Tasks.Task.Run(BuildCheckerPng);
            var stream = await ImageConverter.BytesToWinRtStreamAsync(bytes);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            target.Source = bitmap;
        }
        catch (Exception ex)
        {
            Dbg.Log("BackgroundPreview checker THREW: " + ex.Message);
        }
    }

    /// <summary>Builds a 1024² transparency checkerboard PNG (24 px cells).</summary>
    private static byte[] BuildCheckerPng()
    {
        const int size = 1024;
        const int cell = 24;
        using var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(255, 0x18, 0x1B, 0x23));
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 0x26, 0x2B, 0x37));
            for (var y = 0; y < size; y += cell)
            {
                for (var x = 0; x < size; x += cell)
                {
                    if (((x / cell) + (y / cell)) % 2 == 0)
                        g.FillRectangle(brush, x, y, cell, cell);
                }
            }
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    private Button MakeToolButton(string label, bool erase)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 72,
        };
        button.Click += (_, _) =>
        {
            if (_loaded) SetTool(erase);
        };
        return button;
    }

    private Button MakeFitButton()
    {
        var button = new Button
        {
            Content = "Fit",
            MinWidth = 56,
        };
        ToolTipService.SetToolTip(button, "Fit the picture in the view (F)");
        button.Click += (_, _) => { if (_loaded) FitToView(); };
        return button;
    }

    private Slider MakeBrushSlider()
    {
        var slider = new Slider
        {
            Minimum = 1,
            Maximum = 200,
            StepFrequency = 1,
            Value = 40,
        };
        slider.ValueChanged += (_, e) =>
        {
            _brushValue.Text = ((int)e.NewValue) + " px";
            UpdateViewportCursor();
        };
        return slider;
    }

    private Slider MakeSoftnessSlider()
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            StepFrequency = 1,
            Value = 20,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        slider.ValueChanged += (_, e) =>
        {
            _softnessValue.Text = ((int)e.NewValue) + "%";
        };
        return slider;
    }

    private Slider MakeSmoothnessSlider()
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            StepFrequency = 1,
            Value = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        slider.ValueChanged += (_, e) =>
        {
            _smoothnessValue.Text = ((int)e.NewValue).ToString();
            if (_loaded) RequestCompose(heavy: true);
        };
        return slider;
    }

    private Slider MakeShiftSlider()
    {
        var slider = new Slider
        {
            Minimum = -50,
            Maximum = 50,
            StepFrequency = 1,
            Value = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        slider.ValueChanged += (_, e) =>
        {
            var v = (int)e.NewValue;
            _shiftValue.Text = v > 0 ? "+" + v : v.ToString();
            if (_loaded) RequestCompose(heavy: true);
        };
        return slider;
    }

    private Slider MakeContrastSlider()
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            StepFrequency = 1,
            Value = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        slider.ValueChanged += (_, e) =>
        {
            _contrastValue.Text = ((int)e.NewValue).ToString();
            if (_loaded) RequestCompose(heavy: true);
        };
        return slider;
    }

    private Slider MakeBgKeySlider()
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            StepFrequency = 1,
            Value = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        slider.ValueChanged += (_, e) =>
        {
            var v = (int)e.NewValue;
            _bgKeyValue.Text = v == 0 ? "off" : v.ToString();
            if (_loaded) RequestCompose(heavy: false);
        };
        return slider;
    }

    private CheckBox MakeContiguousCheck()
    {
        var check = new CheckBox
        {
            Content = "Contiguous only",
            IsChecked = true,
            FontSize = 12,
            MinWidth = 0,
            MinHeight = 26,
            Padding = new Thickness(0, 0, 0, 0),
        };
        ToolTipService.SetToolTip(check,
            "On: only the background touching the image border is removed.\n" +
            "Off: every pixel of the chosen color is removed, enclosed or not.");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, "Contiguous only");
        check.Checked += (_, _) => { if (_loaded) RequestCompose(heavy: false); };
        check.Unchecked += (_, _) => { if (_loaded) RequestCompose(heavy: false); };
        return check;
    }

    private Slider MakeCutoutSlider()
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 128,
            StepFrequency = 1,
            Value = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        slider.ValueChanged += (_, e) =>
        {
            _thresholdValue.Text = ((int)e.NewValue).ToString();
            if (_loaded) RequestCompose(heavy: true);
        };
        return slider;
    }

    private Slider MakeFeatherSlider()
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 10,
            StepFrequency = 1,
            Value = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        slider.ValueChanged += (_, e) =>
        {
            _featherValue.Text = ((int)e.NewValue) + " px";
            if (_loaded) RequestCompose(heavy: true);
        };
        return slider;
    }

    private Grid MakeSliderRow(Slider slider, TextBlock value, string label)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Brush(MenuTheme.LabelColor),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Compact slider, no descriptive caption below.
        slider.Height = 24;
        slider.MinHeight = 24;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, label);
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);

        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }

    private TextBlock MakeFileNameText() => new()
    {
        Text = "Preparing preview…",
        FontSize = 14,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Brush(MenuTheme.LabelColor),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private TextBlock MakeStatusText() => new()
    {
        Text = "",
        FontSize = 12,
        Foreground = Brush(MenuTheme.HintColor),
        TextWrapping = TextWrapping.Wrap,
        MaxLines = 2,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };

    private static TextBlock MakeSliderValue() => new()
    {
        FontSize = 12.5,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Brush(MenuTheme.LabelColor),
        VerticalAlignment = VerticalAlignment.Center,
        MinWidth = 38,
        TextAlignment = TextAlignment.Right,
    };

    private Button MakeSaveButton()
    {
        var button = new Button
        {
            Content = "Save",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x14, 0x18)),
            Background = Brush(MenuTheme.AccentColor),
            MinWidth = 96,
        };
        button.Click += (_, _) => OnSaveClicked();
        return button;
    }

    private Button MakeCancelButton()
    {
        var button = new Button
        {
            Content = "Cancel",
            MinWidth = 96,
        };
        button.Click += (_, _) => Close();
        return button;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void UpdateControls(bool loaded, bool keepButtonsOff = false)
    {
        _eraseButton.IsEnabled = loaded;
        _panButton.IsEnabled = loaded;
        _brushSlider.IsEnabled = loaded;
        _softnessSlider.IsEnabled = loaded;
        _smoothnessSlider.IsEnabled = loaded;
        _shiftSlider.IsEnabled = loaded;
        _contrastSlider.IsEnabled = loaded;
        _bgKeySlider.IsEnabled = loaded;
        _contiguousCheck.IsEnabled = loaded;
        _keyColorButton.IsEnabled = loaded;
        _pickColorButton.IsEnabled = loaded;
        _fitButton.IsEnabled = loaded;
        _resetButton.IsEnabled = loaded;
        _thresholdSlider.IsEnabled = loaded;
        _featherSlider.IsEnabled = loaded;
        _saveButton.IsEnabled = loaded && !keepButtonsOff;
        _cancelButton.IsEnabled = !keepButtonsOff;
        UpdateUndoState();
        UpdateViewportCursor();
    }

    private void SetStatus(string message, string colorHex, params object?[] args)
    {
        if (args.Length == 0) LocalizationUi.SetText(_status, message);
        else LocalizationUi.SetFormattedText(_status, message, args);
        _status.Foreground = Brush(colorHex);
        _status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

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
