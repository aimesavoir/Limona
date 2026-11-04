using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Limona.Interop;
using Limona.Services;
using Windows.Foundation;

using MQ = Microsoft.UI.Dispatching;
using WPB = Windows.Media.Playback;

namespace Limona.Menu;

/// <summary>
/// Video trim window: plays the video, user marks A and B points, exporting
/// keeps only the A→B range as MP4 (MediaTranscoder native trim).
/// </summary>
internal sealed partial class VideoTrimWindow : Window
{
    private static VideoTrimWindow? s_active;

    private readonly string _sourcePath;
    private bool _loaded;
    private bool _exporting;
    // in-point flag
    private TimeSpan _markA = TimeSpan.Zero;
    private TimeSpan _markB = TimeSpan.Zero; // out-point (0 = not set = end)
    private TimeSpan _duration;

    private MediaPlayerElement _playerHost = null!;
    private WPB.MediaPlayer? _player;
    private WPB.MediaPlaybackSession? _session;
    private TextBlock _fileNameText = null!;
    private TextBlock _rangeInfo = null!;
    private TextBlock _status = null!;
    private Grid _timelineBar = null!;
    private Canvas _barStage = null!;
    private Canvas _ticksCanvas = null!;
    private Border _rangeFill = null!;
    private Grid _markAGrid = null!;
    private Grid _markBGrid = null!;
    private Grid _playhead = null!;
    private TextBlock _timeNow = null!;
    private TextBlock _timeTotal = null!;
    private Button _saveButton = null!;
    private Button _cancelButton = null!;
    private Button _transportButton = null!; // single Play - Pause round toggle
    private Button _stopButton = null!;
    private Button _markAButton = null!;
    private Button _markBButton = null!;
    private Button _clearButton = null!;
    private Button _trimButton = null!;
    private Button _cropButton = null!;
    private Slider _volumeSlider = null!;
    private TextBlock _volumePct = null!;

    // Crop editor state. The marked region is stored as FRACTIONS of the
    // video frame (0..1) so it stays valid when the window is resized;
    // applying the crop does not touch the video - it is only used when the
    // user finally clicks Save MP4.
    private Grid _cropLayer = null!;        // top layer over the video
    private Border _cropDim = null!;        // dim backdrop, only in edit mode
    private Canvas _cropCanvas = null!;     // pointer surface for the region
    private Border _cropRect = null!;       // visible marked region
    private StackPanel _cropActions = null!;
    private Windows.Foundation.Point _cropFrom;
    private bool _cropMode;                 // user is resizing / drawing
    private bool _cropApplied;              // region committed for export
    private double _cx, _cy, _cw, _ch;      // region fractions left/top/w/h
    private double _undoCx, _undoCy, _undoCw, _undoCh; // state at edit entry
    private bool _undoHadCrop;

    private enum CropDrag { None, New, Pan, Resize }
    private CropDrag _cropDrag = CropDrag.None;
    private bool _edgeL, _edgeR, _edgeT, _edgeB;
    private double _pressCx, _pressCy, _pressCw, _pressCh;
    private double _pressFx, _pressFy;
    private const double CropGrabPx = 12;
    private const double CropMinPx = 10;
    private int _srcW, _srcH;
    private Border _progressHost = null!;
    private TextBlock _progressPct = null!;
    private ColumnDefinition _fillColumn = null!;
    private ColumnDefinition _emptyColumn = null!;
    private MQ.DispatcherQueueTimer? _playTimer;
    private MQ.DispatcherQueueTimer? _exportTimer;
    private DateTime _exportStartedAt;
    private string _exportPhase = "";

    // Pro timeline geometry: markers grab handles (A/B), a highlighted
    // keep-region, adaptive second tics and a draggable playhead line.
    private const double BarW = 760; // fallback width before the first layout pass
    private const double BarH = 46;
    private const double GrabPx = 14;
    private enum TimelineDrag { None, A, B, Playhead }
    private TimelineDrag _barDrag = TimelineDrag.None;


    public static void ShowForFile(string sourcePath)
    {
        s_active = ToolWindowCoordinator.Open(() => new VideoTrimWindow(sourcePath));
    }

    private VideoTrimWindow(string sourcePath)
    {
        _sourcePath = sourcePath;

        WindowFit.SetTitle(this, "Trim Video");
        _fileNameText = MakeFileNameText();
        _playerHost = MakePlayerHost();
        _timelineBar = MakeTimelineBar();
        _timeNow = MakeTimeLabel("0:00", bold: true);
        _timeTotal = MakeTimeLabel("0:00", bold: false);
        _rangeInfo = MakeRangeInfo();
        _status = MakeStatusText();
        _saveButton = MakeAccentButton("Save MP4");
        _cancelButton = MakeCancelButton();
        _progressPct = new TextBlock
        {
            Text = "0 %",
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 42,
            FontSize = 12,
            Foreground = Brush(MenuTheme.HintColor),
        };
        _transportButton = MakeRoundIconButton(Icons.Play(), "Play / Pause", 38, MenuTheme.AccentColor);
        _stopButton = MakeRoundIconButton(Icons.Stop(), "Stop — back to start", 30);
        _markAButton = MakeRoundLetterButton("A", "Mark in point here (A)", "#FF7CCD8A");
        _markBButton = MakeRoundLetterButton("B", "Mark out point here (B)", MenuTheme.AccentDeep);
        _clearButton = MakeRoundIconButton(Icons.Undo(), "Clear markers — keep full video", 30);
        _trimButton = MakeRoundIconButton(Icons.Cut(), "Trim: export only A-B as MP4", 34, MenuTheme.AccentColor);
        _cropButton = MakeRoundIconButton(Icons.Crop(), "Crop: keep only a region of the frame", 34, "#FF9BC1FF");
        _cropButton.Click += (_, _) => OnCropClicked();
        _volumeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            StepFrequency = 1,
            Value = 100,
            Width = 120,
            Height = 24,
            MinHeight = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _volumePct = MakeTimeLabel("100 %", bold: true);
        _volumePct.MinWidth = 40;
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_volumeSlider,
            "Video audio volume — applied to the exported file (0 % mutes the original sound)");
        _volumeSlider.ValueChanged += (_, e) =>
        {
            var pct = (int)e.NewValue;
            _volumePct.Text = pct + " %";
            _edit.VideoVolume = pct / 100.0;
            try { _player!.Volume = pct / 100.0; } catch { }
        };

        _transportButton.Click += (_, _) =>
        {
            if (!_loaded) return;
            if (IsPlaying) _player?.Pause();
            else _player?.Play();
        };
        _stopButton.Click += (_, _) => { if (_loaded) SeekTo(TimeSpan.Zero); };
        _markAButton.Click += (_, _) => SetMarkA(_loaded ? CurrentPosition() : TimeSpan.Zero);
        _markBButton.Click += (_, _) => SetMarkB(_loaded ? CurrentPosition() : TimeSpan.Zero);
        _clearButton.Click += (_, _) =>
        {
            _markA = TimeSpan.Zero;
            _markB = TimeSpan.Zero;
            UpdateBarMarks();
            RefreshRangeInfo();
            SetStatus("Markers cleared — the full video will be kept.", MenuTheme.HintColor);
        };
        _trimButton.Click += (_, _) => _ = OnSaveClicked();
        _saveButton.Click += (_, _) => _ = OnSaveClicked();
        _cancelButton.Click += (_, _) => { try { _playerHost.Source = null; } catch { } Close(); };

        Content = BuildLayout();
        WindowFit.Apply(this, (FrameworkElement)Content!);

        Closed += (_, _) =>
        {
            StopPlayTimer();
            DisposeMusicPreview();
            try { _playerHost.Source = null; } catch { }
            try { _player?.Dispose(); } catch { }
        };

        _ = PrepareAsync();
    }

    private bool IsPlaying => _session?.PlaybackState
        is WPB.MediaPlaybackState.Playing or WPB.MediaPlaybackState.Buffering;

    private bool IsPaused => _session?.PlaybackState == WPB.MediaPlaybackState.Paused;

    private async System.Threading.Tasks.Task PrepareAsync()
    {
        LocalizationUi.SetFormattedText(_fileNameText,
            "Preparing video... {0}", Path.GetFileName(_sourcePath));
        try
        {
            _duration = await System.Threading.Tasks.Task.Run(
                () => VideoConverter.GetDurationAsync(_sourcePath));
            if (_duration <= TimeSpan.Zero)
            {
                // Property store can be empty for some containers; measure the
                // real video stream instead of guessing.
                _duration = await MediaFoundationVideo.VideoDurationAsync(_sourcePath);
            }
            Dbg.Log($"VideoTrimWindow duration={_duration.TotalSeconds:0.###}s for {Path.GetFileName(_sourcePath)}");
            _timeTotal.Text = FormatTime(_duration);
            _fileNameText.Text = Path.GetFileName(_sourcePath);
            RefreshRangeInfo();

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_sourcePath);

            // Real frame size, needed by the crop tool to map selections.
            try
            {
                var props = await file.Properties.GetVideoPropertiesAsync();
                _srcW = (int)props.Width;
                _srcH = (int)props.Height;
            }
            catch { }

            // Own the player object explicitly: MediaPlayerElement auto-creates
            // its player lazily, which left the session late/null in practice.
            var player = new WPB.MediaPlayer
            {
                AudioCategory = WPB.MediaPlayerAudioCategory.Media,
                AutoPlay = false,
            };
            _playerHost.SetMediaPlayer(player);
            _player = player;
            _session = player.PlaybackSession;
            _session.PlaybackStateChanged += (_, _) =>
                _ = _playerHost.DispatcherQueue.TryEnqueue(UpdateButtonsState);
            _session.NaturalDurationChanged += OnDurChanged;
            player.MediaEnded += (_, _) => UpdateButtonsState();
            player.MediaFailed += (_, e) =>
            {
                _loaded = false;
                SetStatus("Video decode problem: {0}", MenuTheme.AccentDeep, Shorten(e.ErrorMessage));
            };
            player.Source = Windows.Media.Core.MediaSource.CreateFromStorageFile(file);
        }
        catch (Exception ex)
        {
            Dbg.Log("VideoTrimWindow prepare THREW: " + ex);
            _fileNameText.Text = Path.GetFileName(_sourcePath);
            SetStatus("Could not open this video: {0}", MenuTheme.AccentDeep, Shorten(ex.Message));
        }
    }

    private void OnDurChanged(WPB.MediaPlaybackSession sender, object args)
    {
            _ = _playerHost.DispatcherQueue.TryEnqueue(() =>
            {
                // MediaPlayer.NaturalDuration can be wildly wrong for some
                // containers (observed: 185 s for a 50 s WMV), so it is only a
                // last resort when no reliable duration could be obtained.
                if (_duration <= TimeSpan.Zero && sender.NaturalDuration > TimeSpan.Zero)
                {
                    _duration = sender.NaturalDuration;
                    Dbg.Log($"VideoTrimWindow duration (player fallback)={_duration.TotalSeconds:0.###}s");
                    _timeTotal.Text = FormatTime(_duration);
                    RefreshRangeInfo();
                    RebuildTicks();
                    UpdateBarMarks();
                }
                _loaded = true;
                UpdatePlayheadVisual(CurrentPosition());
                UpdateButtonsState();
                UpdateTrackBlocks();
                UpdateOverlayPreview();
            });
    }

    private void UpdateTransportIcon()
    {
        // Swap the single round button's glyph between Play and Pause.
        if (_transportButton.Content is Grid host && host.Children.Count > 0 &&
            host.Children[0] is Microsoft.UI.Xaml.Shapes.Path path)
        {
            path.Data = IsPlaying ? Icons.Pause() : Icons.Play();
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(
                _transportButton, IsPlaying ? "Pause" : "Play");
        }
    }

    private void UpdateButtonsState()
    {
        _transportButton.IsEnabled = _loaded;
        _stopButton.IsEnabled = _loaded;
        UpdateTransportIcon();
        if (IsPlaying) StartPlayTimer(); else StopPlayTimer();
    }

    private MediaPlayerElement MakePlayerHost() => new()
    {
        AreTransportControlsEnabled = false,
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ------------------------------------------------------------------
    // Pro timeline bar
    // ------------------------------------------------------------------

    /// <summary>Interactive timeline: drag to scrub, drag the A/B caps to
    /// fine-tune the cut, second tics adapt to the video length.</summary>
    private Grid MakeTimelineBar()
    {
        var bar = new Grid
        {
            Height = BarH,
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(6),
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, BarW, BarH) },
        };

        var track = new Border
        {
            Background = Brush("#FF232838"),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        bar.Children.Add(track);

        _barStage = new Canvas
        {
            Height = BarH,
            IsHitTestVisible = false,
        };

        // 1. ticks (under everything)
        _ticksCanvas = new Canvas
        {
            Height = BarH,
            IsHitTestVisible = false,
        };
        _barStage.Children.Add(_ticksCanvas);

        // 2. keep-region fill between A and B
        _rangeFill = new Border
        {
            Background = Brush("#2FFFE5B3"),      // amber wash
            CornerRadius = new CornerRadius(4),
            Height = BarH,
        };
        _rangeFill.Child = new Border
        {
            BorderBrush = Brush("#FF9C6E24"),
            BorderThickness = new Thickness(0, 2, 0, 2),
            CornerRadius = new CornerRadius(4),
            Opacity = 0.9,
        };
        _barStage.Children.Add(_rangeFill);

        // 3. A / B markers (26px grab grids like the audio editor)
        _markAGrid = MakeBarMarker("A", "#FF7CCD8A");
        _markBGrid = MakeBarMarker("B", MenuTheme.AccentDeep);
        _markBGrid.Visibility = Visibility.Collapsed;
        _barStage.Children.Add(_markAGrid);
        _barStage.Children.Add(_markBGrid);

        // 4. playhead: bright amber line with dark outline, full height
        _playhead = new Grid
        {
            Width = 7,
            Height = BarH,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _playhead.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 3,
            Background = Brush("#FFE8ECF4"),
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 4, 0, 4),
        });
        _playhead.Children.Add(new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            Width = 11,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = Brush("#FFE8ECF4"),
        });
        _barStage.Children.Add(_playhead);

        bar.Children.Add(_barStage);

        bar.PointerPressed += OnBarPressed;
        bar.PointerMoved += OnBarMoved;
        bar.PointerReleased += OnBarReleased;
        bar.PointerCanceled += OnBarReleased;
        bar.PointerCaptureLost += (_, _) => { _barDrag = TimelineDrag.None; };
        bar.PointerEntered += (_, _) =>
        {

            SetBarCursor(SizeWestEastShape());
        };
        bar.PointerExited += (_, _) =>
        {

            SetBarCursor(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        };
        bar.SizeChanged += (_, e) => OnTimelineSizeChanged(e.NewSize.Width);
        return bar;
    }

    /// <summary>The bar stretches to the window; every position is derived
    /// from the current width so the whole timeline stays usable at any size.</summary>
    private double BarWidth => Math.Max(_timelineBar?.ActualWidth ?? BarW, 1);

    private void OnTimelineSizeChanged(double width)
    {
        if (width <= 1 || _barStage is null) return;
        _timelineBar.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, BarH) };
        RebuildTicks();
        UpdateBarMarks();
        UpdatePlayheadVisual(CurrentPosition());
    }

    private Microsoft.UI.Input.InputSystemCursorShape CropCursorFor(
        bool l, bool r, bool t, bool b, bool inside)
    {
        if ((l && t) || (r && b)) return Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast;
        if ((r && t) || (l && b)) return Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest;
        if (l || r) return Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast;
        if (t || b) return Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth;
        if (inside) return Microsoft.UI.Input.InputSystemCursorShape.SizeAll;
        return Microsoft.UI.Input.InputSystemCursorShape.Cross;
    }

    private void SetCropCursor(Microsoft.UI.Input.InputSystemCursorShape shape)
    {
        try { CursorInterop.SetCursor(_cropCanvas, Microsoft.UI.Input.InputSystemCursor.Create(shape)); }
        catch { /* cursor API may not be available on all hosts */ }
    }

    private static Microsoft.UI.Input.InputSystemCursorShape SizeWestEastShape() =>
        Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast;

    private void SetBarCursor(Microsoft.UI.Input.InputSystemCursorShape shape)
    {
        try
        {
            CursorInterop.SetCursor(_timelineBar, Microsoft.UI.Input.InputSystemCursor.Create(shape));
        }
        catch { /* cursor API may not be available on all hosts */ }
    }

    /// <summary>One draggable marker: cap with letter + thin line through the bar.</summary>
    private static Grid MakeBarMarker(string letter, string colorHex)
    {
        var grid = new Grid
        {
            Width = 26,
            Height = BarH,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        grid.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 2,
            Height = BarH,
            Background = Brush(colorHex),
        });
        grid.Children.Add(new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 22,
            Height = 16,
            CornerRadius = new CornerRadius(3, 3, 6, 6),
            Background = Brush(colorHex),
            BorderBrush = Brush("#FF121418"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = letter,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.ExtraBold,
                Foreground = Brush("#FF141820"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        return grid;
    }

    // ------------------------------------------------------------------
    // Timeline interaction
    // ------------------------------------------------------------------

    private double FractionX(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_timelineBar).Position;
        return pt.X;
    }

    private void OnBarPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_loaded || _duration <= TimeSpan.Zero) return;
        var x = FractionX(e);
        _barDrag = NearestTarget(x);
        try { _timelineBar.CapturePointer(e.Pointer); } catch { }
        ApplyBarDrag(x);
        e.Handled = true;
    }

    private void OnBarMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_barDrag == TimelineDrag.None) return;
        ApplyBarDrag(FractionX(e));
        e.Handled = true;
    }

    private void OnBarReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_barDrag == TimelineDrag.None) return;
        _barDrag = TimelineDrag.None;
        try { _timelineBar.ReleasePointerCapture(e.Pointer); } catch { }
        e.Handled = true;
    }

    private TimelineDrag NearestTarget(double x)
    {
        var ax = MarkerX(_markA);
        var bSet = _markB > _markA;
        var bx = bSet ? MarkerX(_markB) : -300;
        var px = MarkerX(_session?.Position ?? TimeSpan.Zero);

        if (Math.Abs(x - ax) <= GrabPx) return TimelineDrag.A;
        if (bSet && Math.Abs(x - bx) <= GrabPx) return TimelineDrag.B;
        _ = px;
        // Anything away from a marker grabs the playhead: whole bar scrubs.
        return TimelineDrag.Playhead;
    }

    private double MarkerX(TimeSpan t)
    {
        if (_duration <= TimeSpan.Zero) return 0;
        var frac = Math.Clamp(
            t.TotalSeconds / _duration.TotalSeconds, 0, 0.999);
        return frac * BarWidth;
    }

    private void ApplyBarDrag(double x)
    {
        var barWidth = BarWidth;
        x = Math.Clamp(x, 0, barWidth - 1);
        var duration = _duration;
        if (duration <= TimeSpan.Zero) return;
        var t = TimeSpan.FromSeconds(Math.Clamp(
            x / barWidth * duration.TotalSeconds, 0, duration.TotalSeconds - 0.05));

        if (_barDrag == TimelineDrag.A) SetMarkA(t);
        else if (_barDrag == TimelineDrag.B) SetMarkB(t);
        else
        {
            SeekTo(t);
            _playhead.IsHitTestVisible = false;
            UpdatePlayheadVisual(t);
            _timeNow.Text = FormatTime(t);
        }
    }

    private void UpdatePlayheadVisual(TimeSpan t)
    {
        if (_playhead is null) return;
        _playhead.HorizontalAlignment = HorizontalAlignment.Left;
        Canvas.SetLeft(_playhead, MarkerX(t) - 3.5);
        _playhead.Visibility = _loaded ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Repositions markers + keep-region to match the current A/B spans.</summary>
    private void UpdateBarMarks()
    {
        if (_barStage is null) return;
        var bSet = _markB > _markA;
        Canvas.SetLeft(_markAGrid, MarkerX(_markA) - 13);
        _markBGrid.Visibility = bSet ? Visibility.Visible : Visibility.Collapsed;
        if (bSet)
        {
            Canvas.SetLeft(_markBGrid, MarkerX(_markB) - 13);
        }
        var ax = MarkerX(_markA);
        var width = bSet ? Math.Max(2, MarkerX(_markB) - ax) : 0;
        Canvas.SetLeft(_rangeFill, ax);
        _rangeFill.Width = width;
        _rangeFill.Visibility = bSet ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Second tics adapt to video length; labels every major step.</summary>
    private void RebuildTicks()
    {
        if (_ticksCanvas is null) return;
        _ticksCanvas.Children.Clear();
        if (_duration <= TimeSpan.Zero) return;

        var barW = BarWidth;
        var total = _duration.TotalSeconds;
        double step = total switch
        {
            <= 10 => 1,
            <= 30 => 5,
            <= 120 => 15,
            <= 300 => 30,
            <= 900 => 60,
            <= 1800 => 120,
            <= 3600 => 300,
            _ => 600,
        };
        // keep the minor tic spacing at least ~9px
        while (barW * step / total < 9 && step < total / 4) step *= 2;

        for (var s = 0.0; s < total; s += step / 2)
        {
            var x = s / total * barW;
            var isMajor = Math.Abs(s % step) < 0.001 || step - (s % step) < 0.001;
            var line = new Border
            {
                Width = 1,
                Height = isMajor ? 12 : 7,
                Background = Brush(isMajor ? "#3DE8ECF4" : "#20E8ECF4"),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Canvas.SetLeft(line, Math.Round(x * 2) / 2);
            Canvas.SetTop(line, BarH - (isMajor ? 12 : 7));
            _ticksCanvas.Children.Add(line);

            if (isMajor && s > 0)
            {
                var label = new TextBlock
                {
                    Text = FormatTime(TimeSpan.FromSeconds(s)),
                    FontSize = 10,
                    Foreground = Brush("#9FE8ECF4"),
                };
                Canvas.SetLeft(label, Math.Min(x + 4, barW - 36));
                Canvas.SetTop(label, 3);
                _ticksCanvas.Children.Add(label);
            }
        }
    }

    private TimeSpan CurrentPosition() => _session is null ? TimeSpan.Zero : _session.Position;

    private void SeekTo(TimeSpan t)
    {
        try { _player!.PlaybackSession.Position = t; } catch { }
        UpdateOverlayPreview();
        SyncMusicPreview(t, false);
    }

        /// <summary>
    /// Video host = player + top crop layer. The crop layer shows the marked
    /// region at all times; it dims and becomes interactive only while crop
    /// edit mode is active.
    /// </summary>
    private Grid MakeVideoHost()
    {
        var host = new Grid { MinHeight = 420 };
        host.Children.Add(new Border
        {
            Background = Brush("#FF1B1E2A"),
            CornerRadius = new CornerRadius(10),
        });
        host.Children.Add(_playerHost);

        // Top layer: dim backdrop (edit mode only) + interactive canvas that
        // keeps showing the marked region whenever one is defined.
        _cropLayer = new Grid { IsHitTestVisible = true };

        _cropDim = new Border
        {
            Background = Brush("#88000000"),
            Visibility = Visibility.Collapsed,
        };
        _cropLayer.Children.Add(_cropDim);

        _cropCanvas = new Canvas
        {
            Background = Brush("#01000000"), // invisible but hit-testable
        };
        _cropRect = new Border
        {
            BorderBrush = Brush(MenuTheme.AccentColor),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(2),
            Background = Brush("#2F" + MenuTheme.AccentColor[3..]), // wash only useful in edit mode
            Visibility = Visibility.Collapsed,
        };
        _cropCanvas.Children.Add(_cropRect);

        _overlayPreview = new Canvas(); // children are positioned absolutely
        _cropCanvas.Children.Add(_overlayPreview);

        _cropCanvas.PointerPressed += OnCropPressed;
        _cropCanvas.PointerMoved += OnCropMoved;
        _cropCanvas.PointerReleased += OnCropReleased;
        _cropCanvas.PointerCanceled += OnCropReleased;
        _cropCanvas.PointerExited += (_, _) =>
        {
            if (_cropDrag == CropDrag.None)
                SetCropCursor(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        };
        _cropLayer.Children.Add(_cropCanvas);

        var hint = new TextBlock
        {
            Text = "Drag to draw the region, drag inside to move, edges/corners to resize. Pick a format or keep Free.",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("#FFE8ECF4"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _cropHint = hint;
        _cropLayer.Children.Add(hint);

        _cropSizeText = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = Brush(MenuTheme.HintColor),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 10),
            Visibility = Visibility.Collapsed,
        };

        _cropPresetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _cropPresetRow.Children.Add(MakePresetButton("Free", 0));
        _cropPresetRow.Children.Add(MakePresetButton("TikTok", 9.0 / 16.0));
        _cropPresetRow.Children.Add(MakePresetButton("YouTube", 16.0 / 9.0));
        _cropPresetRow.Children.Add(MakePresetButton("IG Story", 9.0 / 16.0));
        _cropPresetRow.Children.Add(MakePresetButton("IG Post", 1.0));
        _cropPresetRow.Children.Add(MakePresetButton("FB Post", 4.0 / 5.0));
        actions.Children.Add(_cropSizeText);
        actions.Children.Add(_cropPresetRow);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var apply = new Button
        {
            Content = "Apply crop",
            MinWidth = 104,
            CornerRadius = new CornerRadius(8),
            Background = Brush(MenuTheme.AccentColor),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x14, 0x18)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 96 };
        apply.Click += (_, _) => ApplyCropSelection();
        cancel.Click += (_, _) => ExitCropMode(true);
        buttonRow.Children.Add(apply);
        buttonRow.Children.Add(cancel);
        actions.Children.Add(buttonRow);
        _cropApplyButton = apply;
        _cropActions = actions;
        _cropLayer.Children.Add(actions);
        UpdatePresetVisuals();

        host.Children.Add(_cropLayer);
        host.SizeChanged += (_, _) => UpdateCropVisuals();
        return host;
    }

    // ------------------------------------------------------------------
    // Interactive crop editor
    // ------------------------------------------------------------------

    private TextBlock _cropHint = null!;
    private StackPanel _cropPresetRow = null!;
    private Button _cropApplyButton = null!;
    private readonly List<(Button Button, double Aspect)> _cropPresets = new();
    private double _cropAspect; // 0 = free, else target width/height in pixels
    private TextBlock _cropSizeText = null!;

    /// <summary>Letterboxed content rect of the video inside the host.</summary>
    private (double ox, double oy, double cw, double ch) FramePx()
    {
        var hostW = Math.Max(_cropCanvas.ActualWidth, 1);
        var hostH = Math.Max(_cropCanvas.ActualHeight, 1);
        if (_srcW <= 0 || _srcH <= 0) return (0, 0, hostW, hostH);
        var scale = Math.Min(hostW / _srcW, hostH / _srcH);
        var cw = _srcW * scale;
        var ch = _srcH * scale;
        return ((hostW - cw) / 2, (hostH - ch) / 2, cw, ch);
    }

    private void UpdateCropVisuals()
    {
        var (ox, oy, cw, ch) = FramePx();
        if (_cw > 0.001 && _ch > 0.001)
        {
            _cropRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(_cropRect, ox + _cx * cw);
            Canvas.SetTop(_cropRect, oy + _cy * ch);
            _cropRect.Width = _cw * cw;
            _cropRect.Height = _ch * ch;
        }
        else
        {
            _cropRect.Visibility = Visibility.Collapsed;
        }
        _cropRect.Background = _cropMode ? Brush("#2FFFE5B3") : Brush("#00FFFFFF");
        _cropRect.BorderThickness = new Thickness(_cropMode ? 2 : 1.5);

        UpdateOverlayPreview();
        if (_cropSizeText is not null)
        {
            var valid = _cw > 0.005 && _ch > 0.005;
            if (valid && _srcW > 0 && _srcH > 0)
            {
                var pw = (int)Math.Round(_cw * _srcW);
                var ph = (int)Math.Round(_ch * _srcH);
                LocalizationUi.SetFormattedText(_cropSizeText,
                    "{0} × {1} px · {2}", pw, ph, LocalizationUi.Localized(AspectLabel(_cropAspect)));
            }
            else
            {
                LocalizationUi.SetText(_cropSizeText, _cropAspect > 0
                    ? "Click a format again after opening the video, or drag a region"
                    : "Drag on the video to choose the area · or pick a format");
            }
        }
    }

    private static string AspectLabel(double aspect) => aspect switch
    {
        <= 0 => "Free",
        _ when Math.Abs(aspect - 9.0 / 16.0) < 0.001 => "9:16",
        _ when Math.Abs(aspect - 16.0 / 9.0) < 0.001 => "16:9",
        _ when Math.Abs(aspect - 1.0) < 0.001 => "1:1",
        _ when Math.Abs(aspect - 4.0 / 5.0) < 0.001 => "4:5",
        _ => "Custom",
    };

    /// <summary>Tool button: enter crop edit mode / re-click confirms.</summary>
    private void OnCropClicked()
    {
        if (!_loaded || _exporting) return;
        if (_cropMode) { ApplyCropSelection(); return; }
        if (_srcW <= 0 || _srcH <= 0) _ = ReadVideoSizeAsync();
        EnterCropMode();
    }

    private async System.Threading.Tasks.Task ReadVideoSizeAsync()
    {
        try
        {
            var (w, h) = await MediaFoundationVideo.VideoSizeAsync(_sourcePath);
            if (w > 0)
            {
                _srcW = w;
                _srcH = h;
                if (_cropMode && _cropAspect > 0 && _cw <= 0.005) FitAspectRect(_cropAspect);
                UpdateCropVisuals();
                RefreshCropButtons();
            }
        }
        catch { }
    }

    private void EnterCropMode()
    {
        try { _player?.Pause(); } catch { }
        _cropMode = true;
        _undoCx = _cx; _undoCy = _cy; _undoCw = _cw; _undoCh = _ch;
        _undoHadCrop = _cropApplied;
        _cropDim.Visibility = Visibility.Visible;
        _cropHint.Visibility = Visibility.Visible;
        _cropCanvas.IsHitTestVisible = true;
        UpdatePresetVisuals();
        UpdateCropVisuals();
        RefreshCropButtons();
        SetStatus(_cropApplied
            ? "Crop edit: drag inside to move, edges to resize. Apply to confirm."
            : "Drag a rectangle over the video to pick the area to keep.", MenuTheme.AccentColor);
    }

    private void ExitCropMode(bool revert)
    {
        _cropDrag = CropDrag.None;
        _cropMode = false;
        if (revert)
        {
            if (!_undoHadCrop) { _cx = _cy = _cw = _ch = 0; }
            else { _cx = _undoCx; _cy = _undoCy; _cw = _undoCw; _ch = _undoCh; }
        }
        _cropDim.Visibility = Visibility.Collapsed;
        _cropHint.Visibility = Visibility.Collapsed;
        _cropActions.Visibility = Visibility.Collapsed;
        // Canvas stays hit-test visible: the overlay hit boxes (text/stickers)
        // live inside it, and crop drawing is already gated by _cropMode.
        UpdateCropVisuals();
        RefreshCropButtons();
        if (revert)
        {
            SetStatus(_cropApplied
                ? "Crop edit cancelled - region unchanged; applies when you Save."
                : "Crop edit cancelled.", MenuTheme.HintColor);
        }
    }

    private Button MakePresetButton(string label, double aspect)
    {
        var button = new Button
        {
            Content = label,
            FontSize = 11,
            Height = 26,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(9, 2, 9, 4),
            CornerRadius = new CornerRadius(6),
            Background = Brush("#FF2B3040"),
            Foreground = Brush(MenuTheme.LabelColor),
        };
        button.Click += (_, _) => SelectCropPreset(aspect);
        _cropPresets.Add((button, aspect));
        return button;
    }

    private void SelectCropPreset(double aspect)
    {
        _cropAspect = aspect;
        if (aspect > 0) FitAspectRect(aspect);
        UpdatePresetVisuals();
        UpdateCropVisuals();
        RefreshCropButtons();
        if (_cropMode)
        {
            SetStatus(aspect > 0
                ? "Format applied - resize keeps its aspect; pick Free to unlock."
                : "Free crop - drag any edge or corner.", MenuTheme.AccentColor);
        }
    }

    private void UpdatePresetVisuals()
    {
        foreach (var (button, aspect) in _cropPresets)
        {
            var active = Math.Abs(aspect - _cropAspect) < 0.0001;
            button.Background = active ? Brush(MenuTheme.AccentColor) : Brush("#FF2B3040");
            button.Foreground = active
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x14, 0x18))
                : Brush(MenuTheme.LabelColor);
        }
    }

    /// <summary>Fits the largest rect with the requested pixel aspect,
    /// centred on the current selection (or on the frame if there is none).</summary>
    private void FitAspectRect(double aspect)
    {
        if (_srcW <= 0 || _srcH <= 0 || aspect <= 0) return;
        double cx, cy, cw, ch;
        if (_cw > 0.005 && _ch > 0.005)
        {
            cw = _cw;
            ch = _ch;
            cx = _cx + _cw / 2;
            cy = _cy + _ch / 2;
        }
        else
        {
            cw = 0.94;
            ch = 0.94;
            cx = 0.5;
            cy = 0.5;
        }

        var chFromW = cw * _srcW / (_srcH * aspect);
        if (chFromW <= ch) ch = chFromW;
        else cw = ch * _srcH * aspect / _srcW;

        var scale = Math.Min(1, Math.Min(1 / cw, 1 / ch));
        cw *= scale;
        ch *= scale;
        cx = Math.Clamp(cx, cw / 2, 1 - cw / 2);
        cy = Math.Clamp(cy, ch / 2, 1 - ch / 2);
        _cx = cx - cw / 2;
        _cy = cy - ch / 2;
        _cw = cw;
        _ch = ch;
    }

    private void ApplyCropSelection()
    {
        if (!_cropMode) return;
        if (_cw < 0.005 || _ch < 0.005)
        {
            _cropApplied = false;
            ExitCropMode(false);
            SetStatus("No crop selected - the full frame is kept.", MenuTheme.HintColor);
            return;
        }
        _cropApplied = true;
        ExitCropMode(false);
        SetStatus("Crop marked - it will be written into the saved video when you click Save.",
            "#FF9BE29B");
    }

    private void RefreshCropButtons()
    {
        var valid = _cw > 0.005 && _ch > 0.005;
        if (_cropActions is not null)
            _cropActions.Visibility = _cropMode ? Visibility.Visible : Visibility.Collapsed;
        if (_cropApplyButton is not null)
            _cropApplyButton.IsEnabled = valid;
    }

    private void CropHitTest(Windows.Foundation.Point pt,
        out bool el, out bool er, out bool et, out bool eb, out bool inside)
    {
        el = er = et = eb = inside = false;
        if (_cw <= 0.02 || _ch <= 0.02) return;
        var (ox, oy, cw, ch) = FramePx();
        var left = ox + _cx * cw;
        var top = oy + _cy * ch;
        var right = left + _cw * cw;
        var bottom = top + _ch * ch;
        el = Math.Abs(pt.X - left) <= CropGrabPx;
        er = Math.Abs(pt.X - right) <= CropGrabPx;
        et = Math.Abs(pt.Y - top) <= CropGrabPx;
        eb = Math.Abs(pt.Y - bottom) <= CropGrabPx;
        inside = pt.X > left + CropGrabPx && pt.X < right - CropGrabPx
              && pt.Y > top + CropGrabPx && pt.Y < bottom - CropGrabPx;
    }

    private void OnCropPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_cropMode)
        {
            // Press landed on the video itself (not on an overlay): deselect.
            if (_selected is not null)
                _ = _cropCanvas.DispatcherQueue.TryEnqueue(() =>
                {
                    _selected = null;
                    BuildInspector();
                    UpdateTrackBlocks();
                    UpdateOverlayPreview();
                });
            return;
        }
        var pt = e.GetCurrentPoint(_cropCanvas).Position;
        var (ox, oy, cw, ch) = FramePx();
        _cropFrom = pt;
        var fx = (pt.X - ox) / cw;
        var fy = (pt.Y - oy) / ch;

        CropHitTest(pt, out var el, out var er, out var et, out var eb, out var inside);
        _edgeL = el; _edgeR = er; _edgeT = et; _edgeB = eb;
        _cropDrag = CropDrag.New;
        if (el || er || et || eb) _cropDrag = CropDrag.Resize;
        else if (inside) _cropDrag = CropDrag.Pan;
        SetCropCursor(CropCursorFor(el, er, et, eb, inside));

        _pressCx = _cx; _pressCy = _cy; _pressCw = _cw; _pressCh = _ch;
        _pressFx = Math.Clamp(fx, 0, 1);
        _pressFy = Math.Clamp(fy, 0, 1);
        if (_cropDrag == CropDrag.New)
        {
            // redraw from scratch: old region stays until a new valid one exists
            _cx = _pressFx;
            _cy = _pressFy;
            _cw = 0;
            _ch = 0;
        }
        if (_cropApplyButton is not null) _cropApplyButton.IsEnabled = false;
        try { _cropCanvas.CapturePointer(e.Pointer); } catch { }
        e.Handled = true;
    }

    private void OnCropMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_cropCanvas).Position;
        if (_cropDrag == CropDrag.None)
        {
            if (!_cropMode)
            {
                SetCropCursor(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
                return;
            }
            CropHitTest(pt, out var hl, out var hr, out var ht, out var hb, out var hin);
            SetCropCursor(CropCursorFor(hl, hr, ht, hb, hin));
            return;
        }

        var (ox, oy, cw, ch) = FramePx();
        var fx = Math.Clamp((pt.X - ox) / cw, 0, 1);
        var fy = Math.Clamp((pt.Y - oy) / ch, 0, 1);
        var minW = CropMinPx / cw;
        var minH = CropMinPx / ch;

        switch (_cropDrag)
        {
            case CropDrag.New:
            {
                var dx = fx - _pressFx;
                var dy = fy - _pressFy;
                var wF = Math.Abs(dx);
                var hF = Math.Abs(dy);
                if (_cropAspect > 0 && _srcW > 0 && _srcH > 0)
                {
                    var pxW = wF * _srcW;
                    var pxH = hF * _srcH;
                    if (pxW / Math.Max(pxH, 1e-6) > _cropAspect) pxW = pxH * _cropAspect;
                    else pxH = pxW / _cropAspect;
                    wF = pxW / _srcW;
                    hF = pxH / _srcH;
                }
                var x0 = dx >= 0 ? _pressFx : _pressFx - wF;
                var y0 = dy >= 0 ? _pressFy : _pressFy - hF;
                x0 = Math.Clamp(x0, 0, Math.Max(0, 1 - wF));
                y0 = Math.Clamp(y0, 0, Math.Max(0, 1 - hF));
                _cx = x0; _cy = y0; _cw = wF; _ch = hF;
                break;
            }
            case CropDrag.Pan:
            {
                var dx = fx - _pressFx;
                var dy = fy - _pressFy;
                _cx = Math.Clamp(_pressCx + dx, 0, 1 - _pressCw);
                _cy = Math.Clamp(_pressCy + dy, 0, 1 - _pressCh);
                break;
            }
            case CropDrag.Resize:
            {
                var L = _pressCx; var T = _pressCy;
                var R = _pressCx + _pressCw; var B = _pressCy + _pressCh;
                if (_edgeL)
                {
                    var nl = Math.Clamp(fx, 0, R - minW);
                    _cx = nl; _cw = R - nl;
                }
                if (_edgeR)
                {
                    _cw = Math.Clamp(fx - L, minW, 1 - L);
                }
                if (_edgeT)
                {
                    var nt = Math.Clamp(fy, 0, B - minH);
                    _cy = nt; _ch = B - nt;
                }
                if (_edgeB)
                {
                    _ch = Math.Clamp(fy - T, minH, 1 - T);
                }

                if (_cropAspect > 0 && _srcW > 0 && _srcH > 0)
                    ConstrainResizeAspect(L, T, R, B);
                break;
            }
        }
        UpdateCropVisuals();
        e.Handled = true;
    }

    /// <summary>Keeps the preset aspect while resizing: corners anchor at the
    /// opposite corner, single edges keep the centre of the idle axis.</summary>
    private void ConstrainResizeAspect(double L, double T, double R, double B)
    {
        double w = _cw, h = _ch, x = _cx, y = _cy;
        if ((_edgeL || _edgeR) && (_edgeT || _edgeB))
        {
            h = w * _srcW / (_srcH * _cropAspect);
            x = _edgeL ? R - w : L;
            y = _edgeT ? B - h : T;
        }
        else if (_edgeL || _edgeR)
        {
            h = w * _srcW / (_srcH * _cropAspect);
            y = (T + B) / 2 - h / 2;
        }
        else
        {
            w = h * _srcH * _cropAspect / _srcW;
            x = (L + R) / 2 - w / 2;
        }

        var scale = Math.Min(1, Math.Min(1 / w, 1 / h));
        w *= scale;
        h *= scale;
        if (_edgeL) x = R - w; else if (_edgeR) x = L;
        if (_edgeT) y = B - h; else if (_edgeB) y = T;
        x = Math.Clamp(x, 0, Math.Max(0, 1 - w));
        y = Math.Clamp(y, 0, Math.Max(0, 1 - h));
        _cx = x; _cy = y; _cw = w; _ch = h;
    }

    private void OnCropReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try { _cropCanvas.ReleasePointerCapture(e.Pointer); } catch { }
        var valid = _cw > 0.005 && _ch > 0.005;
        if (_cropDrag == CropDrag.New && !valid)
        {
            _cx = _cy = _cw = _ch = 0; // tap = no region
        }
        _cropDrag = CropDrag.None;
        UpdateCropVisuals();
        RefreshCropButtons();
        e.Handled = true;
    }


    private async System.Threading.Tasks.Task OnSaveClicked()
    {
        if (_exporting || !_loaded) return;
        _exporting = true;
        try { _player?.Pause(); } catch { }

        _saveButton.IsEnabled = false;
        _progressHost.Visibility = Visibility.Visible;
        UpdateProgress(0);

        try
        {
            var dur = _duration;
            var wantsTrim = _markB > _markA;
            var start = _markA;
            var end = wantsTrim ? _markB : (dur > TimeSpan.Zero ? dur : _markB);
            if (dur > TimeSpan.Zero)
            {
                if (start < TimeSpan.Zero) start = TimeSpan.Zero;
                if (start > dur) start = dur;
                if (end > dur) end = dur;
                if (end < start) end = start;
            }
            Dbg.Log($"VideoTrimWindow save: duration={dur.TotalSeconds:0.###}s " +
                $"markA={_markA.TotalSeconds:0.###}s markB={_markB.TotalSeconds:0.###}s " +
                $"effective={start.TotalSeconds:0.###}s->{end.TotalSeconds:0.###}s wantsTrim={wantsTrim}");

            var trimming = end - start >= TimeSpan.FromSeconds(0.25);
            if ((wantsTrim || start > TimeSpan.Zero) && !trimming)
            {
                // The user asked for a range that does not exist in this
                // video: fail clearly instead of silently exporting the
                // whole clip (which is what used to happen).
                var msg = dur > TimeSpan.Zero && start >= dur
                    ? $"the A marker ({FormatTime(_markA)}) is past the end of the video ({FormatTime(dur)})."
                    : "the selected range is empty or shorter than 0.25 s.";
                SetStatus("Export failed: {0}", MenuTheme.AccentDeep, msg);
                Dbg.Log("VideoTrimWindow save rejected: " + msg);
                _progressHost.Visibility = Visibility.Collapsed;
                _saveButton.IsEnabled = true;
                _exporting = false;
                return;
            }
            var step = new Progress<double>(UpdateProgress);
            var trimRequested = wantsTrim || start > TimeSpan.Zero;
            StartExportTimer();

            _edit.VideoVolume = _volumeSlider.Value / 100.0;
            var hasEdits = _edit.HasOverlays || _edit.HasAddedAudio
                || Math.Abs(_edit.VideoVolume - 1.0) > 0.001;

            string resultPath;
            if ((_cropApplied && _cw > 0.005 && _ch > 0.005) || hasEdits)
            {
                // Edited exports share the same FFmpeg encoder used by the
                // conversion menu, including crop, overlays, and audio mix.
                var regionX = _cropApplied ? _cx : 0.0;
                var regionY = _cropApplied ? _cy : 0.0;
                var regionW = _cropApplied ? _cw : 1.0;
                var regionH = _cropApplied ? _ch : 1.0;
                SetExportPhase(hasEdits
                    ? "Exporting with effects…"
                    : trimming ? "Exporting cropped + trimmed MP4…" : "Cropping video…");
                Dbg.Log($"save export: crop={regionX:0.###},{regionY:0.###},{regionW:0.###},{regionH:0.###} " +
                    $"texts={_edit.Texts.Count} images={_edit.Images.Count} audios={_edit.Audios.Count} " +
                    $"videoVol={_edit.VideoVolume:0.##}");
                resultPath = await VideoConverter.ExportEditedToMp4Async(
                    _sourcePath, regionX, regionY, regionW, regionH,
                    start, trimRequested && trimming ? end : TimeSpan.Zero,
                    _edit, step,
                    msg => _ = DispatcherQueue.TryEnqueue(() => SetExportPhase(msg)));
            }
            else
            {
                SetExportPhase("Exporting trimmed MP4…");
                resultPath = await System.Threading.Tasks.Task.Run(
                    () => VideoConverter.TrimToMp4Async(_sourcePath, start, end, step));
            }
            StopExportTimer();
            UpdateProgress(1.0);

            var savedName = Path.GetFileName(resultPath);
            var savedBytes = new FileInfo(resultPath).Length;
            SetStatus("Saved: {0} · {1}", "#FF9BE29B", savedName, FormatKb(savedBytes));
            try { Win32.MessageBeep(0x00000040); } catch { }

            StopPlayTimer();
            var closeTimer = MQ.DispatcherQueue.GetForCurrentThread().CreateTimer();
            closeTimer.Interval = TimeSpan.FromMilliseconds(1600);
            closeTimer.IsRepeating = false;
            closeTimer.Tick += (_, _) => { closeTimer.Stop(); Close(); };
            closeTimer.Start();
        }
        catch (Exception ex)
        {
            StopExportTimer();
            Dbg.Log("VideoTrimWindow save THREW: " + ex);
            SetStatus("Export failed: {0}", MenuTheme.AccentDeep, Shorten(ex.Message));
            _progressHost.Visibility = Visibility.Collapsed;
            _saveButton.IsEnabled = true;
            _exporting = false;
        }
    }

    // ------------------------------------------------------------------
    // Playback sync
    // ------------------------------------------------------------------

    private void StartPlayTimer()
    {
        if (_playTimer is null)
        {
            _playTimer = MQ.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _playTimer.Interval = TimeSpan.FromMilliseconds(60);
            _playTimer.IsRepeating = true;
            _playTimer.Tick += (_, _) =>
            {
                if (!_loaded || _session is null) return;
                var position = CurrentPosition();
                UpdatePlayheadVisual(position);
                _timeNow.Text = FormatTime(position);
                UpdateOverlayPreview();
                SyncMusicPreview(position, IsPlaying);
            };
        }
        if (!_playTimer!.IsRunning) _playTimer.Start();
    }

    private void StopPlayTimer()
    {
        _playTimer?.Stop();
    }

    // ------------------------------------------------------------------
    // Markers
    // ------------------------------------------------------------------

    private void SetMarkA(TimeSpan t)
    {
        _markA = t;
        if (_markB >= _markA && _markB - _markA < TimeSpan.FromSeconds(0.5))
        {
            _markB = TimeSpan.Zero; // B invalid now, drop it
        }
        UpdateBarMarks();
        RefreshRangeInfo();
        SetStatus("A (start) set to {0}.", "#FF7CCD8A", FormatTime(_markA));
    }

    private void SetMarkB(TimeSpan t)
    {
        if (t - _markA >= TimeSpan.FromSeconds(0.2))
        {
            _markB = t;
            UpdateBarMarks();
            RefreshRangeInfo();
            SetStatus("B (end) set to {0} — keeping {1}.", MenuTheme.AccentDeep,
                FormatTime(_markB), FormatTime(_markB - _markA));
        }
        else
        {
            SetStatus("B must be at least 0.2s after A.", MenuTheme.AccentDeep);
        }
    }

    private void RefreshRangeInfo()
    {
        if (_markB > _markA && _duration > TimeSpan.Zero)
        {
            LocalizationUi.SetFormattedText(_rangeInfo, "Keep: {0} → {1} ({2} of {3})",
                FormatTime(_markA), FormatTime(_markB),
                FormatTime(_markB - _markA), FormatTime(_duration));
        }
        else
        {
            if (_duration > TimeSpan.Zero)
                LocalizationUi.SetFormattedText(_rangeInfo,
                    "No trim — the whole {0} video is kept", FormatTime(_duration));
            else
                LocalizationUi.SetText(_rangeInfo, "No trim markers yet");
        }
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------

    private UIElement BuildLayout()
    {
        var root = new Border
        {
            Background = Brush(MenuTheme.HubFill),
            Padding = new Thickness(14),
        };

        var grid = new Grid(); // 0 header, 1 video, 2 timeline, 3 toolbar, 4 inspector, 5 footer
        for (var i = 0; i < 6; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
        }
        grid.RowSpacing = 7;

        void Separator(StackPanel target) => target.Children.Add(new Border
        {
            Width = 1,
            Margin = new Thickness(5, 4, 5, 4),
            Background = Brush("#3DFFFFFF"),
        });

        // Row 0 - header: file name left, main actions right.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _fileNameText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_fileNameText, 0);
        header.Children.Add(_fileNameText);
        var headerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerButtons.Children.Add(_cancelButton);
        headerButtons.Children.Add(_saveButton);
        Grid.SetColumn(headerButtons, 1);
        header.Children.Add(headerButtons);
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // Row 1 - video.
        var videoHost = MakeVideoHost();
        Grid.SetRow(videoHost, 1);
        grid.Children.Add(videoHost);

        // Row 2 - timeline zone: [time][ruler + lanes][time].
        var timelineZone = new Grid();
        timelineZone.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timelineZone.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timelineZone.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _timeNow.MinWidth = 46;
        _timeNow.VerticalAlignment = VerticalAlignment.Top;
        _timeNow.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_timeNow, 0);
        timelineZone.Children.Add(_timeNow);
        _timeTotal.MinWidth = 46;
        _timeTotal.VerticalAlignment = VerticalAlignment.Top;
        _timeTotal.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(_timeTotal, 2);
        timelineZone.Children.Add(_timeTotal);

        var timelineInner = new Grid();
        timelineInner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        timelineInner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _timelineBar.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetRow(_timelineBar, 0);
        timelineInner.Children.Add(_timelineBar);
        var tracks = MakeTracksArea();
        Grid.SetRow(tracks, 1);
        timelineInner.Children.Add(tracks);
        Grid.SetColumn(timelineInner, 1);
        timelineZone.Children.Add(timelineInner);
        Grid.SetRow(timelineZone, 2);
        grid.Children.Add(timelineZone);

        // Row 3 - one grouped toolbar: playback | marks | trim/crop | overlays | volume.
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        tools.Children.Add(_transportButton);
        tools.Children.Add(_stopButton);
        Separator(tools);
        _markAButton.Margin = new Thickness(0);
        tools.Children.Add(_markAButton);
        tools.Children.Add(_markBButton);
        tools.Children.Add(_clearButton);
        Separator(tools);
        tools.Children.Add(_trimButton);
        tools.Children.Add(_cropButton);
        Separator(tools);
        tools.Children.Add(MakeEditButton("+ Text", OnAddText));
        tools.Children.Add(MakeEditButton("+ Sticker", OnAddSticker));
        tools.Children.Add(MakeEditButton("+ Music", OnAddMusic));
        Separator(tools);
        tools.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = Icons.Volume(),
            Fill = Brush(MenuTheme.LabelColor),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        });
        tools.Children.Add(_volumeSlider);
        tools.Children.Add(_volumePct);
        Grid.SetRow(tools, 3);
        grid.Children.Add(tools);

        // Row 4 - inspector for the selected overlay/clip (collapsed when empty).
        _inspector = new StackPanel
        {
            Spacing = 6,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetRow(_inspector, 4);
        grid.Children.Add(_inspector);

        // Row 5 - footer: status left, range info + progress right.
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.HorizontalAlignment = HorizontalAlignment.Left;
        _status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_status, 0);
        footer.Children.Add(_status);
        _rangeInfo.VerticalAlignment = VerticalAlignment.Center;
        _rangeInfo.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(_rangeInfo, 1);
        footer.Children.Add(_rangeInfo);
        _progressHost = MakeProgressHost();
        _progressHost.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_progressHost, 2);
        footer.Children.Add(_progressHost);
        _progressPct.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_progressPct, 3);
        footer.Children.Add(_progressPct);
        Grid.SetRow(footer, 5);
        grid.Children.Add(footer);

        root.Child = grid;
        return root;
    }


    // ------------------------------------------------------------------
    // UI construction helpers
    // ------------------------------------------------------------------

    private static TextBlock MakeFileNameText() => new()
    {
        FontSize = 14,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Brush(MenuTheme.LabelColor),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static TextBlock MakeTimeLabel(string text, bool bold) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = Brush(bold ? MenuTheme.LabelColor : MenuTheme.HintColor),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalTextAlignment = TextAlignment.Center,
    };

    private static TextBlock MakeRangeInfo() => new()
    {
        FontSize = 12,
        Foreground = Brush(MenuTheme.LabelColor),
    };

    private static TextBlock MakeStatusText() => new()
    {
        FontSize = 12,
        Visibility = Visibility.Collapsed,
    };

    private static Button MakeAccentButton(string text) => new()
    {
        Content = text,
        MinWidth = 110,
        Background = Brush(MenuTheme.AccentColor),
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x14, 0x18)),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
    };

    private static Button MakeCancelButton() => new()
    {
        Content = "Cancel",
        MinWidth = 96,
    };

    /// <summary>Small round icon-only button, same style as the audio editor.</summary>
    private static Button MakeRoundIconButton(Geometry icon, string tooltip, int size, string? colorHex = null)
    {
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = icon,
            Fill = Brush(colorHex ?? MenuTheme.LabelColor),
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
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    /// <summary>Round letter marker button (A / B) for the in/out points.</summary>
    private static Button MakeRoundLetterButton(string letter, string tooltip, string colorHex)
    {
        var text = new TextBlock
        {
            Text = letter,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.ExtraBold,
            Foreground = Brush(colorHex),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var button = new Button
        {
            Content = text,
            Width = 30,
            Height = 30,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            Background = Brush("#FF2B3040"),
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

    private void UpdateProgress(double fraction)
    {
        var pct = Math.Clamp(fraction, 0.0, 1.0) * 100.0;
        _fillColumn.Width = new GridLength(Math.Max(pct, pct > 0 ? 1.5 : 0.001), GridUnitType.Star);
        _emptyColumn.Width = new GridLength(Math.Max(100 - pct, 0.001), GridUnitType.Star);
        _progressPct.Text = ((int)Math.Round(pct)) + " %";
    }

    private void StartExportTimer()
    {
        StopExportTimer();
        _exportStartedAt = DateTime.UtcNow;
        _exportPhase = "Preparing export…";
        _exportTimer = MQ.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _exportTimer.Interval = TimeSpan.FromSeconds(1);
        _exportTimer.IsRepeating = true;
        _exportTimer.Tick += (_, _) => ShowExportPhase();
        _exportTimer.Start();
        ShowExportPhase();
    }

    private void SetExportPhase(string phase)
    {
        _exportPhase = phase;
        ShowExportPhase();
    }

    private void ShowExportPhase()
    {
        var elapsed = DateTime.UtcNow - _exportStartedAt;
        var elapsedText = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
        SetStatus("{0} · elapsed {1}", MenuTheme.AccentColor,
            LocalizationUi.Localized(_exportPhase), elapsedText);
    }

    private void StopExportTimer()
    {
        _exportTimer?.Stop();
        _exportTimer = null;
    }

    private void SetStatus(string message, string colorHex, params object?[] args)
    {
        if (args.Length == 0) LocalizationUi.SetText(_status, message);
        else LocalizationUi.SetFormattedText(_status, message, args);
        _status.Foreground = Brush(colorHex);
        _status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours:0}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 100:0}"
        : $"{time.Minutes:0}:{time.Seconds:00}.{time.Milliseconds / 100:0}";

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
