// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace WinPlay.App.Tray;

/// <summary>One entry in the tray right-click menu.</summary>
public sealed class TrayMenuItem
{
    public string Text { get; init; } = "";
    public Action? Clicked { get; init; }
    public bool IsChecked { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsSeparator { get; init; }
    public bool IsDefault { get; init; }

    public static TrayMenuItem Separator { get; } = new() { IsSeparator = true };
}

/// <summary>
/// Shell notification-area icon via raw Shell_NotifyIcon — no packaging or external
/// dependencies. Owns a hidden message window whose WndProc receives tray callbacks, and
/// shows a native popup menu on right-click.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmTrayCallback = WmApp + 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int WmNull = 0x0000;
    private const int NimAdd = 0x0;
    private const int NimModify = 0x1;
    private const int NimDelete = 0x2;
    private const int NifMessage = 0x1;
    private const int NifIcon = 0x2;
    private const int NifTip = 0x4;

    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint MfChecked = 0x0008;
    private const uint MfGrayed = 0x0001;
    private const uint MfDefault = 0x1000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmNoNotify = 0x0080;

    private readonly WndProcDelegate _wndProc; // rooted — GC must not collect the thunk
    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private bool _added;

    /// <summary>Left-click (open the flyout).</summary>
    public event Action? LeftClicked;

    /// <summary>Builds the right-click menu fresh each time (so checkbox state is current).</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuBuilder { get; set; }

    public TrayIcon(string tooltip, string iconPath)
    {
        _wndProc = WndProc;
        var wc = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "WinPlayTrayWindow",
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, wc.lpszClassName, "WinPlayTray", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("failed to create tray message window");

        _icon = LoadImage(IntPtr.Zero, iconPath, 1 /*IMAGE_ICON*/, 0, 0,
            0x00000010 /*LR_LOADFROMFILE*/ | 0x00000040 /*LR_DEFAULTSIZE*/);
        if (_icon == IntPtr.Zero)
            throw new InvalidOperationException($"failed to load tray icon '{iconPath}'");

        var data = MakeData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = WmTrayCallback;
        data.hIcon = _icon;
        data.szTip = tooltip;
        if (!Shell_NotifyIcon(NimAdd, ref data))
            throw new InvalidOperationException("Shell_NotifyIcon add failed");
        _added = true;
    }

    public void SetTooltip(string tooltip)
    {
        var data = MakeData();
        data.uFlags = NifTip;
        data.szTip = tooltip;
        Shell_NotifyIcon(NimModify, ref data);
    }

    private NotifyIconData MakeData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
    };

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmTrayCallback)
        {
            switch ((int)lParam & 0xFFFF)
            {
                case WmLButtonUp: LeftClicked?.Invoke(); break;
                case WmRButtonUp:
                case WmContextMenu: ShowMenu(); break;
            }
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Builds and shows the native right-click popup menu, then runs the chosen action.</summary>
    private void ShowMenu()
    {
        var items = MenuBuilder?.Invoke();
        if (items is null || items.Count == 0) return;

        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        var actions = new Dictionary<uint, Action?>();
        try
        {
            uint id = 1;
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
                    continue;
                }
                uint flags = MfString;
                if (item.IsChecked) flags |= MfChecked;
                if (!item.IsEnabled) flags |= MfGrayed;
                if (item.IsDefault) flags |= MfDefault;
                AppendMenu(menu, flags, (UIntPtr)id, item.Text);
                actions[id] = item.Clicked;
                id++;
            }

            GetCursorPos(out POINT pt);
            // Required so the menu dismisses when the user clicks elsewhere (per MSDN).
            SetForegroundWindow(_hwnd);
            uint cmd = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmNoNotify,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero); // MSDN post-TrackPopupMenu nudge

            if (cmd != 0 && actions.TryGetValue(cmd, out var action))
                action?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = MakeData();
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type,
        int cx, int cy, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint flags, UIntPtr idNewItem, string? newItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
