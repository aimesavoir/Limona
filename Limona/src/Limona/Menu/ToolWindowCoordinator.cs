using Microsoft.UI.Xaml;

namespace Limona.Menu;

internal static class ToolWindowCoordinator
{
    private static Window? s_activeWindow;

    public static TWindow Open<TWindow>(Func<TWindow> createWindow)
        where TWindow : Window
    {
        CloseActive();
        LimonaWindow.HideForToolWindow();

        var window = createWindow();
        s_activeWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(s_activeWindow, window))
                s_activeWindow = null;
        };
        window.Activate();
        return window;
    }

    public static void CloseActive()
    {
        var window = s_activeWindow;
        if (window is null) return;

        window.Close();
        if (ReferenceEquals(s_activeWindow, window))
            s_activeWindow = null;
    }
}
