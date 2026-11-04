using System.Runtime.InteropServices;

namespace Limona.Interop;

#pragma warning disable CA1821 // Win32 conventions: keep delegates/comparisons explicit.

/// <summary>
/// Owns the dedicated input thread:
///  - installs the global WH_MOUSE_LL hook (alive for the app lifetime, minimal work per event),
///  - installs/uninstalls a WH_KEYBOARD_LL hook only while the menu is open (to catch Escape),
///  - runs a lightweight Win32 message loop that pumps hook callbacks and hosts the tray icon.
/// Hook callbacks do a few integer comparisons and (when the shortcut fires) one
/// DispatcherQueue.TryEnqueue. No rendering or file operations happen on this thread.
/// </summary>
internal sealed class HookService : IDisposable
{
    private const string CLASS_NAME = "Limona_MsgWnd";
    private const string TIP_TEXT = "Limona V1.0  ·  Ctrl + Right-click";

    private const nuint TRAYCMD_SETTINGS = 101;
    private const nuint TRAYCMD_HELP = 103;
    private const nuint TRAYCMD_EXIT = 102;
    private const nuint TRAYCMD_LANG_EN = 110;
    private const nuint TRAYCMD_LANG_FR = 111;
    private const nuint TRAYCMD_LANG_AR = 112;
    private const nuint TRAYCMD_LANG_ES = 113;

    private const int NIN_SELECT = 0x0400; // WM_CONTEXTMENU for right, NIN_SELECT for left click (v4)
    private static readonly IntPtr HwndMessage = new(-3); // HWND_MESSAGE: message-only parent

    // "TaskbarCreated" is a globally-registered message that Explorer broadcasts to all
    // top-level windows whenever it (re-)creates the taskbar / system tray.  We use it
    // to re-register our tray icon after an Explorer restart or shell crash.
    private static readonly uint WM_TASKBARCREATED =
        Win32.RegisterWindowMessageW("TaskbarCreated");

    // WM_USER+10: posted to ourselves to schedule a deferred NIM_ADD retry.
    private const uint WM_TRAY_RETRY = Win32.WM_USER + 10;
    private int _trayRetryCount; // how many retries have been attempted so far
    private const int MaxTrayRetries = 10; // give up after ~10 s

    private static HookService? s_instance; // routes the static native window procedure back to this instance

    // Events are raised on the hook thread; App marshals them onto the UI thread.
    public event Action<Win32.POINT>? OpenRequested;   // Ctrl+RMB, or tray "show at cursor"
    public event Action? DismissRequested;            // out-of-bounds click / plain right-click while open
    public event Action? EscapeRequested;             // Escape while the overlay is open
    public event Action? SettingsRequested;           // tray "Settings"
    public event Action? HelpRequested;               // tray "Help"
    public event Action<Limona.Services.AppLanguage>? LanguageRequested; // tray "Language → …"
    public event Action? ExitRequested;               // tray "Exit"

    // Delegate references must stay alive for the process lifetime
    // (the GC would otherwise release the native function pointers).
    private readonly Win32.LowLevelMouseProc _mouseProcKeepAlive;
    private readonly Win32.LowLevelKeyboardProc _keyboardProcKeepAlive;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, nuint wParam, nint lParam);
    private readonly WndProcDelegate _wndProcKeepAlive;

    private Thread _thread = null!;
    private readonly ManualResetEventSlim _threadReady = new(false);
    private readonly ManualResetEventSlim _threadExited = new(false);

    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private IntPtr _hWnd;
    private bool _swallowNextRButtonUp;  // swallow the Up half of the Ctrl+RMB gesture pair
    private volatile bool _menuOpen;     // hook-side mirror of the overlay visibility
    private readonly object _boundsLock = new();
    private (int left, int top, int right, int bottom) _menuBounds; // physical px, virtual-desktop space
    private Win32.NOTIFYICONDATAW? _trayIconData; // null = no icon present
    private IntPtr _trayIcon = IntPtr.Zero;       // HICON loaded for the tray (lifetime owned here)

    private bool _disposed;

    public HookService()
    {
        _mouseProcKeepAlive = MouseProc;
        _keyboardProcKeepAlive = KeyboardProc;
        _wndProcKeepAlive = WndProc;
        s_instance = this;
    }

    public void Start()
    {
        _thread = new Thread(ThreadLoop)
        {
            Name = "LimonaHooks",
            IsBackground = false, // keep the process alive while the hook thread runs
        };
        _thread.SetApartmentState(ApartmentState.STA); // popup menus require an STA thread
        _thread.Start();
        if (!_threadReady.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("The input thread did not initialize in time.");
        }
    }

    // ------------------------------------------------------------------
    // Called by the UI layer
    // ------------------------------------------------------------------

    /// <summary>UI layer signals that the overlay window is about to be shown.</summary>
    public void NotifyMenuOpened()
    {
        _menuOpen = true;
        if (_hWnd != IntPtr.Zero)
            _ = Win32.PostMessageW(_hWnd, Win32.WM_HOOK_KBD_ON, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>UI layer signals that the overlay window has finished hiding.</summary>
    public void NotifyMenuClosed()
    {
        _menuOpen = false;
        if (_hWnd != IntPtr.Zero)
            _ = Win32.PostMessageW(_hWnd, Win32.WM_HOOK_KBD_OFF, IntPtr.Zero, IntPtr.Zero);
    }

    public void UpdateMenuBounds(int x, int y, int width, int height)
    {
        lock (_boundsLock)
        {
            _menuBounds = (x, y, x + width, y + height);
        }
    }

    private bool IsInsideMenu(Win32.POINT p)
    {
        lock (_boundsLock)
        {
            return p.X >= _menuBounds.left && p.X <= _menuBounds.right && p.Y >= _menuBounds.top && p.Y <= _menuBounds.bottom;
        }
    }

    // ------------------------------------------------------------------
    // Hook callbacks (execute on the hook thread)
    // ------------------------------------------------------------------

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & Win32.LLMHF_INJECTED) == 0) // ignore synthesized input (automation tools)
            {
                switch ((int)wParam)
                {
                    case Win32.WM_RBUTTONDOWN:
                        if (Win32.IsCtrlDown())
                        {
                            // This press is the global shortcut. Consume the gesture so the
                            // native context menu does not appear over the Limona.
                            _swallowNextRButtonUp = true;
                            if (!_menuOpen)
                            {
                                _menuOpen = true;
                                OpenRequested?.Invoke(info.pt);
                            }
                            return 1; // swallow
                        }

                        if (_menuOpen)
                        {
                            // Plain right-click while the menu is open: dismiss the overlay
                            // and let the click pass through to the window underneath
                            // (that window's native context menu will appear normally).
                            _menuOpen = false;
                            PostKbdOff();
                            DismissRequested?.Invoke();
                        }
                        break;

                    case Win32.WM_RBUTTONUP:
                        if (_swallowNextRButtonUp)
                        {
                            _swallowNextRButtonUp = false;
                            return 1; // swallow the matching Up of the shortcut gesture
                        }
                        break;

                    case Win32.WM_LBUTTONDOWN:
                        if (_menuOpen && !IsInsideMenu(info.pt))
                        {
                            // Primary click outside the overlay rectangle: dismiss and
                            // let the click behave normally in the window below.
                            _menuOpen = false;
                            PostKbdOff();
                            DismissRequested?.Invoke();
                        }
                        break;
                }
            }
        }

        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _menuOpen)
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & Win32.LLKHF_INJECTED) == 0)
            {
                int msg = (int)wParam;
                if (info.vkCode == Win32.VK_ESCAPE && msg == Win32.WM_KEYDOWN)
                {
                    _menuOpen = false;
                    PostKbdOff();      // remove this hook; the Up event passes through instead
                    EscapeRequested?.Invoke();
                    return 1;          // swallow Escape while the overlay is open
                }
                if (info.vkCode == Win32.VK_ESCAPE && msg == Win32.WM_KEYUP)
                {
                    return 1; // swallow the matching Up
                }
            }
        }

        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    // ------------------------------------------------------------------
    // Thread body: message-only window, tray icon, hooks, message pump
    // ------------------------------------------------------------------

    private void ThreadLoop()
    {
        try
        {
            Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_APARTMENTTHREADED);
            _hWnd = CreateMessageWindow();

            InstallMouseHook();

            _threadReady.Set();

            // Post a deferred init message so AddTrayIcon runs inside the
            // running message pump (Shell_NotifyIconW needs the STA pump active).
            _ = Win32.PostMessageW(_hWnd, Win32.WM_INIT_TRAY, IntPtr.Zero, IntPtr.Zero);

            while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                _ = Win32.TranslateMessage(ref msg);
                _ = Win32.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            Limona.Interop.Dbg.Log("HookService ThreadLoop THREW: " + ex.ToString());
            _threadReady.Set();
        }
        finally
        {
            RemoveKeyboardHookInternal();
            if (_mouseHook != IntPtr.Zero)
            {
                _ = Win32.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            RemoveTrayIcon();
            if (_hWnd != IntPtr.Zero) _ = Win32.DestroyWindow(_hWnd);
            _threadExited.Set();
        }
    }

    private IntPtr CreateMessageWindow()
    {
        var instance = Win32.GetModuleHandleW(null);
        var cls = new Win32.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            hInstance = instance,
            lpszClassName = CLASS_NAME,
        };
        if (Win32.RegisterClassExW(ref cls) == 0)
        {
            var err = Marshal.GetLastWin32Error();
            if (err != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
            {
                Limona.Interop.Dbg.Log("RegisterClassExW warning: " + Win32.GetMessageText(err));
            }
        }

        // Use a regular hidden WS_POPUP window because Shell_NotifyIconW
        // requires a desktop window (HWND_MESSAGE returns E_FAIL).
        return Win32.CreateWindowExW(0, CLASS_NAME, "LimonaTray", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, IntPtr.Zero,
            IntPtr.Zero, instance, IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_TRAYICON:
                HandleTrayMessage(wParam, lParam);
                break;

            case Win32.WM_COMMAND:
                switch (LOWORD(wParam))
                {
                    case TRAYCMD_SETTINGS:
                        SettingsRequested?.Invoke();
                        break;

                    case TRAYCMD_HELP:
                        HelpRequested?.Invoke();
                        break;

                    case TRAYCMD_EXIT:
                        RemoveTrayIcon();
                        ExitRequested?.Invoke();
                        break;

                    case TRAYCMD_LANG_EN:
                        LanguageRequested?.Invoke(Limona.Services.AppLanguage.English);
                        break;
                    case TRAYCMD_LANG_FR:
                        LanguageRequested?.Invoke(Limona.Services.AppLanguage.French);
                        break;
                    case TRAYCMD_LANG_AR:
                        LanguageRequested?.Invoke(Limona.Services.AppLanguage.Arabic);
                        break;
                    case TRAYCMD_LANG_ES:
                        LanguageRequested?.Invoke(Limona.Services.AppLanguage.Spanish);
                        break;
                }
                break;

            case Win32.WM_HOOK_KBD_ON:
                InstallKeyboardHook();
                break;

            case Win32.WM_HOOK_KBD_OFF:
                RemoveKeyboardHookInternal();
                break;

            case Win32.WM_INIT_TRAY:
                _trayRetryCount = 0;
                AddTrayIcon();
                // If the first attempt failed, schedule a retry
                if (_trayIconData is null)
                    ScheduleTrayRetry(hWnd);
                break;

            case WM_TRAY_RETRY:
                // Deferred retry when NIM_ADD failed at startup (Explorer not ready yet)
                if (_trayIconData is null && _trayRetryCount < MaxTrayRetries)
                {
                    _trayRetryCount++;
                    Dbg.Log($"WM_TRAY_RETRY attempt {_trayRetryCount}/{MaxTrayRetries}");
                    AddTrayIcon();
                    if (_trayIconData is null)
                        ScheduleTrayRetry(hWnd);
                }
                break;

            case Win32.WM_QUIT_LOOP:
                Win32.PostQuitMessage(0);
                break;

            default:
                // Re-register the tray icon whenever Explorer (re-)creates the taskbar.
                if (msg == WM_TASKBARCREATED)
                {
                    Dbg.Log("WM_TASKBARCREATED received — re-registering tray icon");
                    if (_trayIconData is { } old)
                    {
                        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref old);
                        _trayIconData = null;
                    }
                    _trayRetryCount = 0;
                    AddTrayIcon();
                }
                break;
        }

        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void HandleTrayMessage(nuint wParam, nint lParam)
    {
        // For Shell_NotifyIcon with NOTIFYICON_VERSION_4:
        // LOWORD(lParam) contains the notification event (WM_CONTEXTMENU, WM_RBUTTONUP, NIN_SELECT, WM_LBUTTONUP, etc.).
        var evt = (uint)(lParam.ToInt64() & 0xFFFF);

        if (evt == Win32.WM_CONTEXTMENU || evt == (uint)Win32.WM_RBUTTONUP)
        {
            _ = Win32.GetCursorPos(out var cursor);
            ShowTrayMenu(cursor);
        }
        else if (evt == (uint)NIN_SELECT || evt == (uint)Win32.WM_LBUTTONUP || evt == 0x0401 /* NIN_KEYSELECT */)
        {
            _ = Win32.GetCursorPos(out var cursor);
            _menuOpen = true;
            PostKbdOn();
            OpenRequested?.Invoke(cursor);
        }
    }

    private void ShowTrayMenu(Win32.POINT at)
    {
        // Popup menus require our window to be foreground so TrackPopupMenu
        // dismisses correctly when the user clicks elsewhere.
        _ = Win32.SetForegroundWindow(_hWnd);

        var menu = Win32.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            _ = Win32.AppendMenuW(menu, Win32.MF_GRAYED, 0, "Limona V1.0");
            _ = Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, null);
            _ = Win32.AppendMenuW(menu, Win32.MF_STRING, TRAYCMD_SETTINGS, Limona.Services.Loc.T("Settings"));
            _ = Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, null);

            // Language ▸ English / Français / العربية / Español
            // A real Win32 submenu: hovering "Language" expands it, no window.
            var langMenu = Win32.CreatePopupMenu();
            if (langMenu != IntPtr.Zero)
            {
                AppendLanguageItem(langMenu, TRAYCMD_LANG_EN, Limona.Services.AppLanguage.English);
                AppendLanguageItem(langMenu, TRAYCMD_LANG_FR, Limona.Services.AppLanguage.French);
                AppendLanguageItem(langMenu, TRAYCMD_LANG_AR, Limona.Services.AppLanguage.Arabic);
                AppendLanguageItem(langMenu, TRAYCMD_LANG_ES, Limona.Services.AppLanguage.Spanish);
                _ = Win32.AppendMenuW(menu, Win32.MF_POPUP, (nuint)langMenu,
                    Limona.Services.Loc.T("Language"));
            }

            _ = Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, null);
            _ = Win32.AppendMenuW(menu, Win32.MF_STRING, TRAYCMD_HELP, Limona.Services.Loc.T("Help"));
            _ = Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, null);
            _ = Win32.AppendMenuW(menu, Win32.MF_STRING, TRAYCMD_EXIT, Limona.Services.Loc.T("Exit"));

            int cmd = Win32.TrackPopupMenuEx(
                menu,
                Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD,
                at.X,
                at.Y,
                _hWnd,
                IntPtr.Zero);

            if (cmd == (int)TRAYCMD_EXIT)
            {
                Limona.Interop.Dbg.Log("Tray menu: Exit clicked");
                RemoveTrayIcon();
                ExitRequested?.Invoke();
            }
            else if (cmd == (int)TRAYCMD_SETTINGS)
            {
                Limona.Interop.Dbg.Log("Tray menu: Settings clicked");
                SettingsRequested?.Invoke();
            }
            else if (cmd == (int)TRAYCMD_HELP)
            {
                Limona.Interop.Dbg.Log("Tray menu: Help clicked");
                HelpRequested?.Invoke();
            }
            else if (cmd is >= (int)TRAYCMD_LANG_EN and <= (int)TRAYCMD_LANG_ES)
            {
                var language = cmd switch
                {
                    (int)TRAYCMD_LANG_EN => Limona.Services.AppLanguage.English,
                    (int)TRAYCMD_LANG_FR => Limona.Services.AppLanguage.French,
                    (int)TRAYCMD_LANG_AR => Limona.Services.AppLanguage.Arabic,
                    _ => Limona.Services.AppLanguage.Spanish,
                };
                Limona.Interop.Dbg.Log("Tray menu: language " + language);
                LanguageRequested?.Invoke(language);
            }
        }
        finally
        {
            _ = Win32.DestroyMenu(menu);
            _ = Win32.PostMessageW(_hWnd, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero); // dismiss-on-click-away quirk
        }
    }

    /// <summary>One radio-checked entry of the tray "Language" submenu.</summary>
    private static void AppendLanguageItem(IntPtr menu, nuint command, Limona.Services.AppLanguage language)
    {
        var flags = Win32.MF_STRING | Win32.MFT_RADIOCHECK;
        if (Limona.Services.Loc.Current == language) flags |= Win32.MF_CHECKED;
        _ = Win32.AppendMenuW(menu, flags, command, Limona.Services.Loc.NativeName(language));
    }

    private void InstallMouseHook()
    {
        var instance = Win32.GetModuleHandleW(null);
        _mouseHook = Win32.SetWindowsHookExW(Win32.WH_MOUSE_LL, _mouseProcKeepAlive, instance, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            ShowBalloon("Mouse hook failed",
                $"Global mouse hook could not be installed: {Win32.GetMessageText(err)}");
        }
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        var instance = Win32.GetModuleHandleW(null);
        _keyboardHook = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, _keyboardProcKeepAlive, instance, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            _keyboardHook = IntPtr.Zero; // non-fatal: dismissal still works via outside-click
        }
    }

    private void RemoveKeyboardHookInternal()
    {
        var hook = _keyboardHook;
        _keyboardHook = IntPtr.Zero;
        if (hook != IntPtr.Zero) _ = Win32.UnhookWindowsHookEx(hook);
    }

    private void PostKbdOn()
    {
        if (_hWnd != IntPtr.Zero)
            _ = Win32.PostMessageW(_hWnd, Win32.WM_HOOK_KBD_ON, IntPtr.Zero, IntPtr.Zero);
    }

    private void PostKbdOff()
    {
        if (_hWnd != IntPtr.Zero)
            _ = Win32.PostMessageW(_hWnd, Win32.WM_HOOK_KBD_OFF, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Posts a WM_TRAY_RETRY message back to ourselves after a 1-second delay
    /// using a thread-pool timer so the message pump stays responsive.
    /// </summary>
    private void ScheduleTrayRetry(IntPtr hWnd)
    {
        var hwndCapture = hWnd; // capture for lambda
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            System.Threading.Thread.Sleep(1000);
            if (!_disposed && hwndCapture != IntPtr.Zero)
                _ = Win32.PostMessageW(hwndCapture, WM_TRAY_RETRY, IntPtr.Zero, IntPtr.Zero);
        });
    }



    private void AddTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            _trayIcon = File.Exists(iconPath)
                ? Win32.LoadImageW(IntPtr.Zero, iconPath, Win32.IMAGE_ICON, 0, 0, Win32.LR_LOADFROMFILE | Win32.LR_DEFAULTSIZE)
                : IntPtr.Zero;
            if (_trayIcon == IntPtr.Zero)
            {
                // System default application icon ("IDI_APPLICATION").
                _trayIcon = Win32.LoadIconW(IntPtr.Zero, Win32.IdiApplication);
            }
            if (_trayIcon == IntPtr.Zero)
            {
                Dbg.Log("AddTrayIcon: no icon available");
            }

            var data = new Win32.NOTIFYICONDATAW
            {
                hWnd = _hWnd,
                uID = 1,
                uFlags = (int)(Win32.NIF_MESSAGE | Win32.NIF_TIP),
                uCallbackMessage = (int)Win32.WM_TRAYICON,
                szTip = TIP_TEXT,
            };
            if (_trayIcon != IntPtr.Zero)
            {
                data.hIcon = _trayIcon;
                data.uFlags |= (int)Win32.NIF_ICON;
            }

            _trayIconData = data;
            var added = TryAddNotifyIcon(Win32.NIM_ADD);
            Dbg.Log($"Tray icon added: {added}");

            // Version 4: LOWORD(lParam) of the callback message carries the
            // notification event (WM_CONTEXTMENU right-click, NIN_SELECT left-click).
            if (added)
            {
                var versionData = _trayIconData!.Value;
                versionData.uVersion = (int)Win32.NOTIFYICON_VERSION_4;
                var versionSet = TryAddNotifyIcon(Win32.NIM_SETVERSION);
                Dbg.Log($"Tray icon version4: {versionSet}");
            }
        }
        catch (Exception ex)
        {
            Dbg.Log("AddTrayIcon failed: " + ex);
        }
    }

    /// <summary>
    /// Shell_NotifyIconW with a defensive cbSize retry: different Windows
    /// versions/marshalling builds accept different struct size variants.
    /// </summary>
    private bool TryAddNotifyIcon(uint message)
    {
        if (_trayIconData is not { } baseData) return false;
        foreach (var size in new[] { Marshal.SizeOf<Win32.NOTIFYICONDATAW>(), 952, 936, 928, 924, 504 })
        {
            var data = baseData;
            data.cbSize = size;
            var ok = Win32.Shell_NotifyIconW(message, ref data);
            var err = ok ? 0 : Marshal.GetLastWin32Error();
            Dbg.Log($"NIM_{message} size={size} res=0x{(ok ? 0 : 1):X} err={err}");
            if (ok)
            {
                _trayIconData = data;
                return true;
            }
        }
        _trayIconData = null;
        return false;
    }

    private void RemoveTrayIcon()
    {
        try
        {
            if (_trayIconData is { } data)
            {
                var ok = Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
                Dbg.Log($"Tray icon removed: {ok}");
                _trayIconData = null;
            }
            if (_trayIcon != IntPtr.Zero)
            {
                _ = Win32.DestroyIcon(_trayIcon);
                _trayIcon = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Dbg.Log("RemoveTrayIcon error: " + ex);
        }
    }

    private void ShowBalloon(string title, string info)
    {
        try
        {
            if (_trayIconData is not { } data) return;
            data.szInfo = info;
            data.szInfoTitle = title;
            data.dwInfoFlags = 0x00000001 /* NIIF_INFO */;
            var ok = Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
            Dbg.Log($"ShowBalloon res={ok}");
        }
        catch (Exception ex)
        {
            Dbg.Log("ShowBalloon error: " + ex);
        }
    }

    private static nuint LOWORD(nuint value) => value & 0xFFFF;

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RemoveTrayIcon();

        if (_hWnd != IntPtr.Zero)
            _ = Win32.PostMessageW(_hWnd, Win32.WM_QUIT_LOOP, IntPtr.Zero, IntPtr.Zero);

        _threadExited.Wait(TimeSpan.FromSeconds(3));
    }
}
