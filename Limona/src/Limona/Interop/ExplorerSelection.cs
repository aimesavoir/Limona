using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace Limona.Interop;

/// <summary>
/// Reads the file/item under the cursor in Explorer windows or on the desktop.
/// Uses native UI Automation (in-process COM) for instant hover detection (<1ms),
/// falling back to MSAA and Shell Automation selection.
/// </summary>
public static class ExplorerSelection
{
    private const uint GA_ROOT = 2;

    public readonly record struct FileTarget(string DisplayName, string? FullPath)
    {
        public bool HasValidFile => !string.IsNullOrWhiteSpace(FullPath) && File.Exists(FullPath);
    }

    /// <summary>Returns the display name and resolved full path of the item under cursor.</summary>
    public static FileTarget GetTargetUnderCursor(int x, int y)
    {
        try
        {
            var cursorWnd = WindowFromPoint(new POINT(x, y));
            if (cursorWnd == IntPtr.Zero) return new FileTarget("no window at cursor", null);
            cursorWnd = GetAncestor(cursorWnd, GA_ROOT);
            var cursorClass = GetClassName(cursorWnd);
            var isDesktopClass = cursorClass is "Progman" or "WorkerW" or "SHELLDLL_DefView";

            // 1) The item DIRECTLY under the cursor always wins over any existing
            //    Explorer selection. This is what guarantees the user acts on the
            //    file they point at, not on a file that was selected earlier.
            var name = TryUiaPointName(x, y);

            // Direct hit via MSAA as secondary check.
            if (string.IsNullOrWhiteSpace(name) || IsNonFileHost(name))
            {
                var msaaResult = ReadMsaaHit(x, y);
                if (msaaResult is FileHit hit && !IsNonFileHost(hit.Name))
                {
                    name = hit.Name;
                }
            }

            if (!string.IsNullOrWhiteSpace(name) && !IsNonFileHost(name))
            {
                var fullPath = TryResolveFullPath(name, cursorWnd, isDesktopClass, x, y);

                // Only when the name cannot be resolved to a path do we consult
                // the shell selection — and then only if it is that same item.
                if (fullPath is null && !isDesktopClass)
                {
                    var sel = ReadExplorerSelection(cursorWnd, cursorClass, x, y);
                    if (sel is not null && SameItem(sel.Value, name))
                        fullPath = sel.Value.Path;
                }

                Dbg.Log($"GetTargetUnderCursor (under cursor): name='{name}' path='{fullPath}'");
                return new FileTarget(name, fullPath);
            }

            // 2) Nothing under the cursor: the target becomes the folder shown
            //    in that Explorer window (folder actions), never a file that
            //    merely happens to be selected.
            if (!isDesktopClass)
            {
                var folder = TryGetExplorerFolder(cursorWnd);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
                    Dbg.Log($"GetTargetUnderCursor: empty area → folder '{folder}'");
                    return new FileTarget(string.IsNullOrEmpty(folderName) ? folder : folderName, folder);
                }
            }

            return new FileTarget("no file selected", null);
        }
        catch (Exception ex)
        {
            Dbg.Log("ExplorerSelection THREW: " + ex.Message);
            return new FileTarget("selection error", null);
        }
    }

    /// <summary>True when the shell selection refers to the item under the cursor.</summary>
    private static bool SameItem(SelectionInfo selection, string name)
    {
        if (string.Equals(selection.Text, name, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return string.Equals(Path.GetFileName(selection.Path), name, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Folder shown by the Explorer window under the cursor.</summary>
    private static string? TryGetExplorerFolder(IntPtr cursorWnd)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic window in shell.Windows())
            {
                var hwnd = (IntPtr)(int)window.HWND;
                if (hwnd != cursorWnd) continue;
                string folder = window.Document.Folder.Self.Path;
                return string.IsNullOrWhiteSpace(folder) ? null : folder;
            }
        }
        catch (Exception ex)
        {
            Dbg.Log("TryGetExplorerFolder THREW: " + ex.Message);
        }
        return null;
    }

    /// <summary>Synchronous entry point used when a bounded fetch is fine.</summary>
    public static string GetUnderCursor(int x, int y)
    {
        var target = GetTargetUnderCursor(x, y);
        return target.DisplayName;
    }

    private static string? TryResolveFullPath(string name, IntPtr cursorWnd, bool isDesktop, int x, int y)
    {
        try
        {
            if (Path.IsPathRooted(name) && File.Exists(name)) return name;

            if (isDesktop)
            {
                var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                var path = MatchFileInDirectory(userDesktop, name) ?? MatchFileInDirectory(commonDesktop, name);
                if (path != null) return path;
            }
            else
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType)!;
                    foreach (dynamic window in shell.Windows())
                    {
                        var hwnd = (IntPtr)(int)window.HWND;
                        if (hwnd == cursorWnd)
                        {
                            string folder = window.Document.Folder.Self.Path;
                            var path = MatchFileInDirectory(folder, name);
                            if (path != null) return path;
                        }
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private static string? MatchFileInDirectory(string dir, string name)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;

            var direct = Path.Combine(dir, name);
            if (File.Exists(direct) || Directory.Exists(direct)) return direct;

            var matches = Directory.GetFiles(dir, name + ".*");
            if (matches.Length > 0) return matches[0];
        }
        catch { }

        return null;
    }

    // ------------------------------------------------------------------
    // Under-cursor reading via MSAA hit test.
    // ------------------------------------------------------------------

    private readonly record struct FileHit(string Name, string Role);

    private readonly record struct SelectionInfo(string Text, string Path);

    /// <summary>
    /// MSAA hit test at a screen point. Returns a FileHit only when the
    /// element under the point behaves like a shell item container (its role
    /// is the classic SysListView32/DefView list role) — a filter that
    /// excludes column headers ("Nom") and checkboxes.
    /// </summary>
    private static FileHit? ReadMsaaHit(int x, int y)
    {
        try
        {
            var hr = AccessibleObjectFromPoint(
                new POINT(x, y),
                out var accObject,
                out var childVariant);

            if (hr != 0)
            {
                Dbg.Log($"MSAA: hit-test failed hr=0x{hr:X}");
                return null;
            }

            var accessible = (IAccessible)accObject;
            var name = GetAccessibleName(accessible, childVariant, out var roleText);
            Dbg.Log($"MSAA hit: name='{name}' role='{roleText}'");

            if (string.IsNullOrWhiteSpace(name)) return null;

            return roleText switch
            {
                "page tab list" => new FileHit(name!, roleText),
                "list item" => new FileHit(name!, roleText),
                "list" => new FileHit(name!, roleText),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Dbg.Log("MSAA THREW: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reads the selection of the Explorer shell window under the given point
    /// via shell automation COM dispatch. Returns null when no shell window
    /// matches the cursor or when nothing is selected.
    /// </summary>
    private static SelectionInfo? ReadExplorerSelection(IntPtr cursorWnd, string cursorClass, int x, int y)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                Dbg.Log("ExplorerSelection: no Shell.Application progID");
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic windows = shell.Windows();

            // Pass 1: exact top-level hwnd at the cursor.
            // Pass 2: window-rect containment fallback (smallest window wins;
            // minimized windows report (-32000,-32000) and are skipped).
            object? match = null;
            foreach (dynamic window in windows)
            {
                var hwnd = (IntPtr)(int)window.HWND;
                if (hwnd != IntPtr.Zero && hwnd == cursorWnd)
                {
                    match = window;
                    break;
                }
            }

            object? smallestWindow = null;
            long smallestArea = long.MaxValue;
            foreach (dynamic window in windows)
            {
                var hwnd = (IntPtr)(int)window.HWND;
                if (!GetWindowRect(hwnd, out var rect)) continue;
                if (rect.X <= -30000 || rect.Y <= -30000) continue;
                var right = rect.X + rect.Width;
                var bottom = rect.Y + rect.Height;
                if (x < rect.X || y < rect.Y || x >= right || y >= bottom) continue;
                var area = (long)rect.Width * rect.Height;
                if (area < smallestArea)
                {
                    smallestArea = area;
                    smallestWindow = window;
                }
            }
            match ??= smallestWindow;
            if (match is not null) Dbg.Log("ExplorerSelection: shell window matched");

            if (match is null)
            {
                Dbg.Log($"ExplorerSelection: no shell window matched cursor ({x},{y}) class={cursorClass}");
                return null;
            }

            dynamic document = ((dynamic)match).Document;
            dynamic selected = document.SelectedItems();
            var count = (int)selected.Count;

            if (count == 0) return null;

            if (count == 1)
            {
                string fileName = selected.Item(0).Name;
                string filePath = "(path unreadable)";
                try { filePath = selected.Item(0).Path; } catch { }
                Dbg.Log($"Explorer selection: name={fileName} path={filePath}");
                return new SelectionInfo(fileName, filePath);
            }

            dynamic first = selected.Item(0);
            string firstPath = "(path unreadable)";
            try { firstPath = first.Path; } catch { }
            Dbg.Log($"Explorer selection: {count} items (first path={firstPath})");
            return new SelectionInfo($"{count} items", firstPath);
        }
        catch (Exception ex)
        {
            Dbg.Log("ExplorerSelection THREW: " + ex.Message);
            return null;
        }
    }

    public static IntPtr FindDesktopViewHwnd(IntPtr cursorTop)
    {
        if (cursorTop != IntPtr.Zero)
        {
            var direct = FindWindowExW(cursorTop, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (direct != IntPtr.Zero) return direct;

            var child = FindWindowExW(cursorTop, IntPtr.Zero, null, null);
            var level = 0;
            while (child != IntPtr.Zero && level < 20)
            {
                var defView = FindWindowExW(child, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero) return defView;
                child = FindWindowExW(cursorTop, child, null, null);
                level++;
            }
        }

        var progman = FindWindowW("Progman", null);
        var view = progman != IntPtr.Zero ? FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null) : IntPtr.Zero;
        if (view != IntPtr.Zero) return view;

        var previous = progman;
        var worker = FindWindowExW(IntPtr.Zero, progman, "WorkerW", null);
        while (worker != IntPtr.Zero)
        {
            var candidate = FindWindowExW(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (candidate != IntPtr.Zero) return candidate;
            previous = worker;
            worker = FindWindowExW(IntPtr.Zero, previous, "WorkerW", null);
        }
        return IntPtr.Zero;
    }

    private static string GetAccessibleName(IAccessible accessible, object childVariant, out string roleText)
    {
        roleText = string.Empty;
        try
        {
            var roleVar = accessible.get_accRole(childVariant);
            roleText = roleVar switch
            {
                int r => RoleText(r),
                _ => roleVar as string ?? string.Empty,
            };
        }
        catch { }

        try
        {
            var name = accessible.get_accName(childVariant);
            if (!string.IsNullOrWhiteSpace(name)) return name!;
        }
        catch { }

        // The hit-test may land on a sub-object (e.g. icon text) whose parent
        // carries the display name.
        try
        {
            var name = accessible.get_accName(0);
            if (!string.IsNullOrWhiteSpace(name)) return name!;
        }
        catch { }

        return string.Empty;
    }

    private static string RoleText(int roleValue)
    {
        try
        {
            var roleString = Roles.Name(roleValue);
            return roleString ?? ("role#" + roleValue);
        }
        catch
        {
            return "role#" + roleValue;
        }
    }

    // Reserved for later phases: role-name mapping for MSAA role ids.
    private static class Roles
    {
        public static string? Name(int roleId) => roleId switch
        {
            9 => "window",
            10 => "client",
            11 => "menu popup",
            12 => "menu item",
            21 => "graphic",
            22 => "text",
            23 => "list",
            24 => "list item",
            33 => "page tab",
            34 => "page tab list",
            37 => "pane",
            38 => "progress bar",
            43 => "status bar",
            _ => null,
        };
    }

    /// <summary>
    /// UIA FromPoint read of the item under a screen point via a hidden
    /// PowerShell helper. Works for desktop icons AND modern Explorer rows
    /// (the native accessibility tree, immune to the MSAA quirks). Returns
    private static IUIAutomation? s_automation;

    private static IUIAutomation GetAutomation()
    {
        return s_automation ??= new CUIAutomation();
    }

    /// <summary>
    /// Native in-process UI Automation FromPoint read (< 1 ms latency).
    /// Detects the file or folder directly under the cursor without requiring a click or selection.
    /// Works for desktop icons, Windows 10 Explorer, and modern Windows 11 Explorer with tabs.
    /// </summary>
    private static string? TryUiaPointName(int x, int y)
    {
        try
        {
            var uia = GetAutomation();
            tagPOINT pt = new() { x = x, y = y };
            var elem = uia.ElementFromPoint(pt);
            if (elem is null) return null;

            var walker = uia.ControlViewWalker;
            var cur = elem;
            string? fallbackCandidate = null;

            for (int hops = 0; hops <= 8 && cur != null; hops++)
            {
                int controlType = 0;
                string name = string.Empty;
                string className = string.Empty;

                try { controlType = cur.CurrentControlType; } catch { }
                try { name = cur.CurrentName ?? string.Empty; } catch { }
                try { className = cur.CurrentClassName ?? string.Empty; } catch { }

                Dbg.Log($"UIA hop {hops}: type={controlType} cls=[{className}] name=[{name}]");

                // Stop if we hit a top-level window or host container
                if (controlType == 50032 /* Window */ ||
                    (controlType == 50033 /* Pane */ && (className is "CabinetWClass" or "ShellTabWindowClass")))
                {
                    break;
                }

                // If this element is a list item / data row (file or folder item in the list)
                if (controlType is 50007 /* ListItem */ or 50001 /* DataItem */ or 50024 /* TreeItem */)
                {
                    if (!string.IsNullOrWhiteSpace(name) && !IsNonFileHost(name))
                    {
                        return name.Trim();
                    }
                }

                // Fallback candidate if user hovered over text or icon inside the item row
                if (fallbackCandidate is null &&
                    !string.IsNullOrWhiteSpace(name) &&
                    !IsNonFileHost(name) &&
                    controlType is 50020 /* Text */ or 50006 /* Image */ or 50025 /* Custom */)
                {
                    fallbackCandidate = name.Trim();
                }

                try
                {
                    cur = walker.GetParentElement(cur);
                }
                catch
                {
                    break;
                }
            }

            return fallbackCandidate;
        }
        catch (Exception ex)
        {
            Dbg.Log("UIA native point-read THREW: " + ex.Message);
            return null;
        }
    }

    /// <summary>Filters shell host windows and column header cells that are
    /// not files (the hidden Win11 "Hôte contextuel" element and the explorer
    /// details-view header words).</summary>
    private static bool IsNonFileHost(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        return name.Contains("contextuel", StringComparison.OrdinalIgnoreCase)
            || name.IsMatchHost()
            || System.Text.RegularExpressions.Regex.IsMatch(
                name,
                @"^(Nom|Name|Modifi\w*|Type|Taille|Date|Genre|Size|Attributes|Statut|Status)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || name.Equals("Vue d'éléments", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Items View", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Arborescence", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Tree View", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Ligne ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMatchHost(this string value)
    {
        return value.Contains("Hôte", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Hte", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Win32 helpers
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct POINT
    {
        public readonly int X; public readonly int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT
    {
        public readonly int X; public readonly int Y;
        public readonly int Width; public readonly int Height;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, [Out] System.Text.StringBuilder name, int maxCount);

    private static string GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(none)";
        var sb = new System.Text.StringBuilder(256);
        _ = GetClassName(hwnd, sb, 256);
        return sb.ToString();
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromPoint(
        POINT pt,
        [Out, MarshalAs(UnmanagedType.Interface)] out object ppacc,
        [Out, MarshalAs(UnmanagedType.Struct)] out object pvarChild);

    private const int ChildIdSelf = 0;

    /// <summary>
    /// IAccessible (MSAA), verbatim oleacc.idl vtable order — every member must
    /// stay in this order for the COM vtable slot mapping to be correct.
    /// </summary>
    [ComImport]
    [ComVisible(true)]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IAccessible
    {
        [DispId(-5000)]
        [return: MarshalAs(UnmanagedType.Interface)]
        object get_accParent();

        [DispId(-5001)]
        int get_accChildCount();

        [DispId(-5002)]
        [return: MarshalAs(UnmanagedType.Interface)]
        object get_accChild([In] object varChild);

        [DispId(-5003)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accName([In] object varChild);

        [DispId(-5004)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accValue([In] object varChild);

        [DispId(-5005)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accDescription([In] object varChild);

        [DispId(-5006)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accRole([In] object varChild);

        [DispId(-5007)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accState([In] object varChild);

        [DispId(-5008)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accHelp([In] object varChild);

        [DispId(-5009)]
        int get_accHelpTopic([MarshalAs(UnmanagedType.BStr)] out string pszHelpFile, [In] object varChild, [Out] int pidTopic);

        [DispId(-5010)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accKeyboardShortcut([In] object varChild);

        [DispId(-5011)]
        [return: MarshalAs(UnmanagedType.Interface)]
        object get_accFocus();

        [DispId(-5012)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accSelection();

        [DispId(-5013)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accDefaultAction([In] object varChild);

        [DispId(-5014)]
        void accSelect(int flagsSelect, [In] object varChild);

        [DispId(-5015)]
        void accLocation(
            [Out] int pxLeft,
            [Out] int pyTop,
            [Out] int pcbWidth,
            [Out] int pcbHeight,
            [In] object varChild);

        [DispId(-5016)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object accNavigate(int navDir, [In] object varStart);

        [DispId(-5017)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object accHitTest(int xLeft, int yTop);

        [DispId(-5018)]
        void accDoDefaultAction([In] object varChild);
    }
}
