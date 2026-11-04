using System.Runtime.InteropServices;
using Windows.UI.Composition;
using WinRT;
using WinCompositor = Windows.UI.Composition.Compositor;
using WinDispatcherQueue = Windows.System.DispatcherQueue;
using WinDispatcherQueueController = Windows.System.DispatcherQueueController;

namespace Limona.Menu;

internal static class WindowsCompositionHelper
{
    private static WinCompositor? s_compositor;
    private static WinDispatcherQueueController? s_dispatcherQueueController;
    private static readonly object s_lock = new();

    public static WinCompositor Compositor => EnsureCompositor();

    private static WinCompositor EnsureCompositor()
    {
        if (s_compositor == null)
        {
            lock (s_lock)
            {
                if (s_compositor == null)
                {
                    _ = WinDispatcherQueue.GetForCurrentThread()
                        ?? (s_dispatcherQueueController = InitializeCoreDispatcher()).DispatcherQueue;

                    s_compositor = new WinCompositor();
                }
            }
        }

        return s_compositor;
    }

    private static WinDispatcherQueueController InitializeCoreDispatcher()
    {
        var options = new DispatcherQueueOptions
        {
            apartmentType = DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_STA,
            threadType = DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT,
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
        };

        CreateDispatcherQueueController(options, out var raw);
        return WinDispatcherQueueController.FromAbi(raw);
    }

    private enum DISPATCHERQUEUE_THREAD_APARTMENTTYPE
    {
        DQTAT_COM_NONE = 0,
        DQTAT_COM_ASTA = 1,
        DQTAT_COM_STA = 2
    }

    private enum DISPATCHERQUEUE_THREAD_TYPE
    {
        DQTYPE_THREAD_DEDICATED = 1,
        DQTYPE_THREAD_CURRENT = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        [MarshalAs(UnmanagedType.I4)]
        public DISPATCHERQUEUE_THREAD_TYPE threadType;
        [MarshalAs(UnmanagedType.I4)]
        public DISPATCHERQUEUE_THREAD_APARTMENTTYPE apartmentType;
    }

    [DllImport("coremessaging.dll", EntryPoint = "CreateDispatcherQueueController", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr dispatcherQueueController);
}

internal static class TransparentHelper
{
    public static void SetTransparent(Microsoft.UI.Xaml.Window window, bool isTransparent)
    {
        var brushHolder = window.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>();
        if (isTransparent)
        {
            var colorBrush = WindowsCompositionHelper.Compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 255, 255, 255));
            brushHolder.SystemBackdrop = colorBrush;
        }
        else
        {
            brushHolder.SystemBackdrop = null;
        }
    }
}
