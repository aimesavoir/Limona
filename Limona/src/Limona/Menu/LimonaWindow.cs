using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Limona.Interop;
using Limona.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

using MQ = Microsoft.UI.Dispatching;
using UIPath = Microsoft.UI.Xaml.Shapes.Path;
using UIColor = Windows.UI.Color;

namespace Limona.Menu;

/// <summary>
/// Frameless, always-on-top, acrylic-backed overlay window rendered as a glass disc
/// with six radial items. Built entirely in code (no XAML templates), so window
/// content only uses primitives (no default control resources).
/// </summary>
internal sealed class LimonaWindow : Window
{
    /// <summary>Raised after the window has been placed (physical px, virtual desktop space).</summary>
    public event Action<int, int, int, int>? Shown;

    /// <summary>Raised after the window finished hiding.</summary>
    public event Action? Hidden;

    private readonly MQ.DispatcherQueue _queue;
    private readonly Grid _root;
    private readonly Canvas _stage;
    private readonly TextBlock _hubLabel;
    private readonly TextBlock _hubHint;
    private readonly List<ItemVisual> _items = new();
    private int _sliceCount = -1;           // how many slices the current layout holds
    private UIElement _hubDisk = null!;     // persistent hub visuals (re-added after each slice rebuild)
    private UIElement _hubGrid = null!;
    private MenuEntry[] _currentEntries = MenuCatalog.DefaultEntries;
    private string? _filePath;

    private Storyboard _entranceStoryboard = null!;
    private Storyboard _hideStoryboard = null!;

    private bool _visible;
    private bool _hideInFlight;
    private int _hoverIndex = -1;
    private string? _fileName;              // file name displayed in hub when no slice is hovered
    private MQ.DispatcherQueueTimer? _closeTimer;
    private IntPtr _blurRgn = IntPtr.Zero;

    public LimonaWindow()
    {
        s_current = this;
        _queue = MQ.DispatcherQueue.GetForCurrentThread();

        Title = "Limona V1.0";

        _hubLabel = MakeHubLabel();
        _hubHint = MakeHubHint();
        _root = BuildRoot();
        _stage = (Canvas)_root.Children[0];

        // Dark, frameless, always-on-top overlay behavior.
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        try { AppWindow.IsShownInSwitchers = false; } catch { }

        this.ExtendsContentIntoTitleBar = true;
        try { TransparentHelper.SetTransparent(this, true); } catch (Exception ex) { Limona.Interop.Dbg.Log("TransparentHelper THREW: " + ex); }

        // Claim topmost once at startup so the WS_EX_TOPMOST style is part of
        // the window from birth; per-show asserts then just re-assert it.
        AssertTopmost("init");

        ConfigureBackdrop();
        AppWindow.Hide();
        Content = _root;

        BuildVisuals();

        BuildHideStoryboard();

        HookPointerEvents();
    }

    // ------------------------------------------------------------------
    // Visual construction
    // ------------------------------------------------------------------

    private Grid BuildRoot()
    {
        var root = new Grid
        {
            // Fully transparent — no background at all. RequestedTheme is intentionally
            // omitted here: setting it causes WinUI to inject a dark system background
            // brush that fights with per-pixel DWM transparency (shows as black).
            // Dark theme is still applied at App level (App.xaml / OnLaunched).
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
        };

        var stage = new Canvas
        {
            Width = MenuTheme.Size,
            Height = MenuTheme.Size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        stage.RenderTransform = new CompositeTransform
        {
            CenterX = MenuTheme.Center,
            CenterY = MenuTheme.Center,
        };
        root.Children.Add(stage);
        return root;
    }

    private TextBlock MakeHubLabel() => new()
    {
        Text = "",
        FontSize = Math.Round(15 * MenuTheme.GlobalScale + 3), // 10@0.66 — readable at small hub
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Brush(MenuTheme.LabelColor),
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = MenuTheme.HubLabelWidth,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private TextBlock MakeHubHint() => new()
    {
        Text = Limona.Services.Loc.T("Ctrl + Right-click · Esc to close"),
        FontSize = Math.Round(9.5 * MenuTheme.GlobalScale + 3),
        Foreground = Brush(MenuTheme.HintColor),
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = MenuTheme.HubHintWidth,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private void ConfigureBackdrop()
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        if (hwnd == IntPtr.Zero) return;

        // Strip standard window chrome (caption, border, resize handles, buttons).
        var style = Win32.GetWindowLongPtrW(hwnd, Win32.GWL_STYLE).ToInt64();
        style &= ~(0x00800000L /* WS_BORDER      */
                 | 0x00C00000L /* WS_CAPTION     */
                 | 0x00040000L /* WS_THICKFRAME  */
                 | 0x00020000L /* WS_MINIMIZEBOX */
                 | 0x00010000L /* WS_MAXIMIZEBOX */
                 | 0x00080000L /* WS_SYSMENU     */);
        style |= 0x80000000L /* WS_POPUP */;
        _ = Win32.SetWindowLongPtrW(hwnd, Win32.GWL_STYLE, new IntPtr(style));

        // WS_EX_NOREDIRECTIONBITMAP: tells DWM not to create an intermediate
        // opaque redirection surface for this window.  With this flag DWM
        // composites the window's DirectX swap chain directly using per-pixel
        // alpha — the transparent areas between/behind the slices are truly
        // see-through instead of showing as solid black.
        var exStyle = Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        exStyle |= 0x00200000L; // WS_EX_NOREDIRECTIONBITMAP
        _ = Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(exStyle));

        // DWM Glass: extend the glass frame over the entire client area.
        var margins = new Win32.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        _ = Win32.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        // DWM Blur Behind with empty/offscreen region tells DWM to enable per-pixel alpha transparency
        _blurRgn = Win32.CreateRectRgn(-2, -2, -1, -1);
        var bb = new Win32.DWM_BLURBEHIND
        {
            dwFlags = 3, // DWM_BB_ENABLE | DWM_BB_BLURREGION
            fEnable = 1,
            hRgnBlur = _blurRgn,
            fTransitionOnMaximized = 0,
        };
        _ = Win32.DwmEnableBlurBehindWindow(hwnd, ref bb);

        // Disable DWM rounded corners
        int corner = 1; // DWMWCP_DONOTROUND
        _ = Win32.DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref corner, sizeof(int));
    }

    private void BuildVisuals()
    {
        // Central hub (persistent, re-added after every slice rebuild): small dark disc + label/hint
        var c = MenuTheme.Center;

        var hub = new Ellipse
        {
            Width = MenuTheme.HubRadius * 2,
            Height = MenuTheme.HubRadius * 2,
            Fill = Brush(MenuTheme.HubFill),
            Stroke = Brush(MenuTheme.HubRing),
            StrokeThickness = 1.2,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(hub, c - MenuTheme.HubRadius);
        Canvas.SetTop(hub, c - MenuTheme.HubRadius);

        var hubGrid = new Grid
        {
            Width = MenuTheme.HubRadius * 2,
            Height = MenuTheme.HubRadius * 2,
            IsHitTestVisible = false,
        };
        hubGrid.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 4,
        });
        var hubStack = (StackPanel)hubGrid.Children[0];
        hubStack.Children.Add(_hubLabel);
        hubStack.Children.Add(_hubHint);
        Canvas.SetLeft(hubGrid, c - MenuTheme.HubRadius);
        Canvas.SetTop(hubGrid, c - MenuTheme.HubRadius);

        _hubDisk = hub;
        _hubGrid = hubGrid;

        RebuildSlices(_currentEntries.Length);
    }

    /// <summary>
    /// (Re)creates the slice visuals for exactly as many entries as the current
    /// file type provides — the slice ring supports any count (3, 6, 7, 10…).
    /// </summary>
    private void RebuildSlices(int count)
    {
        if (count <= 0) count = 1;
        if (_sliceCount == count && _items.Count == count) return;
        _sliceCount = count;
        _hoverIndex = -1;

        _entranceStoryboard?.Stop();
        foreach (var it in _items)
        {
            it.Enter.Stop();
            it.Exit.Stop();
        }

        double c = MenuTheme.Center;
        _stage.Children.Clear();
        _items.Clear();

        // Re-tint the persistent hub disc so theme changes made in Settings
        // are applied on this rebuild (it is re-added below).
        if (_hubDisk is Microsoft.UI.Xaml.Shapes.Ellipse hubEllipse)
        {
            hubEllipse.Fill = Brush(MenuTheme.HubFill);
            hubEllipse.Stroke = Brush(MenuTheme.HubRing);
        }

        for (var i = 0; i < count; i++)
        {
            var (offsetX, offsetY) = RadialOffset(i);
            var (cx, cy) = (c + offsetX, c + offsetY);

            var host = new Canvas
            {
                Width = MenuTheme.Size,
                Height = MenuTheme.Size,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(host, 0);
            Canvas.SetTop(host, 0);

            // 1. Base slice: subtle dark frosted glass, rounded corners
            var geoData = WedgePathString(i);
            var baseSector = new UIPath
            {
                Data = WedgeGeometryFromString(geoData),
                Fill = Brush(MenuTheme.SectorBaseFill),
                Stroke = Brush(MenuTheme.SectorBaseStroke),
                StrokeThickness = 1.0,
                IsHitTestVisible = false,
            };
            host.Children.Add(baseSector);

            // 2. Hover slice: solid accent color, fades in on mouse-over
            var hoverSector = new UIPath
            {
                // NB: a Geometry CANNOT be shared by two Paths (XAML validation) —
                // build a fresh instance from the same path string.
                Data = WedgeGeometryFromString(geoData),
                Fill = Brush(MenuTheme.AccentColor),
                Stroke = Brush(MenuTheme.SectorHoverStroke),
                StrokeThickness = 1.0,
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransform = new CompositeTransform { CenterX = c, CenterY = c },
            };
            host.Children.Add(hoverSector);

            // 3. StackPanel holding Icon + Badge Label inside the slice
            var contentStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 2,
                Width = 56,
                IsHitTestVisible = false,
                RenderTransform = new CompositeTransform
                {
                    CenterX = 28,
                    CenterY = 16,
                },
            };

            var icon = new UIPath
            {
                Data = i < _currentEntries.Length ? _currentEntries[i].Icon() : Icons.Other(),
                Stroke = Brush(MenuTheme.IconColor),
                StrokeThickness = MenuTheme.IconStrokeWidth,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
            };

            var label = new TextBlock
            {
                Text = i < _currentEntries.Length ? _currentEntries[i].DisplayBadge : "",
                FontSize = 9.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush(MenuTheme.IconColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false,
            };

            contentStack.Children.Add(icon);
            contentStack.Children.Add(label);

            Canvas.SetLeft(contentStack, cx - 28);
            Canvas.SetTop(contentStack, cy - 17);
            host.Children.Add(contentStack);

            _stage.Children.Add(host);

            _items.Add(new ItemVisual
            {
                Host = host,
                BaseSector = baseSector,
                HoverSector = hoverSector,
                ContentStack = contentStack,
                Icon = icon,
                Label = label,
                Enter = MakeHoverStoryboard(enter: true, hoverSector, contentStack, icon, label),
                Exit  = MakeHoverStoryboard(enter: false, hoverSector, contentStack, icon, label),
            });
        }

        // Hub renders above the slices.
        _stage.Children.Add(_hubDisk);
        _stage.Children.Add(_hubGrid);

        BuildEntranceStoryboard(); // storyboard keeps references to the new visuals
    }

    /// <summary>
    /// Theme changed (Settings → Menu theme): rebuild hub + slices with the
    /// new colors on the UI thread. Applies immediately for the next open
    /// (or live, if the wheel is currently visible).
    /// </summary>
    public static void RefreshTheme()
    {
        s_current?._queue.TryEnqueue(() => s_current.RebuildMenuVisuals());
    }

    private void RebuildMenuVisuals()
    {
        // NB: rebuilding the hidden/never-shown wheel window out of band triggers
        // WinUI COMException 0x800F1000 — instead mark the layout dirty; the
        // next ShowAt always calls RebuildSlices, which then rebuilds the hub +
        // slices (and brushes) with the freshly selected palette.
        _sliceCount = -1;
        Limona.Interop.Dbg.Log("LimonaWindow theme marked dirty — colors apply at next open");
    }

    private void HookPointerEvents()
    {
        _stage.PointerExited += (_, _) => SetHover(-1);
        _stage.PointerMoved += (_, e) => SetHover(HitTest(e.GetCurrentPoint(_stage).Position));
        _stage.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(_stage);
            if (!point.Properties.IsLeftButtonPressed)
            {
                CloseAnimated();
                e.Handled = true;
                return;
            }
            SetHover(HitTest(point.Position));
        };
        _stage.PointerReleased += (_, e) =>
        {
            var point = e.GetCurrentPoint(_stage);
            if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return;

            var index = HitTest(point.Position);
            if (index >= 0) ExecuteItem(index);
            else CloseAnimated(); // hub / glass margin click: dismiss
            e.Handled = true;
        };
        _stage.PointerCanceled += (_, _) => CloseAnimated();
        _stage.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape) CloseAnimated();
        };
    }

    // ------------------------------------------------------------------
    // Storyboards
    // ------------------------------------------------------------------

    private void BuildEntranceStoryboard()
    {
        _entranceStoryboard = new Storyboard();
        AddFade(_entranceStoryboard, _root, 0, 1, ms: 200);
        AddScale(_entranceStoryboard, _stage, 0.86, 1.0, ms: 260, overshoot: true);

        for (var i = 0; i < _items.Count; i++)
        {
            var begin = TimeSpan.FromMilliseconds(40 + i * 22);
            var item = _items[i];

            AddFade(_entranceStoryboard, item.Host, 0, 1, ms: 170, begin);
            AddScale(_entranceStoryboard, item.ContentStack, 0.55, 1.0, ms: 210, begin: begin);
        }
    }

    private void BuildHideStoryboard()
    {
        _hideStoryboard = new Storyboard();
        AddFade(_hideStoryboard, _root, 1, 0, ms: 140);
        AddScale(_hideStoryboard, _stage, 1, 0.94, ms: 140);
        _hideStoryboard.Completed += (_, _) =>
        {
            if (!_hideInFlight) return;
            _hideInFlight = false;
            AppWindow.Hide();
            Hidden?.Invoke();
        };
    }

    private static Storyboard MakeHoverStoryboard(bool enter, UIElement hoverSector, UIElement contentStack, UIPath icon, TextBlock label)
    {
        var sb = new Storyboard();

        // White wedge highlight smoothly fades in/out over the sector.
        AddFade(sb, hoverSector, double.NaN, enter ? 1.0 : 0.0, ms: 140);
        AddScale(sb, hoverSector, double.NaN, enter ? 1.02 : 1.0, ms: 140);

        // Content stack gently scales up slightly on hover.
        AddScale(sb, contentStack, double.NaN, enter ? 1.10 : 1.0, ms: 140);

        // Icon color smoothly transitions: White -> Dark Obsidian on hover (and back).
        var iconColor = new ColorAnimation
        {
            To = ParseColor(enter ? MenuTheme.IconHoverColor : MenuTheme.IconColor),
            Duration = new Duration(TimeSpan.FromMilliseconds(130)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(iconColor, icon);
        Storyboard.SetTargetProperty(iconColor, "(Path.Stroke).(SolidColorBrush.Color)");
        sb.Children.Add(iconColor);

        var labelColor = new ColorAnimation
        {
            To = ParseColor(enter ? MenuTheme.IconHoverColor : MenuTheme.IconColor),
            Duration = new Duration(TimeSpan.FromMilliseconds(130)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(labelColor, label);
        Storyboard.SetTargetProperty(labelColor, "(TextBlock.Foreground).(SolidColorBrush.Color)");
        sb.Children.Add(labelColor);

        return sb;
    }

    // ------------------------------------------------------------------
    // Show / dismiss
    // ------------------------------------------------------------------

    public void ShowAt(int cursorX, int cursorY, string? fileName = null, string? filePath = null)
    {
        ToolWindowCoordinator.CloseActive();
        Limona.Interop.Dbg.Log($"ShowAt start ({cursorX},{cursorY}) file='{fileName}' path='{filePath}'");
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var dpi = Win32.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96.0;

        var pxSize = (int)Math.Ceiling(MenuTheme.Size * scale);

        // Center on the cursor, clamped into the nearest monitor's work area
        // (multi-monitor + off-screen-edge safe).
        var area = DisplayArea.GetFromPoint(new PointInt32(cursorX, cursorY), DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        var x = cursorX - pxSize / 2;
        var y = cursorY - pxSize / 2;
        if (x + pxSize > work.X + work.Width) x = work.X + work.Width - pxSize;
        if (y + pxSize > work.Y + work.Height) y = work.Y + work.Height - pxSize;
        if (x < work.X) x = work.X;
        if (y < work.Y) y = work.Y;

        _visible = true;
        _hideInFlight = false;
        _hoverIndex = -1;
        StopCloseTimer();

        _hideStoryboard.Stop();
        _entranceStoryboard.Stop();
        foreach (var item in _items)
        {
            item.Enter.Stop();
            item.Exit.Begin(); // To-only animations: ease any leftover visual state to rest.
        }

        _fileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName;
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;

        // Dynamically resolve entries for the targeted file
        _currentEntries = MenuCatalog.GetEntriesForTarget(_filePath, _fileName);

        // Rebuild the slice ring if the entry count changed
        // (the layout supports any slice count: 3, 6, 7, 10…).
        RebuildSlices(_currentEntries.Length);

        // Update slices with dynamic entries
        for (var i = 0; i < _items.Count && i < _currentEntries.Length; i++)
        {
            var entry = _currentEntries[i];
            _items[i].Icon.Data = entry.Icon();
            _items[i].Label.Text = entry.DisplayBadge;
            _items[i].Icon.Stroke = Brush(MenuTheme.IconColor);
            _items[i].Label.Foreground = Brush(MenuTheme.IconColor);
        }

        _hubLabel.Text = _fileName ?? "Limona";
        _hubLabel.Foreground = _fileName is null ? Brush(MenuTheme.LabelColor) : Brush(MenuTheme.AccentColor);
        if (!string.IsNullOrEmpty(_filePath) && System.IO.Directory.Exists(_filePath))
        {
            _hubHint.Text = Limona.Services.Loc.T("Choose a folder action");
        }
        else if (!string.IsNullOrEmpty(_filePath) && Limona.Services.ImageConverter.IsImageFile(_filePath))
        {
            _hubHint.Text = Limona.Services.Loc.T("Select format to convert");
        }
        else
        {
            _hubHint.Text = Limona.Services.Loc.T("Ctrl + Right-click · Esc to close");
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, pxSize, pxSize));
        AppWindow.Show(activateWindow: false); // do not steal focus (overlay menu behavior)

        Limona.Interop.Dbg.Log($"ShowAt shown at ({x},{y}) size {pxSize} dpi {dpi}");

        // Re-assert topmost immediately, then AGAIN after ~250 ms: WinUI's
        // presenter rewrites window styles asynchronously after Hide()/Show()
        // cycles and wipes a one-shot SetWindowPos — the overlay then renders
        // BEHIND the foreground app. The delayed re-assert wins that race.
        AssertTopmost("show");
        _ = ScheduleTopmostReAssert();

        _entranceStoryboard.Begin();
        Limona.Interop.Dbg.Log("ShowAt entrance storyboard started");

        Shown?.Invoke(x, y, pxSize, pxSize);
    }

    private bool AssertTopmost(string phase)
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        // 1. WinUI presenter level: the OverlappedPresenter owns the window
        //    styles and rewrites them asynchronously, so keep toggling its
        //    always-on-top flag back on before touching raw styles.
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter &&
                !presenter.IsAlwaysOnTop)
            {
                presenter.IsAlwaysOnTop = true;
            }
        }
        catch (Exception ex) { Limona.Interop.Dbg.Log("Topmost presenter THREW: " + ex.Message); }

        // 2. Force the WS_EX_TOPMOST style bit directly: WinUI's presenter
        //    rewrites styles asynchronously after Show() and can leave the
        //    window non-topmost, so SetWindowPos alone is not reliable.
        var before = Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        var setLong = Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(before | 0x00000008L));
        var lastErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();

        // 3. Z-order via SetWindowPos; SWP_FRAMECHANGED makes the style bit
        //    from step 2 actually take effect.
        var ok = Win32.SetWindowPos(
            hwnd, Win32.HwndTopmost, 0, 0, 0, 0,
            Win32.SWP_NO_SIZE | Win32.SWP_NO_MOVE | Win32.SWP_NO_ACTIVATE |
            Win32.SWP_SHOW_WINDOW | 0x00000020 /* SWP_FRAMECHANGED */);
        var after = Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
        var presenterTop = AppWindow.Presenter is OverlappedPresenter p ? p.IsAlwaysOnTop : (bool?)null;
        Limona.Interop.Dbg.Log($"Topmost[{phase}] ok={ok} setLong=0x{setLong.ToInt64():X} err={lastErr} presenterTop={presenterTop} exstyle before=0x{before:X} after=0x{after.ToInt64():X}");
        return ok && (after.ToInt64() & 0x00000008L) != 0;
    }

    private async System.Threading.Tasks.Task ScheduleTopmostReAssert()
    {
        // Multiple retries: the WinUI presenter's async style rewrite can land
        // heavily delayed after Hide()/Show() cycles and wipe a one-shot assert.
        foreach (var delayMs in new[] { 100, 300, 700, 1200 })
        {
            await System.Threading.Tasks.Task.Delay(delayMs);
            var queued = _queue.TryEnqueue(() =>
            {
                if (_visible) AssertTopmost("reassert");
            });
            if (!queued) return;
        }
    }

    /// <summary>Dismiss: slight scale-down + fade, then hide the real window.</summary>
    public void CloseAnimated()
    {
        if (!_visible) return;
        _visible = false;
        _hideInFlight = true;
        SetHover(-1);
        Limona.Interop.Dbg.Log("CloseAnimated starting");
        _hideStoryboard.Begin();
    }

    internal static void HideForToolWindow() => s_current?.HideImmediately();

    private void HideImmediately()
    {
        if (!_visible && !_hideInFlight) return;

        _visible = false;
        _hideInFlight = false;
        StopCloseTimer();
        _entranceStoryboard.Stop();
        _hideStoryboard.Stop();
        SetHover(-1);
        AppWindow.Hide();
        Hidden?.Invoke();
    }

    // ------------------------------------------------------------------
    // Interaction
    // ------------------------------------------------------------------

    private void SetHover(int index)
    {
        if (!_visible && index >= 0) return; // ignore hover state while hidden/closing
        if (index == _hoverIndex) return;

        if (_hoverIndex >= 0)
        {
            var prev = _items[_hoverIndex];
            prev.Enter.Stop();
            prev.Exit.Begin();
        }

        _hoverIndex = index;
        if (index < 0)
        {
            // Restore default hub state when no slice is hovered
            _hubLabel.Text = _fileName ?? "Limona";
            _hubLabel.Foreground = _fileName is null ? Brush(MenuTheme.LabelColor) : Brush(MenuTheme.AccentColor);
            if (!string.IsNullOrEmpty(_filePath) && System.IO.Directory.Exists(_filePath))
            {
                _hubHint.Text = Limona.Services.Loc.T("Choose a folder action");
            }
            else if (!string.IsNullOrEmpty(_filePath) && Limona.Services.ImageConverter.IsImageFile(_filePath))
            {
                _hubHint.Text = Limona.Services.Loc.T("Select format to convert");
            }
            else
            {
                _hubHint.Text = Limona.Services.Loc.T("Ctrl + Right-click · Esc to close");
            }
            return;
        }

        var item = _items[index];
        item.Exit.Stop();
        item.Enter.Begin();

        var entry = _currentEntries[index];
        _hubLabel.Text = entry.DisplayLabel;
        _hubLabel.Foreground = Brush(MenuTheme.LabelColor);
        _hubHint.Text = entry.DisplayHint;
    }

    private async void ExecuteItem(int index)
    {
        if (index < 0 || index >= _currentEntries.Length) return;
        var entry = _currentEntries[index];

        _hubLabel.Text = entry.DisplayLabel;
        _hubLabel.Foreground = Brush(MenuTheme.AccentColor);

        // Settings slice opens the settings window (no file targeted).
        if (entry.Label.Equals("Settings", StringComparison.OrdinalIgnoreCase))
        {
            CloseAnimated();
            _hoverIndex = -1;
            SettingsWindow.Show();
            return;
        }

        // Folder-only slices: interactive window / quick actions / status read.
        if (!string.IsNullOrEmpty(_filePath) && System.IO.Directory.Exists(_filePath))
        {
            // One entry point for every icon-related feature: the customization
            // window hosts the library, cover, emoji, badge, monogram and
            // pattern tabs, so the wheel itself stays compact.
            if (entry.Label.Equals("Icon", StringComparison.OrdinalIgnoreCase))
            {
                var folderPath = _filePath!;
                CloseAnimated();
                _hoverIndex = -1;
                FolderCustomizationWindow.ShowForFile(folderPath, FolderCustomTab.Library);
                return;
            }

            if (entry.Label.StartsWith("Copy Path", StringComparison.OrdinalIgnoreCase))
            {
                _hubHint.Text = Limona.Services.Loc.T("Path copied");
                try
                {
                    var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage
                    {
                        RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
                    };
                    pkg.SetText(_filePath!);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                }
                catch (Exception ex)
                {
                    _hubLabel.Text = Limona.Services.Loc.T("Clipboard blocked");
                    _hubHint.Text = ex.Message.Split('\n')[0];
                }
                StartCloseTimer();
                _hoverIndex = -1;
                return;
            }

            if (entry.Label.StartsWith("Size", StringComparison.OrdinalIgnoreCase))
            {
                var sizePath = _filePath!;
                _hubLabel.Text = Limona.Services.Loc.T("Measuring...");
                _hubHint.Text = Limona.Services.Loc.T("Walking the folder tree");
                try
                {
                    var stat = await FolderService.ComputeSizeAsync(sizePath);
                    _hubLabel.Text = FormatBytes(stat.Bytes);
                    _hubHint.Text = Limona.Services.Loc.F("{0} files · {1} folders", stat.Files, stat.Folders);
                }
                catch (Exception ex)
                {
                    _hubLabel.Text = Limona.Services.Loc.T("Error");
                    _hubHint.Text = ex.Message.Split('\n')[0];
                }
                StartCloseTimer(1400);
                _hoverIndex = -1;
                return;
            }

            if (entry.Label.StartsWith("Terminal", StringComparison.OrdinalIgnoreCase))
            {
                var terminalFolder = _filePath!;
                var opened = false;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "wt.exe",
                        Arguments = $"-d \"{terminalFolder}\"",
                        UseShellExecute = true,
                    });
                    opened = true;
                }
                catch { }
                if (!opened)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/K cd /d \"{terminalFolder}\"",
                            UseShellExecute = true,
                        });
                        opened = true;
                    }
                    catch (Exception ex)
                    {
                        _hubLabel.Text = Limona.Services.Loc.T("Error");
                        _hubHint.Text = ex.Message.Split('\n')[0];
                        StartCloseTimer();
                        _hoverIndex = -1;
                        return;
                    }
                }
                _hubLabel.Text = Limona.Services.Loc.T("Terminal opened");
                _hubHint.Text = System.IO.Path.GetFileName(_filePath!.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                StartCloseTimer();
                _hoverIndex = -1;
                return;
            }

            // "Compress ZIP" falls through to the generic Action branch below.
        }

        // Remove Background opens the interactive preview window instead of
        // saving directly, so the user can tune the cutout before saving.
        if (entry.Label.Contains("Background", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_filePath))
        {
            var previewPath = _filePath;
            CloseAnimated();
            _hoverIndex = -1;
            BackgroundPreviewWindow.ShowForFile(previewPath);
            return;
        }

        // Edit opens the image editor (color · rotate · flip · crop).
        if (entry.Label.Equals("Edit", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_filePath) &&
            ImageConverter.IsImageFile(_filePath))
        {
            var editPath = _filePath;
            CloseAnimated();
            _hoverIndex = -1;
            ImageEditWindow.ShowForFile(editPath);
            return;
        }

        // Resize opens the interactive resize window (width / height / exact size).
        if (entry.Label.StartsWith("Resize", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_filePath) &&
            ImageConverter.IsImageFile(_filePath))
        {
            var resizePath = _filePath!;
            CloseAnimated();
            _hoverIndex = -1;
            ResizeWindow.ShowForFile(resizePath);
            return;
        }

        // Edit Audio opens the waveform editor (trim / cut / split / export).
        if (entry.Label.StartsWith("Edit Audio", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_filePath))
        {
            var editPath = _filePath!;
            CloseAnimated();
            _hoverIndex = -1;
            AudioEditWindow.ShowForFile(editPath);
            return;
        }

        // Video slices open their interactive windows:
        //   · Convert to MP4  → quality settings + progress
        //   · Extract Audio   → audio settings window (target MP3 first)
        //   · Edit Video      → A/B trim preview + MP4 export
        if (!string.IsNullOrEmpty(_filePath) &&
            VideoConverter.IsVideoFile(_filePath))
        {
            if (entry.Label.StartsWith("Convert to", StringComparison.OrdinalIgnoreCase))
            {
                var target = entry.Label["Convert to ".Length..].Trim().ToUpperInvariant();
                if (target is "MP4" or "MOV" or "WMV" or "GIF")
                {
                    var videoPath = _filePath!;
                    CloseAnimated();
                    _hoverIndex = -1;
                    VideoConvertWindow.ShowForFile(videoPath, target);
                    return;
                }
            }

            if (entry.Label.StartsWith("Extract Audio", StringComparison.OrdinalIgnoreCase))
            {
                var audioPath = _filePath!;
                CloseAnimated();
                _hoverIndex = -1;
                VideoExtractWindow.ShowForFile(audioPath);
                return;
            }

            if (entry.Label.StartsWith("Edit Video", StringComparison.OrdinalIgnoreCase))
            {
                var trimPath = _filePath!;
                CloseAnimated();
                _hoverIndex = -1;
                VideoTrimWindow.ShowForFile(trimPath);
                return;
            }
        }

        // Audio converts open a per-format settings window first; "Edit Audio"
        // is a placeholder slice for a future effect editor.
        if (entry.Label.StartsWith("Convert to", StringComparison.OrdinalIgnoreCase) &&
            AudioConverter.IsAudioFile(_filePath ?? ""))
        {
            var ext = entry.Label["Convert to ".Length..];
            if (ext is "MP3" or "WAV" or "M4A" or "OGG")
            {
                var audioPath = _filePath!;
                CloseAnimated();
                _hoverIndex = -1;
                AudioConvertWindow.ShowForFile(audioPath, "." + ext.ToLowerInvariant(), ext);
                return;
            }
        }

        if (entry.Action is not null && !string.IsNullOrEmpty(_filePath))
        {
            _hubHint.Text = Limona.Services.Loc.T("Working...");
            try
            {
                var resultPath = await entry.Action(_filePath);
                var resultName = System.IO.Path.GetFileName(resultPath);
                _hubLabel.Text = Limona.Services.Loc.T("Done!");
                _hubHint.Text = Limona.Services.Loc.F("Saved: {0}", resultName);
                try { Win32.MessageBeep(0x00000040 /* MB_ICONASTERISK */); } catch { }
            }
            catch (Exception ex)
            {
                Limona.Interop.Dbg.Log("ExecuteItem action error: " + ex);
                _hubLabel.Text = Limona.Services.Loc.T("Error");
                var shortMsg = ex.Message.Split('\n')[0];
                _hubHint.Text = shortMsg.Length > 50 ? shortMsg[..50] + "…" : shortMsg;
            }
        }
        else
        {
            if (OfficeConverter.IsOfficeFile(_filePath ?? ""))
            {
                _hubLabel.Text = Limona.Services.Loc.T("Coming Soon");
                _hubHint.Text = Limona.Services.Loc.F("{0} feature in development", entry.DisplayBadge);
            }
            else
            {
                _hubHint.Text = entry.DisplayHint;
            }
        }

        var timer = _queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(900);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CloseAnimated();
        };
        timer.Start();
        _closeTimer = timer;

        _hoverIndex = -1;
    }

    /// <summary>One-shot hub auto-close after a quick action completes.</summary>
    private void StartCloseTimer(int durationMs = 900)
    {
        StopCloseTimer();
        var timer = _queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(durationMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CloseAnimated();
        };
        timer.Start();
        _closeTimer = timer;
    }

    /// <summary>Human-readable byte count (B / KB / MB / GB / TB).</summary>
    private static string FormatBytes(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024, tb = gb * 1024;
        var fraction = System.Convert.ToDouble(bytes);
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < mb) return $"{fraction / kb:0.#} KB";
        if (bytes < gb) return $"{fraction / mb:0.#} MB";
        if (bytes < tb) return $"{fraction / gb:0.##} GB";
        return $"{fraction / tb:0.##} TB";
    }

    private int HitTest(Point position)
    {
        var dx = position.X - MenuTheme.Center;
        var dy = position.Y - MenuTheme.Center;
        var dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist <= MenuTheme.HubRadius + 4) return -1;
        if (dist > MenuTheme.SectorOuterRadius) return -1;

        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI; // -180..180, 0 = right
        var rel = (angle + 90.0 + 360.0) % 360.0;         // 0 at top, then clockwise
        var span = 360.0 / Math.Max(1, _sliceCount);
        return (int)Math.Round(rel / span) % _sliceCount;
    }

    private void StopCloseTimer()
    {
        if (_closeTimer is null) return;
        _closeTimer.Stop();
        _closeTimer = null;
    }

    private (double OffsetX, double OffsetY) RadialOffset(int index)
    {
        var angle = SliceAngleDeg(index) * Math.PI / 180.0; // item 0 at top, clockwise
        return (Math.Cos(angle) * MenuTheme.RingRadius, Math.Sin(angle) * MenuTheme.RingRadius);
    }

    private double SliceSpanDeg => 360.0 / Math.Max(1, _sliceCount);

    /// <summary>Center angle (degrees) of a slice: item 0 at top, clockwise.</summary>
    private double SliceAngleDeg(int index) => -90.0 + index * SliceSpanDeg;

    private static Point PointAt(double angleDeg, double radius)
    {
        var rad = angleDeg * Math.PI / 180.0;
        return new Point(
            MenuTheme.Center + Math.Cos(rad) * radius,
            MenuTheme.Center + Math.Sin(rad) * radius);
    }

    /// <summary>
    /// Generates a pie slice (annular sector) with smooth rounded corners on all 4 vertices.
    /// Uses SVG mini-language path string to avoid WinUI3 PathGeometry COM validation bugs
    /// with ArcSegment counterclockwise segments.
    /// </summary>
    private static string WedgePathString(int index) => s_current!.Wedge(index, MenuTheme.SliceCornerRadius);

    private static LimonaWindow? s_current;

    private string Wedge(int index, double r)
    {
        var a = SliceAngleDeg(index); // sector center angle
        var halfSpan = Math.Max(2.0, SliceSpanDeg / 2.0 - 4.0); // ~4° gap between neighbors
        var a1 = a - halfSpan;
        var a2 = a + halfSpan;

        var rIn = MenuTheme.SectorInnerRadius;
        var rOut = MenuTheme.SectorOuterVisualRadius;

        // Angular offset in degrees corresponding to corner radius r at each radius
        var dThetaIn = (r / rIn) * (180.0 / Math.PI);
        var dThetaOut = (r / rOut) * (180.0 / Math.PI);

        // Clamp so the corner radius never exceeds half the sector arc
        var maxDTheta = halfSpan * 0.4;
        if (dThetaIn > maxDTheta) dThetaIn = maxDTheta;
        if (dThetaOut > maxDTheta) dThetaOut = maxDTheta;

        // Build path as SVG mini-language string — robust against WinUI3 COM geometry validation
        // M = MoveTo, L = LineTo, Q = QuadraticBezier, A = Arc, Z = Close
        static string Pt(Point p) =>
            p.X.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," +
            p.Y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

        static string Sz(double r) =>
            r.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

        var p0  = PointAt(a1, rIn + r);             // start: inner-left, past corner
        var p1  = PointAt(a1, rOut - r);            // inner end of left radial
        var cp2 = PointAt(a1, rOut);                // outer-left corner control
        var p2  = PointAt(a1 + dThetaOut, rOut);   // outer-left end of outer arc
        var p3  = PointAt(a2 - dThetaOut, rOut);   // outer-right start of outer arc
        var cp4 = PointAt(a2, rOut);                // outer-right corner control
        var p4  = PointAt(a2, rOut - r);            // outer end of right radial
        var p5  = PointAt(a2, rIn + r);             // inner end of right radial
        var cp6 = PointAt(a2, rIn);                 // inner-right corner control
        var p6  = PointAt(a2 - dThetaIn, rIn);     // inner-right start of inner arc
        var p7  = PointAt(a1 + dThetaIn, rIn);     // inner-left end of inner arc
        var cp8 = PointAt(a1, rIn);                 // inner-left corner control

        var path =
            $"M {Pt(p0)}" +
            $" L {Pt(p1)}" +
            $" Q {Pt(cp2)} {Pt(p2)}" +
            $" A {Sz(rOut)},{Sz(rOut)} 0 0 1 {Pt(p3)}" +
            $" Q {Pt(cp4)} {Pt(p4)}" +
            $" L {Pt(p5)}" +
            $" Q {Pt(cp6)} {Pt(p6)}" +
            $" A {Sz(rIn)},{Sz(rIn)} 0 0 0 {Pt(p7)}" +
            $" Q {Pt(cp8)} {Pt(p0)}" +
            " Z";

        return path;
    }

    private static Geometry WedgeGeometryFromString(string path) =>
        (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(
            typeof(Geometry), path);

    // ------------------------------------------------------------------
    // Brushes / geometry / animation helpers
    // ------------------------------------------------------------------

    private static SolidColorBrush Brush(string hex) => new(ParseColor(hex));

    private static UIColor ParseColor(string hex)
    {
        if (hex.Length != 9 || hex[0] != '#' || !hex.AsSpan(1).ToArray().All(Uri.IsHexDigit))
        {
            Limona.Interop.Dbg.Log("ParseColor: malformed color '" + hex + "' — using obsidian fallback");
            hex = "#FF181A22";
        }
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        var a = (byte)((value >> 24) & 0xFF);
        var r = (byte)((value >> 16) & 0xFF);
        var g = (byte)((value >> 8) & 0xFF);
        var b = (byte)(value & 0xFF);
        return UIColor.FromArgb(a, r, g, b);
    }

    private static void AddFade(Storyboard sb, DependencyObject target, double from, double to, int ms, TimeSpan? begin = null)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        if (!double.IsNaN(from)) anim.From = from;
        if (begin is { } b) anim.BeginTime = b;
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
    }

    private static void AddScale(Storyboard sb, DependencyObject target, double from, double to, int ms,
        bool overshoot = false, TimeSpan? begin = null)
    {
        foreach (var axis in new[] { "ScaleX", "ScaleY" })
        {
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
                EasingFunction = overshoot
                    ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 }
                    : new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            if (!double.IsNaN(from)) anim.From = from;
            if (begin is { } b) anim.BeginTime = b;
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, "(UIElement.RenderTransform).(CompositeTransform." + axis + ")");
            sb.Children.Add(anim);
        }
    }

    /// <summary>Per-item interaction visuals + hover storyboards.</summary>
    private sealed class ItemVisual
    {
        public required Canvas Host { get; init; }
        public required UIPath BaseSector { get; init; }
        public required UIPath HoverSector { get; init; }
        public required StackPanel ContentStack { get; init; }
        public required UIPath Icon { get; init; }
        public required TextBlock Label { get; init; }
        public required Storyboard Enter { get; init; }
        public required Storyboard Exit { get; init; }
    }
}
