using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Limona.Interop;
using Limona.Services;
using Windows.Graphics;

namespace Limona.Menu;

/// <summary>
/// Makes a helper window fit its content exactly (client area = content's
/// DesiredSize at the monitor DPI, keeping it on-screen) and locks the chrome
/// so the user cannot resize or maximize it. Re-fits automatically whenever
/// the content's layout size changes (e.g. rows appear/expand after load).
/// </summary>
internal static class WindowFit
{
    private sealed class TitleState
    {
        public string Source { get; set; } = "";
    }

    private static readonly ConditionalWeakTable<Window, TitleState> s_titles = new();

    public static void Apply(Window window, FrameworkElement root) => ApplyCore(window, root, null);

    /// <summary>
    /// Fixed-size helper window: the content fills the client area instead of
    /// driving the window size. Use it for windows with star-sized rows, whose
    /// DesiredSize is clamped by the window and would otherwise never grow.
    /// </summary>
    public static void ApplyFixed(Window window, FrameworkElement root, int width, int height)
        => ApplyCore(window, root, (width, height));

    public static void SetTitle(Window window, string englishTitle)
    {
        s_titles.GetValue(window, _ => new TitleState()).Source = englishTitle;
        window.Title = TranslateTitle(englishTitle);
    }

    private static void ApplyCore(Window window, FrameworkElement root, (int Width, int Height)? fixedSize)
    {
        var titleState = s_titles.GetValue(window, _ => new TitleState());
        if (string.IsNullOrEmpty(titleState.Source))
            titleState.Source = window.Title;

        void ApplyLanguage()
        {
            root.FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            LocalizationUi.ApplyTo(root);
            window.Title = TranslateTitle(titleState.Source);
        }

        root.Loaded += (_, _) => ApplyLanguage();
        Loc.Changed += ApplyLanguage;
        window.Closed += (_, _) => Loc.Changed -= ApplyLanguage;
        ApplyLanguage();

        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        // App logo in the title bar for every helper window.
        try
        {
            var icon = System.IO.Path.Combine(
                AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(icon))
            {
                window.AppWindow.SetIcon(icon);
            }
        }
        catch (Exception ex)
        {
            Dbg.Log("WindowFit SetIcon THREW: " + ex.Message);
        }

        // Match the app's dark theme: a light caption bar on a dark body
        // looked out of place on every helper window.
        try
        {
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
            Win32.EnableDarkTitleBar(hwnd);
        }
        catch (Exception ex)
        {
            Dbg.Log("WindowFit dark title bar THREW: " + ex.Message);
        }

        if (fixedSize is { } size)
        {
            _ = FitFixedAsync(window, size.Width, size.Height);
            return;
        }

        root.SizeChanged += (_, _) => _ = FitAsync(window, root);
        _ = FitAsync(window, root);
    }

    private static string TranslateTitle(string sourceTitle)
    {
        if (Loc.HasTranslation(sourceTitle))
            return Loc.T(sourceTitle);

        const string convertPrefix = "Convert to ";
        if (sourceTitle.StartsWith(convertPrefix, StringComparison.Ordinal))
            return Loc.F("Convert to {0}", sourceTitle[convertPrefix.Length..]);

        for (var separator = sourceTitle.LastIndexOf(" — ", StringComparison.Ordinal);
             separator > 0;
             separator = sourceTitle.LastIndexOf(" — ", separator - 1, StringComparison.Ordinal))
        {
            var prefix = sourceTitle[..separator];
            if (Loc.HasTranslation(prefix))
                return Loc.T(prefix) + sourceTitle[separator..];
        }

        return sourceTitle;
    }

    private static async Task FitFixedAsync(Window window, int width, int height)
    {
        await Task.Delay(20);

        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
        var dpi = Win32.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96.0;

        var area = DisplayArea.GetFromWindowId(
            window.AppWindow.Id, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        var pxW = Math.Clamp((int)Math.Ceiling(width * scale), 320, work.Width - 24);
        var pxH = Math.Clamp((int)Math.Ceiling(height * scale), 240, work.Height - 24);

        window.AppWindow.ResizeClient(new SizeInt32(pxW, pxH));
        window.AppWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - pxW) / 2),
            work.Y + Math.Max(0, (work.Height - pxH) / 2)));
    }

    private static async Task FitAsync(Window window, FrameworkElement root)
    {
        // Let the layout pass finish so DesiredSize is current.
        await Task.Delay(20);
        root.UpdateLayout();

        var desired = root.DesiredSize;
        if (desired.Width < 2 || desired.Height < 2)
        {
            desired = new Windows.Foundation.Size(root.ActualWidth, root.ActualHeight);
        }
        if (desired.Width < 2 || desired.Height < 2) return; // nothing measurable yet

        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
        var dpi = Win32.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96.0;

        var area = DisplayArea.GetFromWindowId(
            window.AppWindow.Id, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        var pxW = Math.Clamp(
            (int)Math.Ceiling(desired.Width * scale), 160, work.Width - 24);
        var pxH = Math.Clamp(
            (int)Math.Ceiling(desired.Height * scale), 120, work.Height - 24);

        window.AppWindow.ResizeClient(new SizeInt32(pxW, pxH));
        window.AppWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - pxW) / 2),
            work.Y + Math.Max(0, (work.Height - pxH) / 2)));
    }
}
