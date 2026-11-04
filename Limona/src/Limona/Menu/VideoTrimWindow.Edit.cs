using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Limona.Interop;
using Limona.Services;
using Windows.Foundation;

using WPB = Windows.Media.Playback;

namespace Limona.Menu;

/// <summary>Timeline editing: text, stickers and music on top of the cropped
/// video. Each layer has its own lane under the ruler; text and stickers can
/// be dragged and resized directly on the video with the mouse.</summary>
internal sealed partial class VideoTrimWindow
{
    private const double LaneH = 24;
    private const double EdgePx = 7;

    private readonly EditSpec _edit = new();

    private Grid _tracksGrid = null!;
    private readonly Canvas?[] _laneCanvases = new Canvas?[3];
    private Canvas _overlayPreview = null!;     // created by MakeVideoHost
    private StackPanel _inspector = null!;
    private TextBlock? _timeRangeText;

    private readonly List<(Border Block, object Item)> _blocks = new();
    private object? _selected;

    // Track drag state.
    private object? _dragItem;
    private int _dragLane;
    private int _dragEdge;          // -1 head, +1 tail, 0 move
    private double _dragStartX;
    private long _dragStartTicks;
    private long _dragEndTicks;
    private long _dragSourceStart;

    // Preview selection handles (pan / resize).
    private Border? _selBox;
    private UIElement? _selContent;
    private Border? _selCorner;
    private double _selW0, _selH0;
    private readonly Dictionary<object, UIElement> _previewMap = new();

    private sealed class OvDrag
    {
        public object Item = new();
        public bool Resizing;
        public bool IsText;
        public double Sx, Sy;        // pointer start
        public double Cx, Cy;        // content centre
        public double D0 = 1;        // start pointer-to-centre distance
        public double StartXF, StartYF;
        public double StartSize;     // font size fraction or width fraction
        public double W0, H0;        // unscaled content size
    }

    private OvDrag? _ovDrag;

    private WPB.MediaPlayer? _musicPlayer;
    private AudioClip? _musicClip;

    // ------------------------------------------------------------------
    // Tracks area
    // ------------------------------------------------------------------

    private Grid MakeTracksArea()
    {
        _tracksGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (var i = 0; i < 3; i++)
        {
            _tracksGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(LaneH) });
            var lane = new Canvas
            {
                Height = LaneH - 2,
                Margin = new Thickness(8, 1, 8, 1),
                Background = Brush(LaneBrush(i)),
            };
            lane.SizeChanged += (_, _) => UpdateTrackBlocks();
            _laneCanvases[i] = lane;

            var host = new Border
            {
                Child = lane,
                CornerRadius = new CornerRadius(4),
            };
            Grid.SetRow(host, i);
            _tracksGrid.Children.Add(host);
        }
        return _tracksGrid;
    }

    private static string LaneBrush(int lane) => lane switch
    {
        0 => "#FF27304A",
        1 => "#FF312C4E",
        _ => "#FF28403B",
    };

    private static string LaneLabel(int lane) => lane switch
    {
        0 => "T  TEXT",
        1 => "S  STICKER",
        _ => "M  MUSIC",
    };

    private static int LaneOf(object item) => item switch
    {
        TextOverlay => 0,
        ImageOverlay => 1,
        AudioClip => 2,
        _ => 0,
    };

    private static Button MakeEditButton(string text, RoutedEventHandler onClick) =>
        MakeEditButton(text, onClick, 64);

    private static Button MakeEditButton(string text, RoutedEventHandler onClick, double minWidth)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = minWidth,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            Background = Brush("#FF2C3144"),
            Foreground = Brush("#FFE8ECF4"),
            FontSize = 12,
        };
        b.Click += onClick;
        return b;
    }

    private void UpdateTrackBlocks()
    {
        if (_tracksGrid is null) return;
        _blocks.Clear();
        for (var i = 0; i < 3; i++)
        {
            var lane = _laneCanvases[i];
            if (lane is null) continue;
            lane.Children.Clear();
            var tag = new TextBlock
            {
                Text = LaneLabel(i),
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush("#59D9DEEA"),
                Margin = new Thickness(6, 0, 0, 0),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(tag, 6);
            Canvas.SetTop(tag, 5);
            lane.Children.Add(tag);
        }

        var dur = Math.Max(_duration.Ticks, 1);
        foreach (var t in _edit.Texts) AddBlock(t, 0, "#FF8FB7FF", TextLabel(t), dur);
        foreach (var im in _edit.Images) AddBlock(im, 1, "#FFC3A6FF", Path.GetFileName(im.Path), dur);
        foreach (var c in _edit.Audios) AddBlock(c, 2, "#FF7CCD8A", c.Name, dur);
    }

    private void AddBlock(object item, int laneIdx, string hex, string label, long total)
    {
        var lane = _laneCanvases[laneIdx];
        if (lane is null) return;
        var width = Math.Max(lane.ActualWidth, 1);

        long s = item switch
        {
            TextOverlay t => t.StartTicks,
            ImageOverlay im => im.StartTicks,
            AudioClip c => c.StartTicks,
            _ => 0,
        };
        long en = item switch
        {
            TextOverlay t => t.EndTicks,
            ImageOverlay im => im.EndTicks,
            AudioClip c => c.EndTicks,
            _ => s + 1,
        };
        if (en <= s) en = s + TimeSpan.FromSeconds(1).Ticks;

        var x = (double)s / total * width;
        var bw = Math.Max(14, (double)(en - s) / total * width);
        var block = new Border
        {
            Height = LaneH - 6,
            Width = Math.Min(bw, Math.Max(14, width - x)),
            Background = Brush(hex),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(ReferenceEquals(_selected, item) ? 2 : 0),
            BorderBrush = Brush("#FFFFFFFF"),
            Opacity = 0.97,
        };
        block.Child = new TextBlock
        {
            Text = label,
            FontSize = 9.5,
            Margin = new Thickness(5, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x10, 0x12, 0x18)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, 2);
        lane.Children.Add(block);
        _blocks.Add((block, item));

        block.PointerPressed += OnBlockPressed;
        block.PointerMoved += OnBlockMoved;
        block.PointerReleased += OnBlockReleased;
        block.PointerCanceled += OnBlockReleased;
        block.PointerCaptureLost += (_, _) =>
        {
            _dragItem = null;
            _dragEdge = 0;
            SetTrackCursor(block, Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        };
        block.PointerEntered += (_, e) => SetBlockCursor(block, e);
        block.PointerExited += (_, _) =>
        {
            if (_dragItem is null) SetTrackCursor(block, Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        };
    }

    private static string TextLabel(TextOverlay t) =>
        string.IsNullOrWhiteSpace(t.Text) ? "(text)" : t.Text;

    // ------------------------------------------------------------------
    // Track drag (move / trim on the lanes)
    // ------------------------------------------------------------------

    private void OnBlockPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_exporting) return;
        if (sender is not Border block) return;
        var entry = _blocks.FirstOrDefault(b => ReferenceEquals(b.Block, block));
        if (entry.Item is null) return;

        Select(entry.Item, rebuildBlocks: false);

        var lane = block.Parent as Canvas ?? _laneCanvases[_dragLane];
        if (lane is null) return;
        var pt = e.GetCurrentPoint(lane).Position;
        var left = Canvas.GetLeft(block);
        _dragEdge = BlockEdgeAt(pt.X, left, block.Width);
        _dragLane = LaneOf(entry.Item);
        _dragStartX = pt.X;
        _dragItem = entry.Item;
        _dragStartTicks = StartOf(entry.Item);
        _dragEndTicks = EndOf(entry.Item);
        _dragSourceStart = entry.Item is AudioClip ac ? ac.SourceStartTicks : 0;
        SetTrackCursor(block, _dragEdge == 0
            ? Microsoft.UI.Input.InputSystemCursorShape.SizeAll
            : Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        block.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnBlockMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragItem is null)
        {
            if (sender is Border hoveredBlock) SetBlockCursor(hoveredBlock, e);
            return;
        }
        var lane = _laneCanvases[_dragLane];
        if (lane is null) return;
        var pt = e.GetCurrentPoint(lane);
        if (!pt.Properties.IsLeftButtonPressed) return;

        var width = Math.Max(lane.ActualWidth, 1);
        var dur = Math.Max(_duration.Ticks, 1);
        var delta = (long)((pt.Position.X - _dragStartX) / width * dur);
        const long MinLen = 200_000; // 20 ms

        var s = _dragStartTicks;
        var en = _dragEndTicks;
        if (_dragEdge < 0)
        {
            s = Math.Clamp(_dragStartTicks + delta, 0, _dragEndTicks - MinLen);
        }
        else if (_dragEdge > 0)
        {
            en = Math.Clamp(_dragEndTicks + delta, _dragStartTicks + MinLen, dur);
        }
        else
        {
            var len = _dragEndTicks - _dragStartTicks;
            s = Math.Clamp(_dragStartTicks + delta, 0, Math.Max(0, dur - len));
            en = s + len;
        }

        switch (_dragItem)
        {
            case TextOverlay t: t.StartTicks = s; t.EndTicks = en; break;
            case ImageOverlay im: im.StartTicks = s; im.EndTicks = en; break;
            case AudioClip c:
                if (_dragEdge < 0)
                {
                    var shift = s - c.StartTicks;
                    c.SourceStartTicks = Math.Max(0, c.SourceStartTicks + shift);
                }
                c.StartTicks = s;
                c.EndTicks = en;
                break;
        }

        PositionBlock(_dragItem);
        UpdateOverlayPreview();
        RefreshInspectorTimes();
        e.Handled = true;
    }

    private void OnBlockReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragItem is null) return;
        _dragItem = null;
        _dragEdge = 0;
        if (sender is Border b)
        {
            b.ReleasePointerCapture(e.Pointer);
            SetBlockCursor(b, e);
        }
        UpdateTrackBlocks();
        UpdateOverlayPreview();
        RefreshInspectorTimes();
    }

    private static int BlockEdgeAt(double x, double left, double width)
    {
        var edgeWidth = Math.Min(EdgePx, Math.Max(2, width / 3));
        if (x - left <= edgeWidth) return -1;
        if (left + width - x <= edgeWidth) return 1;
        return 0;
    }

    private void SetBlockCursor(Border block, PointerRoutedEventArgs e)
    {
        if (block.Parent is not Canvas lane) return;
        var x = e.GetCurrentPoint(lane).Position.X;
        var edge = BlockEdgeAt(x, Canvas.GetLeft(block), block.Width);
        SetTrackCursor(block, edge == 0
            ? Microsoft.UI.Input.InputSystemCursorShape.SizeAll
            : Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
    }

    private static void SetTrackCursor(UIElement element, Microsoft.UI.Input.InputSystemCursorShape shape)
    {
        CursorInterop.SetCursor(element, Microsoft.UI.Input.InputSystemCursor.Create(shape));
    }

    private void PositionBlock(object item)
    {
        var laneIdx = LaneOf(item);
        var lane = _laneCanvases[laneIdx];
        if (lane is null) return;
        var entry = _blocks.FirstOrDefault(b => ReferenceEquals(b.Item, item));
        if (entry.Block is null) return;
        var w = Math.Max(lane.ActualWidth, 1);
        var dur = Math.Max(_duration.Ticks, 1);
        var s = StartOf(item);
        var e = EndOf(item);
        var x = (double)s / dur * w;
        var bw = (double)(e - s) / dur * w;
        Canvas.SetLeft(entry.Block, x);
        entry.Block.Width = Math.Max(14, Math.Min(bw, Math.Max(14, w - x)));
    }

    private void RefreshHighlight(object? item)
    {
        foreach (var (block, it) in _blocks)
            block.BorderThickness = new Thickness(ReferenceEquals(it, item) ? 2 : 0);
    }

    private static long StartOf(object item) => item switch
    {
        TextOverlay t => t.StartTicks,
        ImageOverlay im => im.StartTicks,
        AudioClip c => c.StartTicks,
        _ => 0,
    };

    private static long EndOf(object item) => item switch
    {
        TextOverlay t => t.EndTicks,
        ImageOverlay im => im.EndTicks,
        AudioClip c => c.EndTicks,
        _ => 0,
    };

    // ------------------------------------------------------------------
    // Preview overlay (inside the output/crop rect) + mouse pan / resize
    // ------------------------------------------------------------------

    private (double ox, double oy, double w, double h) OutputFramePx()
    {
        var (ox, oy, cw, ch) = FramePx();
        if (_cropApplied && _cw > 0.005 && _ch > 0.005)
            return (ox + _cx * cw, oy + _cy * ch, _cw * cw, _ch * ch);
        return (ox, oy, cw, ch);
    }

    private void UpdateOverlayPreview()
    {
        if (_overlayPreview is null) return;
        if (_ovDrag is not null) return; // never rebuild under an active drag

        _overlayPreview.Children.Clear();
        _selBox = null;
        _selCorner = null;
        _selContent = null;
        _previewMap.Clear();
        if (!_loaded || _exporting) return;

        var (ox, oy, fw, fh) = OutputFramePx();
        if (fw < 2 || fh < 2) return;

        var ticks = CurrentPosition().Ticks;
        foreach (var t in _edit.Texts)
        {
            if (t.EndTicks > t.StartTicks && (ticks < t.StartTicks || ticks > t.EndTicks)) continue;
            var fontPx = Math.Max(6, t.FontSizeFraction * fh);
            TextBlock MakeTb(uint argb) => new()
            {
                Text = t.Text,
                FontFamily = new FontFamily(t.FontFamily),
                FontSize = fontPx,
                FontWeight = t.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = t.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                Foreground = new SolidColorBrush(FromArgb(argb)),
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false,
            };
            var tb = MakeTb(t.ColorArgb);
            tb.Opacity = Math.Clamp(t.Opacity, 0, 1);
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var w = Math.Max(8, tb.DesiredSize.Width);
            var h = Math.Max(8, tb.DesiredSize.Height);
            tb.Width = w;
            t.WidthFraction = Math.Clamp(w / fw, 0.005, 2.0);
            var left = ox + t.XFraction * fw - w / 2;
            var top = oy + t.YFraction * fh - h / 2;

            UIElement content = tb;
            var strokeOn = (t.OutlineArgb >> 24) > 0 && t.StrokeFraction > 0;
            if (strokeOn)
            {
                // Live stroke: 8 coloured clones offset around the text.
                var off = Math.Clamp(t.StrokeFraction * fontPx, 1, 12);
                var wrap = new Canvas { Width = w + 2 * off, Height = h + 2 * off, IsHitTestVisible = false };
                (double Dx, double Dy)[] dirs =
                {
                    (1, 0), (-1, 0), (0, 1), (0, -1),
                    (0.71, 0.71), (-0.71, -0.71), (0.71, -0.71), (-0.71, 0.71),
                };
                foreach (var (dx, dy) in dirs)
                {
                    var clone = MakeTb(t.OutlineArgb);
                    clone.Opacity = tb.Opacity;
                    clone.Width = tb.Width;
                    if (tb.Height > 0) clone.Height = tb.Height;
                    clone.TextWrapping = tb.TextWrapping;
                    Canvas.SetLeft(clone, off + dx * off);
                    Canvas.SetTop(clone, off + dy * off);
                    wrap.Children.Add(clone);
                }
                Canvas.SetLeft(tb, off);
                Canvas.SetTop(tb, off);
                wrap.Children.Add(tb);
                left -= off;
                top -= off;
                w += 2 * off;
                h += 2 * off;
                Canvas.SetLeft(wrap, left);
                Canvas.SetTop(wrap, top);
                _overlayPreview.Children.Add(wrap);
                content = wrap;
            }
            else
            {
                Canvas.SetLeft(tb, left);
                Canvas.SetTop(tb, top);
                _overlayPreview.Children.Add(tb);
            }
            if (ReferenceEquals(_selected, t)) AddOverlayHandles(t, content, left, top, w, h);
            else AddOverlayHitBox(t, left, top, w, h);
            _previewMap[t] = content;
        }

        foreach (var im in _edit.Images)
        {
            if (im.EndTicks > im.StartTicks && (ticks < im.StartTicks || ticks > im.EndTicks)) continue;
            if (!File.Exists(im.Path)) continue;
            var imgAspect = 0d;
            if (_imgAspect.TryGetValue(im.Path, out var a) && a > 0.01) imgAspect = a;
            var img = new Image
            {
                Source = new BitmapImage(new Uri(im.Path)),
                Width = Math.Max(10, im.WidthFraction * fw),
                Opacity = Math.Clamp(im.Opacity, 0, 1),
                IsHitTestVisible = false,
            };
            // Known aspect (decoded earlier): size the hit box synchronously.
            // Unknown: the PNG/T-JPEG decodes asynchronously, so DesiredSize is
            // still 0 — place a small provisional picker and re-run the
            // preview build once decode completes.
            var h = imgAspect > 0.01 ? Math.Max(10, img.Width / imgAspect) : Math.Min(48, img.Width);
            img.Height = h;
            var left = ox + im.XFraction * fw - img.Width / 2;
            var top = oy + im.YFraction * fh - h / 2;
            Canvas.SetLeft(img, left);
            Canvas.SetTop(img, top);
            _overlayPreview.Children.Add(img);
            if (ReferenceEquals(_selected, im)) AddOverlayHandles(im, img, left, top, img.Width, h);
            else AddOverlayHitBox(im, left, top, img.Width, h);
            _previewMap[im] = img;
            if (imgAspect <= 0.01) KickImageDecode(im.Path);
        }
    }

    private readonly Dictionary<string, double> _imgAspect = new();

    private void KickImageDecode(string path)
    {
        Task.Run(() =>
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var bmp = System.Drawing.Image.FromStream(fs);
                return bmp.Width <= 0 ? 0d : (double)bmp.Width / bmp.Height;
            }
            catch { return 0d; }
        }).ContinueWith(t =>
        {
            var ratio = t.Status == TaskStatus.RanToCompletion ? t.Result : 0d;
            if (ratio <= 0.01) return;
            _ = _playerHost.DispatcherQueue.TryEnqueue(() =>
            {
                _imgAspect[path] = ratio;
                if (_ovDrag is null) UpdateOverlayPreview();
            });
        });
    }

    /// <summary>Invisible picker over a non-selected overlay: a click selects
    /// it and starts a pan drag (never while the crop tool is armed).</summary>
    private void AddOverlayHitBox(object item, double left, double top, double w, double h)
    {
        var hit = new Border
        {
            Tag = item,
            Background = Brush("#01000000"),
            CornerRadius = new CornerRadius(2),
        };
        PlaceEl(hit, left, top, w, h);
        hit.PointerPressed += OnOverlayPressed;
        hit.PointerMoved += OnOverlayMoved;
        hit.PointerReleased += OnOverlayReleased;
        hit.PointerCanceled += OnOverlayReleased;
        hit.PointerCaptureLost += (_, _) => _ovDrag = null;
        hit.PointerEntered += (_, _) =>
        {
            if (!_ovDragActive)
                SetBarCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
        };
        hit.PointerExited += (_, _) =>
        {
            if (!_ovDragActive) SetBarCursor(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        };
        _overlayPreview.Children.Add(hit);
        _previewMap[item] = hit;
    }

    private bool _ovDragActive => _ovDrag is not null;

    private void AddOverlayHandles(object item, UIElement content, double left, double top, double w, double h)
    {
        _selContent = content;
        _selW0 = w;
        _selH0 = h;

        var box = new Border
        {
            Tag = item,
            BorderBrush = Brush("#FFEFF2FF"),
            BorderThickness = new Thickness(1.3),
            Background = Brush("#01000000"),
            CornerRadius = new CornerRadius(2),
        };
        PlaceEl(box, left, top, w, h);
        box.PointerPressed += OnOverlayPressed;
        box.PointerMoved += OnOverlayMoved;
        box.PointerReleased += OnOverlayReleased;
        box.PointerCanceled += OnOverlayReleased;
        box.PointerCaptureLost += (_, _) => _ovDrag = null;
        box.PointerEntered += (_, _) => SetBarCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
        box.PointerExited += (_, _) =>
        {
            if (_ovDrag is null) SetBarCursor(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        };
        _overlayPreview.Children.Add(box);
        _selBox = box;

        var corner = new Border
        {
            Tag = item,
            Width = 13,
            Height = 13,
            Background = Brush("#FFFFFFFF"),
            BorderBrush = Brush("#FF22273A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
        };
        PlaceEl(corner, left + w - 6.5, top + h - 6.5, 13, 13);
        corner.PointerPressed += OnOverlayPressed;
        corner.PointerMoved += OnOverlayMoved;
        corner.PointerReleased += OnOverlayReleased;
        corner.PointerCanceled += OnOverlayReleased;
        corner.PointerCaptureLost += (_, _) => _ovDrag = null;
        corner.PointerEntered += (_, _) =>
            SetBarCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest);
        corner.PointerExited += (_, _) =>
        {
            if (_ovDrag is null) SetBarCursor(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        };
        _overlayPreview.Children.Add(corner);
        _selCorner = corner;
    }

    private static void PlaceEl(FrameworkElement el, double x, double y, double w, double h)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        el.Width = w;
        el.Height = h;
    }

    private static (double x, double y) ItemPos(object o) => o switch
    {
        TextOverlay t => (t.XFraction, t.YFraction),
        ImageOverlay im => (im.XFraction, im.YFraction),
        _ => (0.5, 0.5),
    };

    private static void SetItemPos(object o, double x, double y)
    {
        switch (o)
        {
            case TextOverlay t: t.XFraction = x; t.YFraction = y; break;
            case ImageOverlay im: im.XFraction = x; im.YFraction = y; break;
        }
    }

    private static double ItemSize(object o, bool isText) => o switch
    {
        TextOverlay t => t.FontSizeFraction,
        ImageOverlay im => im.WidthFraction,
        _ => isText ? 0.08 : 0.3,
    };

    private static void SetItemSize(object o, bool isText, double v)
    {
        switch (o)
        {
            case TextOverlay t: t.FontSizeFraction = v; break;
            case ImageOverlay im: im.WidthFraction = v; break;
        }
    }

    private void OnOverlayPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_exporting || _cropMode) return; // the crop tool owns the canvas while armed
        Limona.Interop.Dbg.Log($"OV press: sender={sender.GetType().Name} tag={(sender as FrameworkElement)?.Tag?.GetType().Name} exporting={_exporting} cropMode={_cropMode}");
        if (sender is not FrameworkElement el || el.Tag is null) return;
        var item = el.Tag;

        var changed = !ReferenceEquals(_selected, item);
        _selected = item;
        BuildInspector();     // sync inspector for the new selection
        UpdateTrackBlocks();  // highlight its lane block

        // Promote the pressed element to the live selection box without
        // rebuilding the preview: a rebuild would destroy the pressed
        // element mid-press and kill the drag (esp. while the video plays).
        if (changed && el is Border hit)
        {
            if (_selBox is not null && ReferenceEquals(_selBox.Parent, _overlayPreview))
                _overlayPreview.Children.Remove(_selBox);
            if (_selCorner is not null && ReferenceEquals(_selCorner.Parent, _overlayPreview))
                _overlayPreview.Children.Remove(_selCorner);
            hit.BorderBrush = Brush("#FFEFF2FF");
            hit.BorderThickness = new Thickness(1.3);
            _selBox = hit;
            _selCorner = null;          // resize handle appears on next release
            _selContent = _previewMap.TryGetValue(item, out var content) ? content : null;
            _selW0 = hit.Width;
            _selH0 = hit.Height;
        }
        else if (changed)
        {
            // Pressed element was not a Border (unexpected): fall back.
            _selContent = _previewMap.TryGetValue(item, out var c) ? c : _selContent;
            if (!double.IsNaN(el.Width) && el.Width > 0) _selW0 = el.Width;
            if (!double.IsNaN(el.Height) && el.Height > 0) _selH0 = el.Height;
        }

        var pt = e.GetCurrentPoint(_cropCanvas);
        var (ox, oy, fw, fh) = OutputFramePx();
        if (fw < 2 || fh < 2) return;

        var resizing = ReferenceEquals(el, _selCorner);
        var (x, y) = ItemPos(item);
        var centreX = ox + x * fw;
        var centreY = oy + y * fh;
        _ovDrag = new OvDrag
        {
            Item = item,
            Resizing = resizing,
            IsText = item is TextOverlay,
            Sx = pt.Position.X,
            Sy = pt.Position.Y,
            Cx = centreX,
            Cy = centreY,
            D0 = Math.Max(8, Math.Sqrt(
                Math.Pow(pt.Position.X - centreX, 2) + Math.Pow(pt.Position.Y - centreY, 2))),
            StartXF = x,
            StartYF = y,
            StartSize = ItemSize(item, item is TextOverlay),
            W0 = _selW0,
            H0 = _selH0,
        };
        SetBarCursor(resizing
            ? Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest
            : Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
        el.CapturePointer(e.Pointer);
        Limona.Interop.Dbg.Log($"OV press ok: resizing={resizing} dragSet={_ovDrag is not null}");
        e.Handled = true;
    }

    private void OnOverlayMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_ovDrag is null) return;
        Limona.Interop.Dbg.Log($"OV move: p=({e.GetCurrentPoint(null).Position.X:0.##},{e.GetCurrentPoint(null).Position.Y:0.##})");
        var pt = e.GetCurrentPoint(_cropCanvas);
        if (!pt.Properties.IsLeftButtonPressed) return;
        var (ox, oy, fw, fh) = OutputFramePx();
        if (fw < 2 || fh < 2) return;
        var d = _ovDrag;

        if (!d.Resizing)
        {
            var xF = Math.Clamp(d.StartXF + (pt.Position.X - d.Sx) / fw, 0, 1);
            var yF = Math.Clamp(d.StartYF + (pt.Position.Y - d.Sy) / fh, 0, 1);
            SetItemPos(d.Item, xF, yF);

            var right = ox + xF * fw - d.W0 / 2;
            var bottom = oy + yF * fh - d.H0 / 2;
            if (_selContent is not null)
            {
                Canvas.SetLeft(_selContent, right);
                Canvas.SetTop(_selContent, bottom);
            }
            if (_selBox is not null) PlaceEl(_selBox, right, bottom, d.W0, d.H0);
            if (_selCorner is not null) PlaceEl(_selCorner, right + d.W0 - 6.5, bottom + d.H0 - 6.5, 13, 13);
        }
        else if (_selContent is not null)
        {
            var dist = Math.Max(2, Math.Sqrt(
                Math.Pow(pt.Position.X - d.Cx, 2) + Math.Pow(pt.Position.Y - d.Cy, 2)));
            var r = Math.Clamp(dist / d.D0, 0.05, 6);
            var maxV = d.IsText ? 0.5 : 1.0;
            var minV = d.IsText ? 0.02 : 0.04;
            var newV = Math.Clamp(d.StartSize * r, minV, maxV);
            SetItemSize(d.Item, d.IsText, newV);

            var scale = newV / d.StartSize;
            _selContent.RenderTransform = new ScaleTransform
            {
                ScaleX = scale,
                ScaleY = scale,
                CenterX = d.W0 / 2,
                CenterY = d.H0 / 2,
            };
            var bw = d.W0 * scale;
            var bh = d.H0 * scale;
            var right = d.Cx - bw / 2;
            var bottom = d.Cy - bh / 2;
            if (_selBox is not null) PlaceEl(_selBox, right, bottom, bw, bh);
            if (_selCorner is not null) PlaceEl(_selCorner, right + bw - 6.5, bottom + bh - 6.5, 13, 13);
        }
        e.Handled = true;
    }

    private void OnOverlayReleased(object sender, PointerRoutedEventArgs e)
    {
        Limona.Interop.Dbg.Log($"OV release: dragSet={_ovDrag is not null}");
        if (_ovDrag is null) return;
        _ovDrag = null;
        try { (sender as FrameworkElement)?.ReleasePointerCapture(e.Pointer); } catch { }
        if (_selContent is not null) _selContent.RenderTransform = null;
        SetBarCursor(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        BuildInspector();      // sync sliders with the new position/size
        UpdateOverlayPreview();
        UpdateTrackBlocks();
    }

    private static Windows.UI.Color FromArgb(uint argb) => Windows.UI.Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    // ------------------------------------------------------------------
    // Selection + inspector
    // ------------------------------------------------------------------

    private void Select(object? item, bool rebuildBlocks = true)
    {
        _selected = item;
        if (rebuildBlocks) UpdateTrackBlocks();
        else RefreshHighlight(item);
        if (item is not null) BuildInspector();
        else
        {
            _inspector.Visibility = Visibility.Collapsed;
            _inspector.Children.Clear();
        }
        UpdateOverlayPreview();
    }

    private void DeleteSelected()
    {
        switch (_selected)
        {
            case TextOverlay t: _edit.Texts.Remove(t); break;
            case ImageOverlay im: _edit.Images.Remove(im); break;
            case AudioClip c:
                _edit.Audios.Remove(c);
                if (_musicClip == c) { _musicPlayer?.Pause(); _musicClip = null; }
                break;
        }
        _selected = null;
        _inspector.Visibility = Visibility.Collapsed;
        _inspector.Children.Clear();
        UpdateTrackBlocks();
        UpdateOverlayPreview();
    }

    private void BuildInspector()
    {
        if (_inspector is null) return;
        _inspector.Children.Clear();
        switch (_selected)
        {
            case TextOverlay t: BuildTextInspector(t); break;
            case ImageOverlay im: BuildImageInspector(im); break;
            case AudioClip c: BuildAudioInspector(c); break;
            default:
                _inspector.Visibility = Visibility.Collapsed;
                return;
        }
        _inspector.Visibility = Visibility.Visible;
        LocalizationUi.ApplyTo(_inspector);
    }

    private void InspectorClear()
    {
        _inspector.Children.Clear();
    }

    private void RefreshInspectorTimes()
    {
        if (_timeRangeText is null || _selected is null) return;
        _timeRangeText.Text = RangeText(StartOf(_selected), EndOf(_selected));
    }

    private static string RangeText(long s, long e) =>
        $"{FormatTime(TimeSpan.FromTicks(s))} → {FormatTime(TimeSpan.FromTicks(e))}";

    private Border MakeRow(params UIElement[] items)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var i in items) sp.Children.Add(i);
        return new Border
        {
            Background = Brush("#FF22273A"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = sp,
        };
    }

    private static TextBlock Lbl(string text) => new()
    {
        Text = text,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brush(MenuTheme.HintColor),
    };

    private static Slider MiniSlider(double min, double max, double value, double width) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = value,
        StepFrequency = 0.01,   // smooth steps; a 0..1 range would otherwise only hit 0/1
        Width = width,
        Height = 22,
        MinHeight = 22,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Button MiniButton(string text, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text,
            Height = 26,
            MinWidth = 40,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11,
            Background = Brush("#FF30364C"),
            Foreground = Brush("#FFE8ECF4"),
        };
        b.Click += onClick;
        return b;
    }

    private static Button ToggleBtn(string text, bool active, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Width = 34,
            Height = 30,
            MinWidth = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Background = Brush(active ? "#FF5063C9" : "#FF30364C"),
            Foreground = Brush(active ? "#FFFFFFFF" : "#FFE8ECF4"),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Button Swatch(string tooltip, Windows.UI.Color color, bool active, Action col)
    {
        var b = new Button
        {
            Width = 22,
            Height = 22,
            MinWidth = 22,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(color),
            BorderThickness = new Thickness(active ? 2 : 1),
            BorderBrush = Brush(active ? "#FFFFFFFF" : "#FF0F1119"),
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(b, tooltip);
        b.Click += (_, _) => col();
        return b;
    }

    private static Button SwatchNone(bool active, Action onClick)
    {
        var b = new Button
        {
            Content = "∅",
            FontSize = 11,
            Width = 22,
            Height = 22,
            MinWidth = 22,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            Background = Brush("#FF22273A"),
            BorderThickness = new Thickness(active ? 2 : 1),
            BorderBrush = Brush(active ? "#FFFFFFFF" : "#FF0F1119"),
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(b, "No stroke");
        b.Click += (_, _) => onClick();
        return b;
    }

    private void BuildTextInspector(TextOverlay t)
    {
        InspectorClear();
        _timeRangeText = null;
        var textBox = new TextBox
        {
            Text = t.Text,
            Width = 200,
            Height = 30,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        textBox.TextChanged += (_, _) =>
        {
            t.Text = textBox.Text;
            UpdateTrackBlocks();
            UpdateOverlayPreview();
        };

        var bold = ToggleBtn("B", t.Bold, () => { t.Bold = !t.Bold; BuildInspector(); UpdateOverlayPreview(); });
        var italic = ToggleBtn("I", t.Italic, () => { t.Italic = !t.Italic; BuildInspector(); UpdateOverlayPreview(); });

        (string Name, uint Argb)[] palette =
        {
            ("White", 0xFFFFFFFF), ("Black", 0xFF000000), ("Yellow", 0xFFFFD54A),
            ("Red", 0xFFED6A5E), ("Blue", 0xFF6FA8FF), ("Green", 0xFF7CCD8A),
            ("Pink", 0xFFFF8FC7), ("Orange", 0xFFFF9F45),
            ("Gray", 0xFF9AA0B4), ("Teal", 0xFF64D2C8),
        };
        var fillSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        fillSp.Children.Add(Lbl("Color"));
        foreach (var (name, argb) in palette)
            fillSp.Children.Add(Swatch("Fill: " + name, FromArgb(argb),
                (t.ColorArgb & 0xFFFFFF) == (argb & 0xFFFFFF),
                col: () => { t.ColorArgb = argb; BuildInspector(); UpdateOverlayPreview(); }));

        var strokeSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        strokeSp.Children.Add(Lbl("Stroke"));
        strokeSp.Children.Add(SwatchNone((t.OutlineArgb >> 24) == 0,
            () => { t.OutlineArgb = 0; BuildInspector(); UpdateOverlayPreview(); }));
        uint[] strokes = { 0xC0000000, 0xC0FFFFFF, 0xC0ED0A0A, 0xC03055D6, 0xC00A8748, 0xC0D9A400 };
        foreach (var s in strokes)
            strokeSp.Children.Add(Swatch("Stroke", FromArgb(s),
                (t.OutlineArgb >> 24) > 0 && (t.OutlineArgb & 0xFFFFFF) == (s & 0xFFFFFF),
                () => { t.OutlineArgb = s; BuildInspector(); UpdateOverlayPreview(); }));

        var thick = MiniSlider(0.01, 0.40, Math.Clamp(t.StrokeFraction, 0.01, 0.40), 90);
        var thickPct = Lbl((int)Math.Round(t.StrokeFraction * 100) + "%");
        thick.ValueChanged += (_, e) =>
        {
            t.StrokeFraction = e.NewValue;
            thickPct.Text = (int)Math.Round(e.NewValue * 100) + "%";
            UpdateOverlayPreview();
        };

        var opacity = MiniSlider(0, 1, t.Opacity, 120);
        var opPct = Lbl((int)Math.Round(t.Opacity * 100) + "%");
        opacity.ValueChanged += (_, e) =>
        {
            t.Opacity = e.NewValue;
            opPct.Text = (int)Math.Round(e.NewValue * 100) + "%";
            UpdateOverlayPreview();
        };

        _inspector.Children.Add(MakeRow(textBox, bold, italic, fillSp, strokeSp,
            Lbl("Width"), thick, thickPct, Lbl("Opacity"), opacity, opPct,
            MiniButton("✕", (_, _) => DeleteSelected())));
    }

    private void BuildImageInspector(ImageOverlay im)
    {
        InspectorClear();
        _timeRangeText = null;
        var name = Lbl(Path.GetFileName(im.Path));
        name.MaxWidth = 160;
        name.TextTrimming = TextTrimming.CharacterEllipsis;

        var opacity = MiniSlider(0, 1, im.Opacity, 120);
        var opPct = Lbl((int)Math.Round(im.Opacity * 100) + "%");
        opacity.ValueChanged += (_, e) =>
        {
            im.Opacity = e.NewValue;
            opPct.Text = (int)Math.Round(e.NewValue * 100) + "%";
            UpdateOverlayPreview();
        };

        _inspector.Children.Add(MakeRow(name,
            Lbl("Opacity"), opacity, opPct, MiniButton("✕", (_, _) => DeleteSelected())));
    }

    private void BuildAudioInspector(AudioClip c)
    {
        InspectorClear();
        var vol = MiniSlider(0, 2, c.Volume, 120);
        var volPct = Lbl((int)Math.Round(c.Volume * 100) + "%");
        vol.ValueChanged += (_, e) =>
        {
            c.Volume = e.NewValue;
            volPct.Text = (int)Math.Round(e.NewValue * 100) + "%";
        };

        var name = Lbl(c.Name);
        name.MaxWidth = 180;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        _timeRangeText = Lbl(RangeText(c.StartTicks, c.EndTicks));

        var trim = Lbl("Trim: " + FormatTime(TimeSpan.FromTicks(c.SourceStartTicks)));
        var setTrim = MiniButton("Trim from here", (_, _) =>
        {
            var pos = CurrentPosition().Ticks;
            var delta = pos - c.StartTicks;
            if (delta < 0) return;
            c.SourceStartTicks = Math.Max(0, c.SourceStartTicks + delta);
            c.StartTicks = pos;
            UpdateTrackBlocks();
            RefreshInspectorTimes();
            trim.Text = Limona.Services.Loc.F("Trim: {0}", FormatTime(TimeSpan.FromTicks(c.SourceStartTicks)));
        });

        _inspector.Children.Add(MakeRow(name, Lbl("Volume"), vol, volPct, trim, setTrim,
            MiniButton("Delete", (_, _) => DeleteSelected())));
        _inspector.Children.Add(MakeRow(_timeRangeText,
            MiniButton("Start here", (_, _) => AlignSelectedStart()),
            MiniButton("End here", (_, _) => AlignSelectedEnd())));
    }

    private void AlignSelectedStart()
    {
        if (_selected is null) return;
        var pos = CurrentPosition().Ticks;
        if (pos >= EndOf(_selected)) return;
        switch (_selected)
        {
            case TextOverlay t: t.StartTicks = pos; break;
            case ImageOverlay im: im.StartTicks = pos; break;
            case AudioClip c:
                var shift = pos - c.StartTicks;
                c.SourceStartTicks = Math.Max(0, c.SourceStartTicks + shift);
                c.StartTicks = pos;
                break;
        }
        UpdateTrackBlocks();
        RefreshInspectorTimes();
        UpdateOverlayPreview();
    }

    private void AlignSelectedEnd()
    {
        if (_selected is null) return;
        var pos = CurrentPosition().Ticks;
        if (pos <= StartOf(_selected)) return;
        switch (_selected)
        {
            case TextOverlay t: t.EndTicks = pos; break;
            case ImageOverlay im: im.EndTicks = pos; break;
            case AudioClip c: c.EndTicks = pos; break;
        }
        UpdateTrackBlocks();
        RefreshInspectorTimes();
        UpdateOverlayPreview();
    }

    // ------------------------------------------------------------------
    // Music preview
    // ------------------------------------------------------------------

    private void SyncMusicPreview(TimeSpan position, bool playing)
    {
        if (_edit.Audios.Count == 0)
        {
            if (_musicPlayer is not null && _musicClip is not null)
            {
                try { _musicPlayer.Pause(); } catch { }
                _musicClip = null;
            }
            return;
        }

        var ticks = position.Ticks;
        var active = _edit.Audios.FirstOrDefault(c => ticks >= c.StartTicks && ticks < c.EndTicks);
        if (active is null)
        {
            if (_musicClip is not null)
            {
                try { _musicPlayer?.Pause(); } catch { }
                _musicClip = null;
            }
            return;
        }

        try
        {
            _musicPlayer ??= new WPB.MediaPlayer { Volume = 1 };
            if (_musicClip != active)
            {
                _musicPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(active.Path));
                _musicClip = active;
            }

            var target = TimeSpan.FromTicks(active.SourceStartTicks + (ticks - active.StartTicks));
            var drift = (_musicPlayer.PlaybackSession?.Position ?? TimeSpan.Zero) - target;
            if (_musicPlayer.PlaybackSession is null || Math.Abs(drift.TotalMilliseconds) > 400)
                _musicPlayer.PlaybackSession!.Position = target;
            if (playing)
            {
                if (_musicPlayer.PlaybackSession!.PlaybackState != WPB.MediaPlaybackState.Playing) _musicPlayer.Play();
            }
            else _musicPlayer.Pause();
        }
        catch (Exception ex)
        {
            Dbg.Log("music preview failed: " + ex.Message);
        }
    }

    private void DisposeMusicPreview()
    {
        try { _musicPlayer?.Pause(); } catch { }
        try { _musicPlayer?.Dispose(); } catch { }
        _musicPlayer = null;
        _musicClip = null;
    }

    // ------------------------------------------------------------------
    // Adding items
    // ------------------------------------------------------------------

    private long DefaultEndTicks(long startTicks)
    {
        var limit = _markB > _markA
            ? _markB.Ticks
            : (_duration > TimeSpan.Zero ? _duration.Ticks : startTicks + TimeSpan.FromSeconds(5).Ticks);
        if (limit <= startTicks) limit = startTicks + TimeSpan.FromSeconds(3).Ticks;
        return limit;
    }

    private void OnAddText(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _exporting) return;
        var start = CurrentPosition().Ticks;
        var t = new TextOverlay
        {
            Text = "Your text",
            StartTicks = start,
            EndTicks = DefaultEndTicks(start),
            FontSizeFraction = 0.08,
            XFraction = 0.5,
            YFraction = 0.5,
            ColorArgb = 0xFFFFFFFF,
            OutlineArgb = 0xC0000000,
        };
        _edit.Texts.Add(t);
        Select(t);
        SetStatus("Text added — drag it on the video or on the timeline.", "#FF9BC1FF");
    }

    private async void OnAddSticker(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _exporting) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".gif");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var start = CurrentPosition().Ticks;
            var im = new ImageOverlay
            {
                Path = file.Path,
                StartTicks = start,
                EndTicks = DefaultEndTicks(start),
                XFraction = 0.5,
                YFraction = 0.5,
                WidthFraction = 0.3,
                Opacity = 1,
            };
            _edit.Images.Add(im);
            Select(im);
            SetStatus("Sticker added: {0}", "#FF9BC1FF", file.Name);
        }
        catch (Exception ex)
        {
            Dbg.Log("add sticker failed: " + ex);
            SetStatus("Could not load the picture: {0}", "#FFED6A5E", Shorten(ex.Message));
        }
    }

    private async void OnAddMusic(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _exporting) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".m4a");
            picker.FileTypeFilter.Add(".aac");
            picker.FileTypeFilter.Add(".wma");
            picker.FileTypeFilter.Add(".flac");
            picker.FileTypeFilter.Add(".mp4");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var start = CurrentPosition().Ticks;
            var clip = new AudioClip
            {
                Path = file.Path,
                Name = file.Name,
                StartTicks = start,
                EndTicks = DefaultEndTicks(start),
                SourceStartTicks = 0,
                Volume = 1.0,
            };
            _edit.Audios.Add(clip);
            Select(clip);
            SetStatus("Music added: {0}", "#FF9BC1FF", file.Name);
        }
        catch (Exception ex)
        {
            Dbg.Log("add music failed: " + ex);
            SetStatus("Could not load the audio: {0}", "#FFED6A5E", Shorten(ex.Message));
        }
    }
}
