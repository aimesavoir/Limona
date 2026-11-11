using System.Runtime.InteropServices;

namespace Limona.Interop;

#pragma warning disable CA1852 // P/Invoke surface: keep explicit internals.

/// <summary>
/// Win32 API surface (P/Invoke) used by the global input hooks, the hidden
/// notification window, the tray icon and a few window-composition helpers.
/// </summary>
internal static class Win32
{
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // ------------------------------------------------------------------
    // Low-level hooks
    // ------------------------------------------------------------------

    public const int WH_MOUSE_LL = 14;
    public const int WH_KEYBOARD_LL = 13;

    public const int VK_CONTROL = 0x11;
    public const int VK_ESCAPE = 0x1B;

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;

    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;

    public const uint LLMHF_INJECTED = 0x0001; // synthesized input (automation tools)
    public const uint LLKHF_INJECTED = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y) { X = x; Y = y; }

        public override string ToString() => $"({X}, {Y})";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint extraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>0x8000 bit set while the physical key is currently held down.</summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static bool IsCtrlDown()
    {
        // GetAsyncKeyState reflects the physical keyboard state at call time,
        // which is what a low-level hook needs (per-thread state would not).
        return ((ushort)GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
    }

    // ------------------------------------------------------------------
    // Hidden message window + tray icon + popup menu
    // ------------------------------------------------------------------

    public const uint WM_USER = 0x0400;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_NULL = 0x0000;
    public const uint WM_QUIT_LOOP = WM_USER + 4;
    public const uint WM_INIT_TRAY = WM_USER + 5;     // deferred tray icon creation

    public const uint WM_HOOK_KBD_ON = WM_USER + 2;   // install keyboard hook
    public const uint WM_HOOK_KBD_OFF = WM_USER + 3;  // remove keyboard hook
    public const uint WM_TRAYICON = WM_USER + 1;      // tray callback message

    public const uint NIM_ADD = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;
    public const uint NIM_SETVERSION = 4;

    public const uint NIF_MESSAGE = 0x0001;
    public const uint NIF_ICON = 0x0002;
    public const uint NIF_TIP = 0x0004;
    public const uint NIF_INFO = 0x0010;
    public const uint NIF_SHOWTIP = 0x0080;

    public const uint NOTIFYICON_VERSION_4 = 4;

    // Matches the exact layout used by System.Windows.Forms.NativeMethods.NOTIFYICONDATA
    // (CharSet.Auto, no Guid/hBalloonIcon, cbSize=952). Using CharSet.Auto and passing by
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW lpData);

    /// <summary>
    /// Registers a unique message ID for a given string name.
    /// Used to receive the "TaskbarCreated" broadcast that Explorer sends
    /// whenever the shell taskbar is (re-)created (e.g. after Explorer restart).
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string lpString);

    /// <summary>
    /// Allows or blocks a specific message from reaching a window when the
    /// process runs at a lower integrity level than the sender (UIPI).
    /// dwAction: 1 = MSGFLT_ALLOW, 2 = MSGFLT_DISALLOW, 3 = MSGFLT_RESET.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint dwAction, IntPtr pChangeFilterStruct);

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
    public const uint COINIT_APARTMENTTHREADED = 0x2;

    // Shell change notification event IDs
    public const int SHCNE_RENAMEITEM = 0x00000001;
    public const int SHCNE_ATTRIBUTES = 0x00000800;
    public const int SHCNE_UPDATEDIR = 0x00001000;
    public const int SHCNE_UPDATEITEM = 0x00002000;
    public const int SHCNE_UPDATEIMAGE = 0x00008000;
    public const int SHCNE_RENAMEFOLDER = 0x00020000;
    public const int SHCNE_ASSOCCHANGED = 0x08000000;

    // Shell change notification flags
    public const uint SHCNF_IDLIST = 0x0000;
    public const uint SHCNF_DWORD = 0x0003;
    public const uint SHCNF_PATHW = 0x0005;
    public const uint SHCNF_FLUSH = 0x1000;
    public const uint SHCNF_FLUSHNOWAIT = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WritePrivateProfileStringW(string? lpAppName, string? lpKeyName, string? lpString, string? lpFileName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, string? byrefItem, IntPtr dwItem2);

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    private static extern void SHChangeNotifyPidl(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    private static extern void SHChangeNotifyDword(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHSimpleIDListFromPath(string path);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shlwapi.dll", EntryPoint = "PathMakeSystemFolderW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PathMakeSystemFolder(string folderPath);

    [DllImport("shlwapi.dll", EntryPoint = "PathUnmakeSystemFolderW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PathUnmakeSystemFolder(string folderPath);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFOLDERCUSTOMSETTINGS
    {
        public uint dwSize;
        public uint dwMask;
        public IntPtr pvid;
        public string? pszWebViewTemplate;
        public uint cchWebViewTemplate;
        public string? pszWebViewTemplateVersion;
        public string? pszInfoTip;
        public uint cchInfoTip;
        public IntPtr pclsid;
        public uint dwFlags;
        public string? pszIconFile;
        public uint cchIconFile;
        public int iIconIndex;
        public string? pszLogo;
        public uint cchLogo;
    }

    public const uint FCSM_ICONFILE = 0x00000010;
    public const uint FCS_FORCEWRITE = 0x00000002;

    /// <summary>
    /// The exact shell32.dll API that the Windows Explorer Properties dialog calls when customizing a folder.
    /// Handles desktop.ini formatting, volume-specific folder attributes, and shell icon cache invalidation natively.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SHGetSetFolderCustomSettings(
        ref SHFOLDERCUSTOMSETTINGS pfcs,
        string pszPath,
        uint dwReadWrite
    );

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    /// <summary>Flushes the Windows kernel in-memory INI cache so GetPrivateProfileString re-reads desktop.ini.</summary>
    public static void FlushIniCache(string iniPath)
    {
        try
        {
            WritePrivateProfileStringW(null, null, null, iniPath);
        }
        catch (Exception ex)
        {
            Dbg.Log("FlushIniCache THREW: " + ex.Message);
        }
    }

    /// <summary>Asks Explorer to refresh a directory (e.g. after an icon change).</summary>
    public static void NotifyShellDirectoryChanged(string directoryPath) =>
        SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, directoryPath, IntPtr.Zero);

    /// <summary>
    /// Forces Explorer to re-read and apply a folder's custom icon IMMEDIATELY:
    /// 1. Flushes kernel INI cache so desktop.ini changes are recognized without latency.
    /// 2. Fires PIDL-based shell notifications (including the self-rename notification to evict cached icon handles).
    /// 3. Flushes the system shell image list (-1).
    /// 4. Broadcasts SHCNE_ASSOCCHANGED to invalidate shell icon caches across all Explorer processes.
    /// 5. Uses COM automation (IShellWindows) to directly trigger window.Refresh() on any open Explorer window.
    /// 6. Refreshes the Windows Desktop view if the target folder resides on the Desktop.
    /// </summary>
    public static void NotifyFolderIconChanged(string folderPath)
    {
        var parent = System.IO.Path.GetDirectoryName(folderPath);

        // 1. Flush kernel INI cache for desktop.ini so GetPrivateProfileString immediately sees changes
        var ini = System.IO.Path.Combine(folderPath, "desktop.ini");
        FlushIniCache(ini);

        // 2. PIDL-based notifications (Explorer views prioritize PIDL updates)
        var folderPidl = IntPtr.Zero;
        var parentPidl = IntPtr.Zero;
        try
        {
            folderPidl = SHSimpleIDListFromPath(folderPath);
            if (!string.IsNullOrEmpty(parent))
                parentPidl = SHSimpleIDListFromPath(parent);

            if (folderPidl != IntPtr.Zero)
            {
                SHChangeNotifyPidl(SHCNE_ATTRIBUTES, SHCNF_IDLIST | SHCNF_FLUSH, folderPidl, IntPtr.Zero);
                SHChangeNotifyPidl(SHCNE_UPDATEITEM, SHCNF_IDLIST | SHCNF_FLUSH, folderPidl, IntPtr.Zero);
                SHChangeNotifyPidl(SHCNE_UPDATEDIR, SHCNF_IDLIST | SHCNF_FLUSH, folderPidl, IntPtr.Zero);
            }

            if (parentPidl != IntPtr.Zero)
            {
                SHChangeNotifyPidl(SHCNE_UPDATEDIR, SHCNF_IDLIST | SHCNF_FLUSH, parentPidl, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            Dbg.Log("NotifyFolderIconChanged PIDL THREW: " + ex.Message);
        }
        finally
        {
            if (folderPidl != IntPtr.Zero) ILFree(folderPidl);
            if (parentPidl != IntPtr.Zero) ILFree(parentPidl);
        }

        // 3. String-path notifications for views listening by path
        try
        {
            SHChangeNotify(SHCNE_ATTRIBUTES, SHCNF_PATHW | SHCNF_FLUSH, folderPath, IntPtr.Zero);
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, folderPath, IntPtr.Zero);
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, folderPath, IntPtr.Zero);
            if (!string.IsNullOrEmpty(parent))
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, parent, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Dbg.Log("NotifyFolderIconChanged PATH THREW: " + ex.Message);
        }

        // 4. System image list reload (-1 forces all shell image lists to reload)
        try
        {
            SHChangeNotifyDword(SHCNE_UPDATEIMAGE, SHCNF_DWORD | SHCNF_FLUSH, IntPtr.Zero, new IntPtr(-1));
        }
        catch { }

        // 5. Invalidate shell icon association cache
        try
        {
            SHChangeNotifyPidl(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }

        // 6. Direct COM automation: Refresh open Explorer windows displaying this folder or its parent
        RefreshOpenExplorerWindows(folderPath);

        // 7. Refresh desktop if the folder is on the Desktop
        RefreshDesktopIfNecessary(folderPath);
    }

    /// <summary>
    /// Enumerates open Windows Explorer windows via Shell.Application COM interface and
    /// calls Refresh() on any window displaying the target folder or its parent folder.
    /// This causes the folder's new icon to update instantaneously right in front of the user.
    /// </summary>
    public static void RefreshOpenExplorerWindows(string folderPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            var parent = System.IO.Path.GetDirectoryName(folderPath);
            var normTarget = System.IO.Path.GetFullPath(folderPath).TrimEnd('\\');
            var normParent = !string.IsNullOrEmpty(parent) ? System.IO.Path.GetFullPath(parent).TrimEnd('\\') : null;

            dynamic windows = shell.Windows();
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic window = windows.Item(i);
                    if (window == null) continue;

                    string? winPath = null;
                    try
                    {
                        string? url = window.LocationURL;
                        if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
                        {
                            winPath = uri.LocalPath;
                        }
                    }
                    catch { }

                    if (string.IsNullOrEmpty(winPath))
                    {
                        try { winPath = window.Document?.Folder?.Self?.Path; } catch { }
                    }

                    if (!string.IsNullOrEmpty(winPath))
                    {
                        winPath = System.IO.Path.GetFullPath(winPath).TrimEnd('\\');
                        if (string.Equals(winPath, normTarget, StringComparison.OrdinalIgnoreCase) ||
                            (normParent != null && string.Equals(winPath, normParent, StringComparison.OrdinalIgnoreCase)))
                        {
                            window.Refresh();
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Dbg.Log("RefreshOpenExplorerWindows THREW: " + ex.Message);
        }
    }

    private static void RefreshDesktopIfNecessary(string folderPath)
    {
        try
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var commonDesktopPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            var parent = System.IO.Path.GetDirectoryName(folderPath);

            bool isOnDesktop = (!string.IsNullOrEmpty(parent) &&
                (string.Equals(parent, desktopPath, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(parent, commonDesktopPath, StringComparison.OrdinalIgnoreCase)));

            if (isOnDesktop)
            {
                UPDATEDirSafe(desktopPath);
                if (!string.IsNullOrEmpty(commonDesktopPath))
                    UPDATEDirSafe(commonDesktopPath);

                var progman = FindWindowW("Progman", null);
                var defView = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView == IntPtr.Zero)
                {
                    var shellWin = GetShellWindow();
                    if (shellWin != IntPtr.Zero)
                        defView = FindWindowExW(shellWin, IntPtr.Zero, "SHELLDLL_DefView", null);
                }

                if (defView == IntPtr.Zero)
                {
                    var workerW = IntPtr.Zero;
                    do
                    {
                        workerW = FindWindowExW(IntPtr.Zero, workerW, "WorkerW", null);
                        if (workerW != IntPtr.Zero)
                        {
                            defView = FindWindowExW(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
                            if (defView != IntPtr.Zero) break;
                        }
                    } while (workerW != IntPtr.Zero);
                }

                if (defView != IntPtr.Zero)
                {
                    PostMessageW(defView, 0x0111 /* WM_COMMAND */, (IntPtr)28931 /* F5/Refresh */, IntPtr.Zero);
                    PostMessageW(defView, 0x0111 /* WM_COMMAND */, (IntPtr)41504 /* Refresh */, IntPtr.Zero);
                }
            }
        }
        catch { }
    }

    private static void UPDATEDirSafe(string path)
    {
        try
        {
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, path, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Dbg.Log("NotifyShell UPDATEDIR THREW: " + ex.Message);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);

    // ------------------------------------------------------------------
    // Dark title bar for helper windows
    // ------------------------------------------------------------------

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    /// <summary>Switches a window's title bar / caption to dark mode (Win10 1809+).</summary>
    public static void EnableDarkTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var on = 1;
        try
        {
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
            }
        }
        catch (Exception ex)
        {
            Dbg.Log("EnableDarkTitleBar THREW: " + ex.Message);
        }
    }

    // Use MAKEINTRESOURCE-style integer resource handles (e.g. IdiApplication).
    public static readonly IntPtr IdiApplication = new(32512);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool MessageBeep(uint uType);

    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x0010;
    public const uint LR_DEFAULTSIZE = 0x0040;
    public const uint LR_SHARED = 0x8000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadImageW(IntPtr hInstance, string lpIconName, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    public const uint MF_STRING = 0x0000;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_POPUP = 0x0010;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_UNCHECKED = 0x0000;
    public const uint MFT_RADIOCHECK = 0x00000200;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;

    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_LEFTBUTTON = 0x0000;
    public const uint TPM_RETURNCMD = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    private const uint FORMAT_MESSAGE_FROM_SYSTEM = 0x00001000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern uint FormatMessageW(uint dwFlags, IntPtr lpSource, uint dwMessageId, uint dwLanguageId,
        char[] lpBuffer, uint nSize, IntPtr arguments);

    public static string GetMessageText(int error)
    {
#pragma warning disable CA2324 // fine for a small local buffer
        var buf = new char[512];
        _ = FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM, IntPtr.Zero, (uint)error, 0, buf, 512, IntPtr.Zero);
#pragma warning restore CA2324
        var text = new string(buf).TrimEnd('\0', '\r', '\n');
        return string.IsNullOrEmpty(text) ? $"Win32 error {error}" : text;
    }

    // ------------------------------------------------------------------
    // Window shape / composition helpers for the radial overlay window
    // ------------------------------------------------------------------

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_VISIBLE = 0x10000000;
    public const long WS_EX_LAYERED = 0x00080000;
    public const uint LWA_ALPHA = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        public int fEnable;
        public IntPtr hRgnBlur;
        public int fTransitionOnMaximized;
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    public static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND pBlurBehind);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint color, byte alpha, uint dwFlags);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    // ------------------------------------------------------------------
    // Z-order enforcement for the overlay window
    // ------------------------------------------------------------------

    public static readonly IntPtr HwndTopmost = new(-1);

    public const uint SWP_NO_SIZE = 0x0001;
    public const uint SWP_NO_MOVE = 0x0002;
    public const uint SWP_NO_ACTIVATE = 0x0010;
    public const uint SWP_SHOW_WINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
