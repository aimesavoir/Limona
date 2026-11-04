using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Limona.Interop;
using Limona.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;

using MQ = Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using ZIndex = Microsoft.UI.Xaml.Controls.Canvas;

namespace Limona.Menu;

/// <summary>
/// Simple audio editor opened by the "Edit Audio" slice: waveform overview,
/// drag the A/B markers, Trim with Undo, then export in a chosen
/// format (MP3 / WAV / M4A / OGG) with a live progress bar.
/// </summary>
internal sealed class AudioEditWindow : Window
{
    private const int WaveW = 900;   // waveform canvas pixels
    private const int WaveH = 200;

    private static AudioEditWindow? s_active;

    private readonly string _sourcePath;
    private readonly string _fileName;
    private bool _exporting;
    private double _sourceSeconds;

    // Sample buffer (48 kHz int16 interleaved). Every Trim compacts the
    // buffer, so the timeline ALWAYS spans exactly the kept audio and the
    // A/B markers naturally sit at its start and end.
    private short[] _pcm = Array.Empty<short>();
    private int _rate = 48000;
    private int _channels = 2;

    // Undo snapshots: previous buffer + marker positions.
    private readonly List<(short[] Buf, double MarkA, double MarkB)> _undoStack = new();

    // Draggable marker lines: A = start (in), B = end (out) of the region
    // the user wants to trim. Both are always defined (0..1 of the width).
    private double _markA, _markB;

    private enum MarkDrag { None, Rubber, HandleA, HandleB }
    private MarkDrag _dragBody = MarkDrag.None;
    private double _pressedFrac;
    private double _prevMarkA, _prevMarkB;
    private bool _dragging;

    private TextBlock _titleText = null!;
    private TextBlock _sourceInfo = null!;
    private Image _waveImage = null!;
    private Grid _wavePanel = null!;
    private Border _selectionOverlay = null!;
    private Grid _markerAGrid = null!;
    private Grid _markerBGrid = null!;
    private TextBlock _timeALabel = null!;
    private TextBlock _timeBLabel = null!;
    private TextBlock _timeInfo = null!;
    private Button _playButton = null!;
    private Button _pauseButton = null!;
    private Button _stopButton = null!;
    private Button _trimButton = null!;
    private Button _undoButton = null!;
    private ComboBox _formatBox = null!;
    private ComboBox _qualityBox = null!;
    private TextBlock _status = null!;
    private TextBlock _progressPct = null!;
    private Border _progressHost = null!;
    private Button _exportButton = null!;
    private Button _closeButton = null!;

    private ColumnDefinition _fillColumn = null!;
    private ColumnDefinition _emptyColumn = null!;
    private byte[] _wavePixels = Array.Empty<byte>();

    // Playback (NAudio): plays ONLY the A→B region (from the A marker to the
    // B marker); Pause freezes mid-stream, Stop rewinds to the A marker.
    private NAudio.Wave.WasapiOut? _waveOut;
    private NAudio.Wave.BufferedWaveProvider? _waveBuffer;
    private Border _playLine = null!;
    private MQ.DispatcherQueueTimer? _playTimer;
    private int _regionStartFrames, _regionEndFrames;

    /// <summary>Opens the editor (single instance at a time).</summary>
    public static void ShowForFile(string sourcePath)
    {
        s_active = ToolWindowCoordinator.Open(() => new AudioEditWindow(sourcePath));
    }

    private AudioEditWindow(string sourcePath)
    {
        _sourcePath = sourcePath;
        _fileName = System.IO.Path.GetFileName(sourcePath);

        WindowFit.SetTitle(this, "Edit Audio — " + _fileName);
        _titleText = MakeTitle();
        _sourceInfo = MakeHintText();
        _waveImage = MakeWaveImage();
        _wavePanel = MakeWavePanel();
        _timeALabel = MakeTimeLabel();
        _timeBLabel = MakeTimeLabel();
        _timeInfo = MakeTimeInfo();
        _playButton = MakeRoundIconButton(Icons.Play(), "Play from A marker", 38, MenuTheme.AccentColor);
        _pauseButton = MakeRoundIconButton(Icons.Pause(), "Pause here", 30);
        _stopButton = MakeRoundIconButton(Icons.Stop(), "Stop — back to start", 30);
        _trimButton = MakeRoundIconButton(Icons.Cut(), "Trim — keep A→B", 34, MenuTheme.AccentColor);
        _undoButton = MakeRoundIconButton(Icons.Undo(), "Undo trim", 30);
        _formatBox = MakeCombo(new[] { "MP3", "WAV", "M4A", "OGG" }, 0);
        _qualityBox = MakeCombo(new[] { "64 kbps", "96 kbps", "128 kbps", "192 kbps", "256 kbps" }, 3);
        _status = MakeStatusText();
        _progressPct = new TextBlock
        {
            Text = "0 %",
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 42,
            FontSize = 12,
            Foreground = Brush(MenuTheme.HintColor),
        };
        _exportButton = MakeAccentButton("Export");
        _closeButton = MakeCancelButton();
        _trimButton.Click += (_, _) => _ = OnTrimClicked();
        _undoButton.Click += (_, _) => OnUndoClicked();
        _playButton.Click += (_, _) => OnPlayClicked();
        _pauseButton.Click += (_, _) => OnPauseClicked();
        _stopButton.Click += (_, _) => StopPlayback();
        _formatBox.SelectionChanged += (_, _) => UpdateQualityRow();
        _exportButton.Click += (_, _) => _ = OnExportClicked();
        _closeButton.Click += (_, _) => Close();

        Content = BuildLayout();
        WindowFit.Apply(this, (FrameworkElement)Content!);

        Closed += (_, _) => StopPlayback();

        _ = PrepareAsync();
    }

    // ------------------------------------------------------------------
    // Playback (NAudio): Play / Pause(point) / Stop(back to start)
    // ------------------------------------------------------------------

    private void OnPlayClicked()
    {
        if (_pcm.Length == 0) return;
        if (!HasRegion)
        {
            SetStatus("Drag the A/B markers around the part to play.", MenuTheme.AccentDeep);
            return;
        }
        try
        {
            var sF = SelStartSample();
            var eF = SelEndSample();
            if (eF - sF < 256)
            {
                SetStatus("The A→B range is too small to play.", MenuTheme.AccentDeep);
                return;
            }
            if (_waveOut is null)
            {
                // Fresh device: feed ONLY the A→B segment of the buffer.
                _waveOut?.Dispose();
                _regionStartFrames = sF;
                _regionEndFrames = eF;
                var seconds = (eF - sF) / (double)_rate;
                _waveBuffer = new NAudio.Wave.BufferedWaveProvider(
                    new NAudio.Wave.WaveFormat(_rate, 16, _channels))
                {
                    BufferDuration = TimeSpan.FromSeconds(seconds + 10),
                    DiscardOnBufferOverflow = false,
                };
                var bytes = new byte[(eF - sF) * _channels * 2];
                System.Buffer.BlockCopy(
                    _pcm, sF * _channels * 2, bytes, 0, bytes.Length);
                _waveBuffer.AddSamples(bytes, 0, bytes.Length);
                _waveOut = new NAudio.Wave.WasapiOut(
                    NAudio.CoreAudioApi.AudioClientShareMode.Shared, 120);
                _waveOut.Init(_waveBuffer);
                _waveOut.PlaybackStopped += (_, _) => { if (_waveOut is not null) StopPlayback(); };
                _waveOut.Play();
            }
            else if (_waveOut.PlaybackState == NAudio.Wave.PlaybackState.Paused)
            {
                _waveOut.Play(); // resume exactly where it paused
            }
            UpdatePlayButtons();
            StartPlayTimer();
        }
        catch (Exception ex)
        {
            Dbg.Log("AudioEdit play THREW: " + ex);
            SetStatus("Playback failed: {0}", MenuTheme.AccentDeep, Shorten(ex.Message));
        }
    }

    private void OnPauseClicked()
    {
        if (_waveOut is null) return;
        if (_waveOut.PlaybackState == NAudio.Wave.PlaybackState.Playing)
        {
            _waveOut.Pause(); // stays at this position
            UpdatePlayButtons();
            SetStatus("Paused at {0}.", MenuTheme.HintColor, FormatTimePlayed());
        }
        else if (_waveOut.PlaybackState == NAudio.Wave.PlaybackState.Paused)
        {
            _waveOut.Play();
            UpdatePlayButtons();
        }
    }

    private void StopPlayback()
    {
        _playTimer?.Stop();
        try { _waveOut?.Stop(); } catch { }
        try { _waveOut?.Dispose(); } catch { }
        _waveOut = null;
        _waveBuffer = null;
        _playLine.Visibility = Visibility.Collapsed;
        UpdatePlayButtons();
    }

    /// <summary>Trims replace the buffer: drop the device, play restarts at A.</summary>
    private void InvalidatePlaybackBuffer() => StopPlayback();

    private int PlayedFrames()
    {
        if (_waveOut is null) return 0;
        var bytes = Math.Max(0, _waveOut.GetPosition());
        return (int)(bytes / (2 * Math.Max(1, _channels)));
    }

    private string FormatTimePlayed() =>
        FormatTime(Math.Min(_regionEndFrames, _regionStartFrames + PlayedFrames()));

    private MQ.DispatcherQueueTimer StartPlayTimer()
    {
        if (_playTimer is null)
        {
            _playTimer = MQ.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _playTimer.Interval = TimeSpan.FromMilliseconds(60);
            _playTimer.IsRepeating = true;
            _playTimer.Tick += (_, _) => OnPlayTick();
        }
        _playTimer.Start();
        return _playTimer;
    }

    private void OnPlayTick()
    {
        if (_waveOut is null) return;
        var played = PlayedFrames();
        var totalFrames = _pcm.Length / Math.Max(1, _channels);
        var absFrame = _regionStartFrames + played;
        var frac = totalFrames <= 0 ? 0 : Math.Clamp(absFrame / (double)totalFrames, 0, 1);
        _playLine.Margin = new Thickness((int)Math.Round(frac * WaveW), 0, 0, 0);
        _playLine.Visibility = Visibility.Visible;
        if (played >= _regionEndFrames - _regionStartFrames - 16)
        {
            // Reached the B marker: stop and rewind to A for the next Play.
            StopPlayback();
        }
    }

    private void UpdatePlayButtons()
    {
        var hasDevice = _waveOut is not null;
        _playButton.IsEnabled = _pcm.Length > 0 &&
            (!hasDevice || _waveOut!.PlaybackState == NAudio.Wave.PlaybackState.Paused);
        _pauseButton.IsEnabled = hasDevice;
        _stopButton.IsEnabled = hasDevice;
    }

    // ------------------------------------------------------------------
    // Preparation: decode + render waveform
    // ------------------------------------------------------------------

    private async System.Threading.Tasks.Task PrepareAsync()
    {
        LocalizationUi.SetFormattedText(_sourceInfo, "Source: {0} — decoding...", _fileName);
        SetStatus("", MenuTheme.HintColor);
        try
        {
            var decoded = await System.Threading.Tasks.Task.Run(
                () => AudioConverter.DecodeToPcmAsync(_sourcePath));
            _pcm = decoded.Pcm;
            _rate = decoded.Rate;
            _channels = decoded.Channels;
            _sourceSeconds = _pcm.Length / (double)(_rate * _channels);
            // Open state: A at the very start, B at the very end.
            _markA = 0;
            _markB = 1;
            LocalizationUi.SetFormattedText(_sourceInfo,
                "Source: {0} — {1} s — {2} — 48 kHz",
                _fileName,
                _sourceSeconds.ToString("0.0#"),
                LocalizationUi.Localized(_channels == 1 ? "Mono" : "Stereo"));
            UpdateTimeInfo();
            UpdateMarkers(null);
            RenderWaveform();
            UpdateButtonsState();
        }
        catch (Exception ex)
        {
            Dbg.Log("AudioEditWindow prepare THREW: " + ex);
            LocalizationUi.SetFormattedText(_sourceInfo,
                "Source: {0} — failed: {1}", _fileName, Shorten(ex.Message));
        }
    }

    // ------------------------------------------------------------------
    // Waveform rendering (hand-drawn into a pixel buffer → SoftwareBitmap)
    // ------------------------------------------------------------------

    private Image MakeWaveImage() => new()
    {
        Width = WaveW,
        Height = WaveH,
        Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private Grid MakeWavePanel()
    {
        var panel = new Grid
        {
            Width = WaveW,
            Height = WaveH,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(_waveImage);
        _selectionOverlay = new Border
        {
            Background = Brush("#3D" + MenuTheme.AccentColor[3..]),
            BorderBrush = Brush("#88" + MenuTheme.AccentColor[3..]),
            BorderThickness = new Thickness(0, 0, 1, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
        };
        _playLine = new Border
        {
            Background = Brush(MenuTheme.AccentColor),
            Width = 2,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
        };
        _markerAGrid = MakeMarker("A");
        _markerBGrid = MakeMarker("B");
        panel.Children.Add(_selectionOverlay);
        panel.Children.Add(_playLine);
        panel.Children.Add(_markerAGrid);
        panel.Children.Add(_markerBGrid);

        ZIndex.SetZIndex(_selectionOverlay, 4);
        ZIndex.SetZIndex(_markerAGrid, 5);
        ZIndex.SetZIndex(_markerBGrid, 5);
        ZIndex.SetZIndex(_playLine, 6);
        _waveImage.PointerPressed += Wave_PointerPressed;
        _waveImage.PointerMoved += Wave_PointerMoved;
        _waveImage.PointerReleased += Wave_PointerReleased;
        return panel;
    }

    /// <summary>
    /// A clearly visible vertical marker line with a lettered grab cap on top
    /// (A / B) and a wide hit zone. The grid is 26 px wide so the line centers
    /// on the marker x; IsHitTestVisible stays false (the wave image handles
    /// all pointer logic).
    /// </summary>
    private Grid MakeMarker(string letter)
    {
        var line = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x9E, 0xC1, 0xF5)),
        };
        var cap = new Border
        {
            Width = 26,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x3D, 0x5A, 0x88)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x9E, 0xC1, 0xF5)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = letter,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xEA, 0xF2, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        return new Grid
        {
            Width = 26,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            Children = { line, cap },
        };
    }

    private bool _waveHoverCursorSet;

    private void Wave_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_pcm.Length == 0) return;
        _ = _waveImage.CapturePointer(e.Pointer);
        var frac = FractionFromPointer(e);
        _pressedFrac = frac;
        _prevMarkA = _markA;
        _prevMarkB = _markB;
        _dragging = true;

        // Grab the nearest marker line within ~12 px; otherwise start a fresh
        // rubber-band selection (a plain click later nudges the nearer marker).
        var handle = NearestHandle(frac);
        if (handle == MarkDrag.None)
        {
            handle = MarkDrag.Rubber;
            _markA = frac;
            _markB = frac;
        }
        _dragBody = handle;
        UpdateMarkers(e);
    }

    private void Wave_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            // Hover feedback: swap the cursor near a grabbable line.
            var near = NearestHandle(FractionFromPointer(e)) != MarkDrag.None;
            if (near != _waveHoverCursorSet)
            {
                _waveHoverCursorSet = near;
                try
                {
                    var shape = near
                        ? Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast
                        : Microsoft.UI.Input.InputSystemCursorShape.Arrow;
                    CursorInterop.SetCursor(_waveImage, Microsoft.UI.Input.InputSystemCursor.Create(shape));
                }
                catch { /* cursor API may not be available on all hosts */ }
            }
            return;
        }
        UpdateMarkers(e);
    }

    private void Wave_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _waveImage.ReleasePointerCapture(e.Pointer);
        _dragging = false;
        var frac = FractionFromPointer(e);
        var wasClick = Math.Abs(frac - _pressedFrac) * WaveW < 3 && _dragBody == MarkDrag.Rubber;
        if (wasClick)
        {
            // Plain click: keep marks as they were, jump the NEARER marker
            // there — a quick refine without dragging.
            _markA = _prevMarkA;
            _markB = _prevMarkB;
            var dA = Math.Abs(frac * WaveW - Math.Clamp(_markA, 0, 1) * WaveW);
            var dB = Math.Abs(frac * WaveW - Math.Clamp(_markB, 0, 1) * WaveW);
            if (dB < dA) _markB = frac; else _markA = frac;
        }
        else if (_dragBody == MarkDrag.Rubber)
        {
            // Allow either direction; marks normalize in the overlay/info.
            _markB = frac;
        }
        _dragBody = MarkDrag.None;
        UpdateMarkers(e);
        UpdateButtonsState();
    }

    private double FractionFromPointer(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_waveImage).Position.X / WaveW;
        return Math.Clamp(point, 0.0, 1.0);
    }

    /// <summary>Which grabbable element is closest to the pointer (within 12 px)?</summary>
    private MarkDrag NearestHandle(double frac)
    {
        var x = frac * WaveW;
        MarkDrag best = MarkDrag.None;
        var bestDist = 12.0;
        void Consider(double f, MarkDrag mode)
        {
            var d = Math.Abs(x - f * WaveW);
            if (d < bestDist)
            {
                bestDist = d;
                best = mode;
            }
        }
        Consider(_markA, MarkDrag.HandleA);
        Consider(_markB, MarkDrag.HandleB);
        return best;
    }

    private void UpdateMarkers(PointerRoutedEventArgs? e)
    {
        if (_dragBody != MarkDrag.None && e is not null)
        {
            var frac = FractionFromPointer(e);
            switch (_dragBody)
            {
                case MarkDrag.Rubber: _markB = frac; break;
                case MarkDrag.HandleA: _markA = frac; break;
                case MarkDrag.HandleB: _markB = frac; break;
            }
        }
        UpdateSelectionOverlay();
        UpdateHandleLines();
        UpdateTimeInfo();
    }

    private double ClampHandle(double f) => Math.Clamp(f, 0.0, 1.0);

    private void UpdateSelectionOverlay()
    {
        var a = Math.Min(ClampHandle(_markA), ClampHandle(_markB));
        var b = Math.Max(ClampHandle(_markA), ClampHandle(_markB));
        var x1 = (int)Math.Round(a * WaveW);
        var x2 = (int)Math.Round(b * WaveW);
        if (x2 - x1 < 3)
        {
            _selectionOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        _selectionOverlay.Margin = new Thickness(x1, 0, 0, 0);
        _selectionOverlay.Width = x2 - x1;
        _selectionOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateHandleLines()
    {
        _markerAGrid.Margin = new Thickness(
            (int)Math.Round(ClampHandle(_markA) * WaveW) - 13, 0, 0, 0);
        _markerBGrid.Margin = new Thickness(
            (int)Math.Round(ClampHandle(_markB) * WaveW) - 13, 0, 0, 0);
        var framesPerSample = _pcm.Length / Math.Max(1, _channels);
        var aLabel = FormatTime((int)(ClampHandle(_markA) * framesPerSample));
        var bLabel = FormatTime((int)(ClampHandle(_markB) * framesPerSample));
        LocalizationUi.SetFormattedText(_timeALabel, "start {0}", aLabel);
        LocalizationUi.SetFormattedText(_timeBLabel, "end {0}", bLabel);
        var loaded = _pcm.Length > 0;
        _timeALabel.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        _timeBLabel.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        _timeALabel.Margin = new Thickness(
            Math.Clamp((int)(ClampHandle(_markA) * WaveW) - 45, 0, WaveW - 90), 0, 0, 0);
        _timeBLabel.Margin = new Thickness(
            Math.Clamp((int)(ClampHandle(_markB) * WaveW) - 45, 0, WaveW - 90), 0, 0, 0);
    }

    /// <summary>Paints the waveform of the current (kept) audio buffer.</summary>
    private void RenderWaveform()
    {
        var pixels = new byte[WaveW * WaveH * 4];
        var frames = _pcm.Length / Math.Max(1, _channels);

        // background
        byte bgB = 0x2A, bgG = 0x1E, bgR = 0x1B, bgA = 0xFF;
        byte wfB = 0x5A, wfG = 0xB3, wfR = 0xE5;
        byte midB = 0x54, midG = 0x41, midR = 0x3A;

        var mid = WaveH / 2;
        for (var y = 0; y < WaveH; y++)
        {
            for (var x = 0; x < WaveW; x++)
            {
                var i = (y * WaveW + x) * 4;
                pixels[i] = bgB; pixels[i + 1] = bgG; pixels[i + 2] = bgR; pixels[i + 3] = bgA;
            }
        }
        // center axis line
        for (var x = 0; x < WaveW; x++)
        {
            var i = (mid * WaveW + x) * 4;
            pixels[i] = midB; pixels[i + 1] = midG; pixels[i + 2] = midR; pixels[i + 3] = 0xFF;
        }

        var framesPerCol = Math.Max(1, frames / WaveW);
        for (var x = 0; x < WaveW; x++)
        {
            var f0 = x * framesPerCol;
            var f1 = Math.Min(frames, f0 + framesPerCol);
            if (f0 >= f1) break;
            var peak = 0;
            for (var f = f0; f < f1; f++)
            {
                var v = Math.Abs(_pcm[f * _channels]);
                if (v > peak) peak = v;
            }
            var half = (int)Math.Max(1, peak / 32768.0 * (WaveH / 2 - 4));
            for (var y = mid - half; y <= mid + half; y++)
            {
                var i = (y * WaveW + x) * 4;
                pixels[i] = wfB; pixels[i + 1] = wfG; pixels[i + 2] = wfR; pixels[i + 3] = 0xFF;
            }
        }

        _wavePixels = pixels;
        _ = RenderWaveBitmap();
    }

    private async System.Threading.Tasks.Task RenderWaveBitmap()
    {
        // Serialize bitmap creation; only the latest pixels matter.
        var (w, h, data) = (WaveW, WaveH, (byte[])_wavePixels.Clone());
        await System.Threading.Tasks.Task.Run(() =>
        {
            var sb = ImageConverter.CreateSoftwareBitmapFromPixels(data, (uint)w, (uint)h);
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var source = new SoftwareBitmapSource();
                    _ = source.SetBitmapAsync(sb).AsTask().ContinueWith(_ => sb.Dispose());
                    _waveImage.Source = source;
                }
                catch (Exception ex)
                {
                    Dbg.Log("AudioEdit wave bitmap THREW: " + ex);
                }
            });
        });
    }

    // ------------------------------------------------------------------
    // Edit operations
    // ------------------------------------------------------------------

    private int SelStartSample() =>
        (int)(Math.Min(ClampHandle(_markA), ClampHandle(_markB)) *
              (_pcm.Length / Math.Max(1, _channels)));

    private int SelEndSample() =>
        (int)(Math.Max(ClampHandle(_markA), ClampHandle(_markB)) *
              (_pcm.Length / Math.Max(1, _channels)));

    private bool HasRegion => _pcm.Length > 0 &&
        Math.Abs(ClampHandle(_markA) - ClampHandle(_markB)) * WaveW > 3;

    private void PushUndo()
    {
        _undoStack.Add((_pcm, _markA, _markB));
        if (_undoStack.Count > 20) _undoStack.RemoveAt(0);
    }

    private async System.Threading.Tasks.Task OnTrimClicked()
    {
        if (!HasRegion)
        {
            SetStatus("Drag the A/B handles around the region to keep.", MenuTheme.AccentDeep);
            return;
        }
        var s = SelStartSample();
        var e = SelEndSample();
        var keptFrames = e - s;
        if (keptFrames < 500)
        {
            SetStatus("The selected range is too short.", MenuTheme.AccentDeep);
            return;
        }
        PushUndo();
        InvalidatePlaybackBuffer();
        await System.Threading.Tasks.Task.Run(() =>
        {
            // Physically compact: the new timeline starts at A's position and
            // ends at B's position; removed audio leaves the buffer entirely.
            var kept = new short[keptFrames * _channels];
            Array.Copy(_pcm, s * _channels, kept, 0, keptFrames * _channels);
            _pcm = kept;
        });
        // Markers to the edges of the new audio.
        _markA = 0;
        _markB = 1;
        UpdateMarkers(null);
        RenderWaveform();
        UpdateTimeInfo();
        UpdateButtonsState();
        SetStatus("Trimmed — A/B are now at the edges of the new audio.", "#FF9BE29B");
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void OnUndoClicked()
    {
        if (_undoStack.Count == 0) return;
        var (buf, markA, markB) = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        InvalidatePlaybackBuffer();
        _pcm = buf;
        _markA = markA;
        _markB = markB;
        UpdateMarkers(null);
        RenderWaveform();
        UpdateButtonsState();
        SetStatus("Undo applied.", MenuTheme.HintColor);
    }

    private void UpdateButtonsState()
    {
        var loaded = _pcm.Length > 0;
        _trimButton.IsEnabled = loaded && HasRegion;
        _undoButton.IsEnabled = _undoStack.Count > 0;
        _exportButton.IsEnabled = loaded;
        UpdatePlayButtons();
    }

    private void UpdateTimeInfo()
    {
        if (_pcm.Length == 0)
        {
            LocalizationUi.SetText(_timeInfo, "—");
            return;
        }
        var a = FormatTime(SelStartSample());
        var b = FormatTime(SelEndSample());
        LocalizationUi.SetFormattedText(_timeInfo,
            "A {0} → B {1} · trim keeps {2} of audio",
            a, b, FormatTime(SelEndSample() - SelStartSample()));
    }

    private string FormatTime(int sampleIndex)
    {
        var seconds = sampleIndex / (double)_rate;
        if (seconds >= 60)
        {
            var m = (int)(seconds / 60);
            return $"{m}:{seconds - m * 60:00.0#}";
        }
        return $"{seconds:0.0#} s";
    }

    // ------------------------------------------------------------------
    // Export
    // ------------------------------------------------------------------

    private int ParsedBitrateKbps() => int.TryParse(
        (_qualityBox.SelectedItem as string ?? "").Split(' ')[0],
        System.Globalization.CultureInfo.InvariantCulture, out var kbps) ? kbps : 192;

    private void UpdateQualityRow()
    {
        var isWav = (_formatBox.SelectedItem as string) == "WAV";
        // WAV gets no bitrate; swap options for context.
        _qualityBox.Items.Clear();
        if (isWav)
        {
            foreach (var item in new[] { "44100 Hz", "48000 Hz" }) _qualityBox.Items.Add(item);
            _qualityBox.SelectedIndex = 1;
        }
        else
        {
            foreach (var item in new[] { "64 kbps", "96 kbps", "128 kbps", "192 kbps", "256 kbps" }) _qualityBox.Items.Add(item);
            _qualityBox.SelectedIndex = 3;
        }
    }

    private async System.Threading.Tasks.Task OnExportClicked()
    {
        if (_exporting || _pcm.Length == 0) return;

        var format = (_formatBox.SelectedItem as string) ?? "MP3";
        var settings = new AudioConvertSettings(
            BitrateKbps: ParsedBitrateKbps(),
            SampleRate: 48000,
            BitsPerSample: 16u,
            Channels: _channels == 1 ? 1 : 2);

        _exporting = true;
        _exportButton.IsEnabled = false;
        ShowProgress(0);

        try
        {
            SetStatus("Preparing {0} export…", MenuTheme.AccentColor, format);
            var targetExt = "." + format.ToLowerInvariant();
            var step = new Progress<double>(UpdateProgress);
            var resultPath = await System.Threading.Tasks.Task.Run(() =>
                // The buffer is already trimmed to start→end, save it whole.
                AudioConverter.EncodePcmFromSourceAsync(
                    _pcm, _rate, _channels, _sourcePath, targetExt, settings, step));
            UpdateProgress(1.0);

            var savedName = System.IO.Path.GetFileName(resultPath);
            var savedBytes = new System.IO.FileInfo(resultPath).Length;
            SetStatus("Saved: {0} · {1}", "#FF9BE29B", savedName, FormatKb(savedBytes));
            try { Win32.MessageBeep(0x00000040); } catch { }

            var closeTimer = MQ.DispatcherQueue.GetForCurrentThread().CreateTimer();
            closeTimer.Interval = TimeSpan.FromMilliseconds(1800);
            closeTimer.IsRepeating = false;
            closeTimer.Tick += (_, _) => { closeTimer.Stop(); Close(); };
            closeTimer.Start();
        }
        catch (Exception ex)
        {
            Dbg.Log("AudioEditWindow export THREW: " + ex);
            SetStatus("Export failed: {0}", MenuTheme.AccentDeep, Shorten(ex.Message));
            _progressHost.Visibility = Visibility.Collapsed;
            _exportButton.IsEnabled = true;
            _exporting = false;
        }
    }

    // ------------------------------------------------------------------
    // Progress bar (same hand-built pattern as AudioConvertWindow)
    // ------------------------------------------------------------------

    private void ShowProgress(double fraction)
    {
        _progressHost.Visibility = Visibility.Visible;
        UpdateProgress(fraction);
    }

    private void UpdateProgress(double fraction)
    {
        if (_progressHost.Visibility != Visibility.Visible) return;
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

        var grid = new Grid(); // rows: 0 title, 1 info, 2 waveform, 3 time info, 4 tools,
                               //        5 export settings, 6 progress, 7 status, 8 buttons
        for (var i = 0; i < 9; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        grid.RowSpacing = 10;

        Grid.SetRow(_titleText, 0);
        grid.Children.Add(_titleText);

        Grid.SetRow(_sourceInfo, 1);
        grid.Children.Add(_sourceInfo);

        var waveWrap = new ScrollViewer
        {
            Content = _wavePanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(waveWrap, 2);
        grid.Children.Add(waveWrap);

        // 3: marker time labels under the waveform (start / end) + info line
        var timeRow = new StackPanel { Spacing = 4 };
        var timeLabels = new Grid
        {
            Width = WaveW,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        timeLabels.Children.Add(_timeALabel);
        timeLabels.Children.Add(_timeBLabel);
        timeRow.Children.Add(timeLabels);
        timeRow.Children.Add(_timeInfo);
        Grid.SetRow(timeRow, 3);
        grid.Children.Add(timeRow);

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        tools.Children.Add(_playButton);
        tools.Children.Add(_pauseButton);
        tools.Children.Add(_stopButton);
        tools.Children.Add(_trimButton);
        tools.Children.Add(_undoButton);
        Grid.SetRow(tools, 4);
        grid.Children.Add(tools);

        var exportRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        exportRow.Children.Add(new TextBlock
        {
            Text = "Format",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(MenuTheme.HintColor),
            FontSize = 12,
        });
        _formatBox.MinWidth = 96;
        exportRow.Children.Add(_formatBox);
        exportRow.Children.Add(new TextBlock
        {
            Text = "Quality",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(MenuTheme.HintColor),
            FontSize = 12,
        });
        _qualityBox.MinWidth = 130;
        exportRow.Children.Add(_qualityBox);
        Grid.SetRow(exportRow, 5);
        grid.Children.Add(exportRow);

        var progressRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        _progressHost = MakeProgressHost();
        progressRow.Children.Add(_progressHost);
        progressRow.Children.Add(_progressPct);
        Grid.SetRow(progressRow, 6);
        grid.Children.Add(progressRow);

        Grid.SetRow(_status, 7);
        grid.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
        };
        buttons.Children.Add(_closeButton);
        buttons.Children.Add(_exportButton);
        Grid.SetRow(buttons, 8);
        grid.Children.Add(buttons);

        root.Child = grid;
        return root;
    }

    private TextBlock MakeTitle() => new()
    {
        Text = "Edit Audio — drag A/B over the range to keep",
        FontSize = 15,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Brush(MenuTheme.LabelColor),
    };

    private static TextBlock MakeHintText() => new()
    {
        FontSize = 11,
        Foreground = Brush(MenuTheme.HintColor),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static TextBlock MakeTimeLabel() => new()
    {
        FontSize = 11,
        Width = 90,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
        Visibility = Visibility.Collapsed,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x9E, 0xC1, 0xF5)),
    };

    private TextBlock MakeTimeInfo() => new()
    {
        FontSize = 12,
        Foreground = Brush(MenuTheme.HintColor),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private Button MakeToolButton(string text) => new()
    {
        Content = text,
        MinWidth = 92,
    };

    /// <summary>Small round icon-only button (Material icon, tooltip for name).</summary>
    private static Button MakeRoundIconButton(Geometry icon, string tooltip, int size, string? iconColorHex = null)
    {
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = icon,
            Fill = Brush(iconColorHex ?? MenuTheme.LabelColor),
            Width = size * 0.42,
            Height = size * 0.42,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var button = new Button
        {
            Content = new Grid { Children = { path } },
            Width = size,
            Height = size,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(size / 2.0),
            Background = Brush("#FF2B3040"),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private static ComboBox MakeCombo(string[] items, int selectedIndex)
    {
        var box = new ComboBox { MinWidth = 130 };
        foreach (var item in items) box.Items.Add(item);
        box.SelectedIndex = selectedIndex;
        return box;
    }

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
        Content = "Close",
        MinWidth = 96,
    };

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

    private static TextBlock MakeStatusText() => new()
    {
        FontSize = 12,
        Visibility = Visibility.Collapsed,
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
