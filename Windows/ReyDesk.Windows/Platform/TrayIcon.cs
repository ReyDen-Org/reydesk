using System;
using System.Runtime.InteropServices;
using ReyDesk.Windows.Localization;

namespace ReyDesk.Windows.Platform;

/// <summary>Connection state shown by the tray icon: Online=Tray-Online.ico, Connecting=Tray-Connecting.ico, Error=Tray-Error.ico, Offline=Tray-Offline.ico.</summary>
internal enum TrayState { Offline, Connecting, Online, Error }

/// <summary>
/// Win32 system tray icon (spec §20). WinUI3 has no built-in tray API, so this
/// talks to Shell_NotifyIcon directly through a dedicated hidden message-only
/// window rather than subclassing MainWindow's HWND - keeps tray message
/// handling fully isolated from WinUI's own window procedure.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint WM_TRAYICON = 0x8000 + 1; // WM_APP + 1
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_LBUTTONDBLCLK = 0x0203;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIF_REALTIME = 0x00000040;
    private const uint NIN_BALLOONUSERCLICK = 0x0400 + 5;

    private const uint WM_MEASUREITEM = 0x002C;
    private const uint WM_DRAWITEM = 0x002B;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_OWNERDRAW = 0x00000100;
    private const uint TPM_RIGHTALIGN = 0x0008;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_LAYOUTRTL = 0x8000;
    private const uint ODS_SELECTED = 0x0001;
    private const uint ODS_GRAYED = 0x0002;
    private const uint FR_PRIVATE = 0x10;
    private const uint MIM_BACKGROUND = 0x00000002;
    private const int SM_CXSMICON = 49;
    private const uint DT_LEFT = 0x0, DT_RIGHT = 0x2, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_RTLREADING = 0x20000;

    // Owner-draw item kinds, carried in itemData.
    private const int KindHeader = 1;
    private const int KindSeparator = 2;
    private const int KindOpen = 3;
    private const int KindQuit = 4;
    private const int ItemHeightDip = 32;
    private const int SeparatorHeightDip = 9;
    private const int PadDip = 12;
    private const int DotDip = 8;
    private const int MinWidthDip = 190;

    private const int CmdOpen = 1;
    private const int CmdQuit = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEASUREITEMSTRUCT
    {
        public uint CtlType, CtlID, itemID, itemWidth, itemHeight;
        public UIntPtr itemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DRAWITEMSTRUCT
    {
        public uint CtlType, CtlID, itemID, itemAction, itemState;
        public IntPtr hwndItem, hDC;
        public RECT rcItem;
        public UIntPtr itemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MENUINFO
    {
        public int cbSize;
        public uint fMask, dwStyle;
        public uint cyMax;
        public IntPtr hbrBack;
        public uint dwContextHelpID;
        public UIntPtr dwMenuData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, IntPtr lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetMenuInfo(IntPtr hMenu, ref MENUINFO lpcmi);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hDC, string lpchText, int cchText, ref RECT lprc, uint format);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int i);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontIndirectW(ref LOGFONT lplf);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string lpString, int c, out SIZE psizl);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceExW(string name, uint fl, IntPtr res);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

    private readonly WndProcDelegate _wndProc;
    private readonly IntPtr _hwnd;
    private readonly string _assetsDir;
    private readonly string _baseTooltip;
    private IntPtr _hIcon;
    private TrayState _state = TrayState.Offline;
    private bool _fontsLoaded;
    private NOTIFYICONDATA _iconData;
    private bool _disposed;

    public event Action? OpenRequested;
    public event Action? PhoneHubOpenRequested;
    public event Action? QuitRequested;
    public string LastAlertStatus { get; private set; } = "hub.testFailed";

    public TrayIcon(string tooltip, string iconPath)
    {
        _wndProc = WndProc;

        var className = "ReyDeskTrayWindow";
        var wc = new WNDCLASS
        {
            lpfnWndProc = _wndProc,
            hInstance = Marshal.GetHINSTANCE(typeof(TrayIcon).Module),
            lpszClassName = className,
        };
        RegisterClassW(ref wc);

        // HWND_MESSAGE (-3) parent: a message-only window, never shown, exists
        // purely to receive the tray callback + menu command messages.
        _hwnd = CreateWindowExW(0, className, "ReyDesk Tray", 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        _assetsDir = System.IO.Path.GetDirectoryName(iconPath) ?? "";
        _baseTooltip = tooltip;
        _hIcon = LoadStateIcon(TrayState.Offline) ?? LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);

        _iconData = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = tooltip,
            szInfo = "",
            szInfoTitle = "",
        };
        Shell_NotifyIconW(NIM_ADD, ref _iconData);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAYICON:
                var evt = (uint)lParam.ToInt64();
                if (evt is WM_RBUTTONUP)
                {
                    ShowContextMenu();
                }
                else if (evt == NIN_BALLOONUSERCLICK)
                {
                    ClearPhoneHubAlert();
                    PhoneHubOpenRequested?.Invoke();
                }
                else if (evt == WM_LBUTTONDBLCLK)
                {
                    ClearPhoneHubAlert();
                    OpenRequested?.Invoke();
                }
                return IntPtr.Zero;

            case WM_MEASUREITEM:
                MeasureItem(lParam);
                return new IntPtr(1);

            case WM_DRAWITEM:
                DrawItem(lParam);
                return new IntPtr(1);

            case WM_COMMAND:
                var cmd = (int)wParam.ToInt64() & 0xFFFF;
                if (cmd == CmdOpen) OpenRequested?.Invoke();
                else if (cmd == CmdQuit) QuitRequested?.Invoke();
                return IntPtr.Zero;

            default:
                return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>Switches the tray icon and tooltip to the given connection state.</summary>
    public void SetState(TrayState state)
    {
        if (_disposed || state == _state) return;
        var icon = LoadStateIcon(state);
        if (icon == null) return;
        _state = state;
        var old = _hIcon;
        _hIcon = icon.Value;
        _iconData.hIcon = _hIcon;
        _iconData.szTip = _baseTooltip + " · " + StateText(state);
        var data = _iconData;
        data.uFlags = NIF_ICON | NIF_TIP;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private IntPtr? LoadStateIcon(TrayState state)
    {
        var name = state switch
        {
            TrayState.Online => "Tray-Online.ico",
            TrayState.Connecting => "Tray-Connecting.ico",
            TrayState.Error => "Tray-Error.ico",
            _ => "Tray-Offline.ico",
        };
        var path = System.IO.Path.Combine(_assetsDir, name);
        if (!System.IO.File.Exists(path)) return null;
        var size = GetSystemMetrics(SM_CXSMICON);
        var h = LoadImageW(IntPtr.Zero, path, IMAGE_ICON, size, size, LR_LOADFROMFILE);
        return h == IntPtr.Zero ? null : h;
    }

    // Keys tray.* are new; until they are added to Strings.cs the English default is shown.
    private static string T(string key, string fallback)
    {
        var v = Strings.Get(key);
        return v == key ? fallback : v;
    }

    private static string StateText(TrayState state) => state switch
    {
        TrayState.Online => T("tray.online", "Connected"),
        TrayState.Connecting => T("tray.connecting", "Connecting"),
        TrayState.Error => T("tray.error", "Connection error"),
        _ => T("tray.offline", "Offline"),
    };

    private string ItemText(int kind) => kind switch
    {
        KindHeader => StateText(_state),
        KindOpen => T("tray.open", "Open ReyDesk"),
        KindQuit => T("tray.quit", "Quit"),
        _ => "",
    };

    private static int Scale(int dip, uint dpi) => (int)Math.Round(dip * dpi / 96.0);

    private static bool IsRtl => Strings.IsRtl(Strings.Current);

    private void EnsureFonts()
    {
        if (_fontsLoaded) return;
        _fontsLoaded = true;
        var fonts = System.IO.Path.Combine(_assetsDir, "Fonts");
        foreach (var f in new[] { "Vazirmatn-Regular.ttf", "Vazirmatn-Medium.ttf" })
        {
            var p = System.IO.Path.Combine(fonts, f);
            if (System.IO.File.Exists(p)) AddFontResourceExW(p, FR_PRIVATE, IntPtr.Zero);
        }
    }

    private IntPtr CreateMenuFont(uint dpi)
    {
        EnsureFonts();
        var lf = new LOGFONT
        {
            lfHeight = -(int)Math.Round(10 * dpi / 72.0), // 10pt
            lfWeight = 400,
            lfCharSet = 1, // DEFAULT_CHARSET
            lfQuality = 5, // CLEARTYPE_QUALITY
            lfFaceName = IsRtl ? "Vazirmatn" : "Segoe UI Variable",
        };
        return CreateFontIndirectW(ref lf);
    }

    private void MeasureItem(IntPtr lParam)
    {
        var m = Marshal.PtrToStructure<MEASUREITEMSTRUCT>(lParam);
        var dpi = GetDpiForSystem();
        var kind = (int)m.itemData.ToUInt64();
        if (kind == KindSeparator)
        {
            m.itemHeight = (uint)Scale(SeparatorHeightDip, dpi);
            m.itemWidth = 1;
        }
        else
        {
            var dc = GetDC(_hwnd);
            var font = CreateMenuFont(dpi);
            var old = SelectObject(dc, font);
            var text = ItemText(kind);
            GetTextExtentPoint32W(dc, text, text.Length, out var sz);
            SelectObject(dc, old);
            DeleteObject(font);
            ReleaseDC(_hwnd, dc);
            var extra = kind == KindHeader ? Scale(DotDip + 8, dpi) : 0;
            m.itemWidth = (uint)Math.Max(Scale(MinWidthDip, dpi), sz.cx + extra + Scale(PadDip * 2, dpi));
            m.itemHeight = (uint)Scale(ItemHeightDip, dpi);
        }
        Marshal.StructureToPtr(m, lParam, false);
    }

    private void DrawItem(IntPtr lParam)
    {
        var d = Marshal.PtrToStructure<DRAWITEMSTRUCT>(lParam);
        var kind = (int)d.itemData.ToUInt64();
        var dpi = GetDpiForSystem();
        var rc = d.rcItem;
        var dc = d.hDC;

        var selected = (d.itemState & ODS_SELECTED) != 0 && (kind == KindOpen || kind == KindQuit);
        var bg = CreateSolidBrush(selected ? TrayPalette.Hover : TrayPalette.Surface);
        FillRect(dc, ref rc, bg);
        DeleteObject(bg);

        var pad = Scale(PadDip, dpi);
        if (kind == KindSeparator)
        {
            var mid = rc.Top + (rc.Bottom - rc.Top) / 2;
            var line = new RECT { Left = rc.Left + pad, Right = rc.Right - pad, Top = mid, Bottom = mid + 1 };
            var b = CreateSolidBrush(TrayPalette.Separator);
            FillRect(dc, ref line, b);
            DeleteObject(b);
            return;
        }

        var rtl = IsRtl;
        var textRc = new RECT { Left = rc.Left + pad, Right = rc.Right - pad, Top = rc.Top, Bottom = rc.Bottom };

        if (kind == KindHeader)
        {
            var dot = Scale(DotDip, dpi);
            var gap = Scale(8, dpi);
            var top = rc.Top + (rc.Bottom - rc.Top - dot) / 2;
            var left = rtl ? rc.Right - pad - dot : rc.Left + pad;
            var dotBrush = CreateSolidBrush(TrayPalette.ForState(_state));
            var oldBrush = SelectObject(dc, dotBrush);
            var oldPen = SelectObject(dc, GetStockObject(8)); // NULL_PEN
            Ellipse(dc, left, top, left + dot + 1, top + dot + 1);
            SelectObject(dc, oldPen);
            SelectObject(dc, oldBrush);
            DeleteObject(dotBrush);
            if (rtl) textRc.Right = left - gap; else textRc.Left = left + dot + gap;
        }

        var font = CreateMenuFont(dpi);
        var oldFont = SelectObject(dc, font);
        SetBkMode(dc, 1); // TRANSPARENT
        var disabled = (d.itemState & ODS_GRAYED) != 0 && kind != KindHeader;
        SetTextColor(dc, disabled ? TrayPalette.TextDisabled : TrayPalette.Text);
        var fmt = DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | (rtl ? DT_RIGHT | DT_RTLREADING : DT_LEFT);
        var text = ItemText(kind);
        DrawTextW(dc, text, text.Length, ref textRc, fmt);
        SelectObject(dc, oldFont);
        DeleteObject(font);
    }

    private void ShowContextMenu()
    {
        var hMenu = CreatePopupMenu();
        var surface = CreateSolidBrush(TrayPalette.Surface);
        var info = new MENUINFO { cbSize = Marshal.SizeOf<MENUINFO>(), fMask = MIM_BACKGROUND, hbrBack = surface };
        SetMenuInfo(hMenu, ref info);

        AppendMenuW(hMenu, MF_OWNERDRAW | MF_GRAYED, IntPtr.Zero, new IntPtr(KindHeader));
        AppendMenuW(hMenu, MF_OWNERDRAW | MF_SEPARATOR, IntPtr.Zero, new IntPtr(KindSeparator));
        AppendMenuW(hMenu, MF_OWNERDRAW, new IntPtr(CmdOpen), new IntPtr(KindOpen));
        AppendMenuW(hMenu, MF_OWNERDRAW, new IntPtr(CmdQuit), new IntPtr(KindQuit));

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // required so the popup menu dismisses correctly on focus loss
        var flags = TPM_RIGHTALIGN | TPM_BOTTOMALIGN | (IsRtl ? TPM_LAYOUTRTL : 0);
        TrackPopupMenuEx(hMenu, flags, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(hMenu);
        DeleteObject(surface);
    }

    public void Dispose()
    {
        if (_disposed) return;
        ClearPhoneHubAlert();
        _disposed = true;
        Shell_NotifyIconW(NIM_DELETE, ref _iconData);
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    // No remote strings enter the shell. Windows may suppress even a successful submission.
    public bool ShowPhoneHubAlert() => ShowPhoneHubAlertCore(test: false);
    public bool ShowPhoneHubTestAlert() => ShowPhoneHubAlertCore(test: true);

    private bool ShowPhoneHubAlertCore(bool test)
    {
        LastAlertStatus = "hub.testFailed";
        if (_disposed) return false;
        if (SHQueryUserNotificationState(out var state) != 0) return false;
        LastAlertStatus = NotificationStateStatus(state);
        if (state != 5) return false; // Respect Windows fullscreen/presentation/quiet states.
        var data = _iconData;
        data.uFlags = NIF_INFO | NIF_REALTIME;
        data.szInfoTitle = Strings.Get(test ? "hub.shellTestTitle" : "hub.shellTitle");
        data.szInfo = Strings.Get(test ? "hub.shellTestMessage" : "hub.shellMessage");
        data.dwInfoFlags = 0x01 | 0x10 | 0x80; // NIIF_INFO | NOSOUND | RESPECT_QUIET_TIME
        var accepted = Shell_NotifyIconW(NIM_MODIFY, ref data);
        if (!accepted) LastAlertStatus = "hub.testFailed";
        return accepted;
    }
    public static string NotificationStateStatus(int state) => state switch
    {
        1 => "hub.testNotPresent",
        2 or 3 or 4 or 7 => "hub.testBusy",
        5 => "hub.testSubmitted",
        6 => "hub.testQuiet",
        _ => "hub.testFailed",
    };
    public void ClearPhoneHubAlert()
    {
        if (_disposed) return;
        var data = _iconData;
        data.uFlags = NIF_INFO;
        data.szInfo = ""; data.szInfoTitle = "";
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }
}
