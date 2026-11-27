using System.Runtime.InteropServices;
using FloatIsland.Services;

namespace FloatIsland.Overlay;

/// <summary>
/// A real Win32 layered popup (WS_EX_LAYERED + UpdateLayeredWindow), the same shape of
/// window this needs. Unlike a WPF <c>AllowsTransparency</c> window it is not forced
/// through WPF's software compositor: we hand it a premultiplied BGRA bitmap each frame
/// and the OS composites it, so drawing is just Skia's rasteriser.
/// </summary>
internal sealed class OverlayWindow : IDisposable
{
    public IntPtr Handle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }
    public IntPtr Bits { get; private set; }
    public double Scale { get; private set; } = 1.0;

    /// <summary>Called on every timer tick. Return true when a new frame was drawn
    /// (false skips the composited blit).</summary>
    public Func<bool>? OnFrame;
    public Action<int, int>? OnLeftClick;
    /// <summary>Mouse-down. Return true to capture a drag (e.g. a slider); then
    /// <see cref="OnDrag"/> fires on movement and <see cref="OnLeftUp"/> on release,
    /// and <see cref="OnLeftClick"/> is suppressed for that press.</summary>
    public Func<float, float, bool>? OnLeftDown;
    public Action<float, float>? OnDrag;
    public Action? OnLeftUp;
    public Action? OnRightClick;
    public Action<int, int>? OnMouseMove;
    public Action? OnMouseLeave;
    public Action? OnTrayShow;
    public Action? OnTrayExit;
    /// <summary>Raised when a second launch (or another process) asks this island to show.</summary>
    public Action? OnShowRequested;

    /// <summary>Client-space hit test. When it returns false the window reports HTTRANSPARENT
    /// so clicks pass through the empty part of the surface to whatever is behind it.</summary>
    public Func<int, int, bool>? HitTest;

    /// <summary>Raised for a registered global hotkey (id is one of the Hk* constants).</summary>
    public Action<int>? OnHotkey;

    // ---- global hotkeys
    public const int HkPlayPause = 0x5101, HkNext = 0x5102, HkPrev = 0x5103;
    public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    public const uint VK_SPACE = 0x20, VK_LEFT = 0x25, VK_RIGHT = 0x27;

    public bool RegisterHotkey(int id, uint mods, uint vk) => Handle != IntPtr.Zero && RegisterHotKey(Handle, id, mods, vk);

    public static void UnregisterHotkeys(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        for (int id = HkPlayPause; id <= HkPrev; id++) UnregisterHotKey(handle, id);
    }

    /// <summary>True when the foreground window covers its whole monitor (a game or a
    /// fullscreen video), i.e. the island should get out of the way.</summary>
    public static bool ForegroundIsFullscreen(IntPtr self)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == self || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
        if (!GetWindowRect(fg, out var r)) return false;

        var mon = MonitorFromWindow(fg, 2 /*MONITOR_DEFAULTTONEAREST*/);
        if (mon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;

        var m = mi.rcMonitor;
        const int tol = 2;
        return r.Left <= m.Left + tol && r.Top <= m.Top + tol
            && r.Right >= m.Right - tol && r.Bottom >= m.Bottom - tol;
    }

    private IntPtr _memDc, _dib, _oldBmp;
    private bool _disposed;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const byte AC_SRC_OVER = 0x00, AC_SRC_ALPHA = 0x01;
    private const uint ULW_ALPHA = 0x02;
    private const uint WM_TIMER = 0x0113, WM_DESTROY = 0x0002, WM_DISPLAYCHANGE = 0x007E, WM_NCHITTEST = 0x0084, WM_HOTKEY = 0x0312;
    private const uint WM_LBUTTONDOWN = 0x0201, WM_RBUTTONUP = 0x0205, WM_MOUSEMOVE = 0x0200, WM_MOUSELEAVE = 0x02A3, WM_SETCURSOR = 0x0020, WM_LBUTTONUP = 0x0202;
    private const int SW_SHOWNOACTIVATE = 4;

    private bool _trackingLeave;
    private bool _dragging;

    /// <summary>0 = arrow, 1 = hand. Set by the host from the current hover state.</summary>
    public int CursorKind;

    /// <summary>Whole-island opacity, folded into the layered-window blend (255 = opaque).</summary>
    public byte WindowAlpha = 255;

    private void TrackLeave()
    {
        if (_trackingLeave) return;
        _trackingLeave = true;
        var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 2 /*TME_LEAVE*/, hwndTrack = Handle };
        TrackMouseEvent(ref tme);
    }

    public void Hide() => ShowWindow(Handle, 0);
    public void Show2() => ShowWindow(Handle, SW_SHOWNOACTIVATE);
    public bool IsVisible => Handle != IntPtr.Zero && IsWindowVisible(Handle);

    /// <summary>Posts WM_CLOSE, used by the live demo/self-check to end its own message loop.</summary>
    public void PostClose()
    {
        if (Handle != IntPtr.Zero) PostMessage(Handle, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Reads the alpha of one pixel in the DIB (used by the live self-check).</summary>
    public byte SampleAlpha(int x, int y)
    {
        if (Bits == IntPtr.Zero || x < 0 || y < 0 || x >= Width || y >= Height) return 0;
        return Marshal.ReadByte(Bits, y * Stride + x * 4 + 3);
    }

    // --------------------------------------------------------- cross-instance show
    public const uint WM_SHOW_REQUESTED = 0x8002; // WM_APP + 2

    /// <summary>Finds an already-running Skia island window, if any. Matched by class only:
    /// the window title can come back truncated from the layered/tool window (measured "F"),
    /// and a title match then silently finds nothing - which is how a second launch ended up
    /// doing nothing at all.</summary>
    public static IntPtr FindExisting() => FindWindow("FloatIslandSkia", null);

    /// <summary>Asks a running island (in another process) to show itself.</summary>
    public static void RequestShow(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_SHOW_REQUESTED, IntPtr.Zero, IntPtr.Zero);
    }

    // --------------------------------------------------------------------- tray
    private const uint WM_TRAY = 0x8001;          // WM_APP + 1
    private const uint NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10, LR_DEFAULTSIZE = 0x40;
    private const uint MF_STRING = 0, TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100;

    /// <summary>Adds a notification-area icon; left click shows the island, right click
    /// pops a small menu (Show / Exit).</summary>
    public void EnableTray(string iconPath, string tip)
    {
        IntPtr icon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = icon,
            szTip = tip,
        };
        _trayAdded = Shell_NotifyIcon(NIM_ADD, ref data);
        if (!_trayAdded) Log.Warn("tray: Shell_NotifyIcon failed");
    }

    public void RemoveTray()
    {
        if (!_trayAdded) return;
        var data = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = Handle, uID = 1 };
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _trayAdded = false;
    }

    private void ShowTrayMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, 1, "Show island");
        AppendMenu(menu, MF_STRING, 2, "Exit");
        GetCursorPos(out var pt);
        SetForegroundWindow(Handle);
        int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, Handle, IntPtr.Zero);
        DestroyMenu(menu);

        if (cmd == 1) OnTrayShow?.Invoke();
        else if (cmd == 2) OnTrayExit?.Invoke();
    }

    private bool _trayAdded;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    private static readonly Dictionary<IntPtr, OverlayWindow> Live = new();
    private static WndProcDelegate? _proc; // keep alive

    public OverlayWindow(double widthDip, double heightDip)
    {
        EnsureClass();
        SetProcessDpiAwarenessContext(new IntPtr(-4));   // PER_MONITOR_AWARE_V2 (no-op if the manifest already set it)

        int w = (int)Math.Round(widthDip), h = (int)Math.Round(heightDip);
        Handle = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            "FloatIslandSkia", "FloatIsland",
            WS_POPUP,
            0, 0, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (Handle == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        // The title passed to CreateWindowEx does not survive on this layered tool window
        // (read back as a single character), so set it explicitly - the window is otherwise
        // nameless to anything that looks it up by name.
        SetWindowTextW(Handle, "FloatIsland");
        Live[Handle] = this;

        Scale = GetDpiForWindow(Handle) / 96.0;
        CreateSurface((int)Math.Round(widthDip * Scale), (int)Math.Round(heightDip * Scale));
        PlaceTopCentre();
    }

    /// <summary>Resizes the surface and recentres the window at the top edge.</summary>
    public void Resize(double widthDip, double heightDip)
    {
        int w = (int)Math.Round(widthDip * Scale);
        int h = (int)Math.Round(heightDip * Scale);
        if (w == Width && h == Height) { PlaceTopCentre(); return; }
        FreeSurface();
        CreateSurface(w, h);
        PlaceTopCentre();
    }

    public void Show()
    {
        PlaceTopCentre();
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        Present();
    }

    public void PlaceTopCentre() => PlaceAt(MonitorForCursor());

    /// <summary>Sits the island on a specific monitor (0-based). Falls back to the pointer's
    /// monitor when <paramref name="index"/> is out of range or negative.</summary>
    public void PlaceOnMonitor(int index)
    {
        var mons = Monitors();
        PlaceAt(index >= 0 && index < mons.Count ? mons[index] : MonitorForCursor());
    }

    public static int MonitorCount() => Monitors().Count;

    public static List<IntPtr> Monitors()
    {
        var list = new List<IntPtr>();
        MonitorEnumProc cb = (IntPtr m, IntPtr hdc, ref RECT r, IntPtr data) => { list.Add(m); return true; };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        return list;
    }

    private static IntPtr MonitorForCursor()
    {
        GetCursorPos(out var cur);
        var mon = MonitorFromPoint(cur, 2 /*MONITOR_DEFAULTTONEAREST*/);
        return mon == IntPtr.Zero ? MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /*PRIMARY*/) : mon;
    }

    private void PlaceAt(IntPtr mon)
    {
        if (mon == IntPtr.Zero) return;
        _lastMon = mon;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);

        // Centre over the usable width (work area) so a side/top taskbar can't make it look
        // off-centre; the panel still hangs from the very top of the monitor.
        int wl = mi.rcWork.Left, wr = mi.rcWork.Right;
        int ww = wr - wl;
        int x = wl + (ww - Width) / 2;
        int top = mi.rcMonitor.Top;

        SetWindowPos(Handle, IntPtr.Zero, x, top, Width, Height, 0x0040 /*SWP_SHOWWINDOW*/);
        Log.Info($"[skia] place win={Width}x{Height} monitor={mi.rcMonitor.Right - mi.rcMonitor.Left}@({mi.rcMonitor.Left},{mi.rcMonitor.Top}) work={ww}@({wl}) x={x} centre={x + Width / 2} scale={Scale:F2}");
    }

    /// <summary>Moves the island to whichever monitor the pointer is on (only when it changes).</summary>
    public void FollowCursorMonitor()
    {
        if (Handle == IntPtr.Zero) return;
        GetCursorPos(out var cur);
        var mon = MonitorFromPoint(cur, 2 /*MONITOR_DEFAULTTONEAREST*/);
        if (mon == IntPtr.Zero || mon == _lastMon) return;
        PlaceTopCentre();
    }

    private IntPtr _lastMon;

    private void CreateSurface(int w, int h)
    {
        Width = w; Height = h; Stride = w * 4;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screenDc);

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,          // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,      // BI_RGB
            },
            bmiColors = new byte[256 * 4]
        };
        _dib = CreateDIBSection(screenDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        Bits = bits;
        _oldBmp = SelectObject(_memDc, _dib);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    private void FreeSurface()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBmp != IntPtr.Zero) SelectObject(_memDc, _oldBmp);
            DeleteDC(_memDc);
        }
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        _memDc = _dib = _oldBmp = Bits = IntPtr.Zero;
    }

    /// <summary>Pushes the current DIB to the window (one composited frame).</summary>
    public void Present()
    {
        if (Handle == IntPtr.Zero) return;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        // pptDst is the window's NEW screen position: passing {0,0} would teleport it to the
        // top-left on every frame, undoing SetWindowPos. Pass the current rect instead.
        GetWindowRect(Handle, out var rect);
        var dst = new POINT { X = rect.Left, Y = rect.Top };
        var size = new SIZE { cx = Width, cy = Height };
        var src = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = WindowAlpha, AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA);
        ReleaseDC(IntPtr.Zero, screenDc);

        long now = Environment.TickCount64;
        if (now - _lastRectLog > 2000)
        {
            _lastRectLog = now;
            Log.Debug($"[skia] rect L={rect.Left} T={rect.Top} W={rect.Right - rect.Left} H={rect.Bottom - rect.Top} centre={rect.Left + (rect.Right - rect.Left) / 2}");
        }
    }

    private long _lastRectLog;

    public void StartTimer(uint ms)
    {
        SetFrameInterval(ms);
        // paint one frame immediately so the window is never blank
        if (OnFrame?.Invoke() == true) Present();
    }

    /// <summary>0 stops the timer; otherwise the tick interval in ms. Hidden states use a
    /// slow poll so the process is effectively idle instead of blitting 100+ times a second.</summary>
    public void SetFrameInterval(uint ms)
    {
        if (ms == _frameMs) return;
        _frameMs = ms;
        KillTimer(Handle, (IntPtr)1);
        if (ms > 0) SetTimer(Handle, (IntPtr)1, ms, IntPtr.Zero);
    }

    private uint _frameMs = uint.MaxValue;

    public void Close()
    {
        if (Handle != IntPtr.Zero) DestroyWindow(Handle);
    }

    // ------------------------------------------------------------------ window class
    private static void EnsureClass()
    {
        if (_proc != null) return;
        _proc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _proc,
            hInstance = GetModuleHandle(null),
            lpszClassName = "FloatIslandSkia",
            hCursor = LoadCursor(IntPtr.Zero, 32512u),   // IDC_ARROW ---- without it Windows shows the busy cursor
        };
        RegisterClassEx(ref wc);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Live.TryGetValue(hWnd, out var self))
        {
            switch (msg)
            {
                case WM_TIMER:
                    if (self.OnFrame?.Invoke() == true) self.Present();
                    return IntPtr.Zero;
                case WM_LBUTTONDOWN:
                {
                    int x = (short)(lParam.ToInt64() & 0xFFFF), y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                    self._dragging = self.OnLeftDown?.Invoke(x, y) ?? false;
                    if (self._dragging) SetCapture(hWnd);
                    else self.OnLeftClick?.Invoke(x, y);
                    return IntPtr.Zero;
                }
                case WM_MOUSEMOVE:
                    self.TrackLeave();
                    {
                        int x = (short)(lParam.ToInt64() & 0xFFFF), y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                        self.OnMouseMove?.Invoke(x, y);
                        if (self._dragging) self.OnDrag?.Invoke(x, y);
                    }
                    return IntPtr.Zero;
                case WM_LBUTTONUP:
                    if (self._dragging)
                    {
                        self._dragging = false;
                        ReleaseCapture();
                        self.OnLeftUp?.Invoke();
                    }
                    return IntPtr.Zero;
                case WM_MOUSELEAVE:
                    self._trackingLeave = false;
                    self.OnMouseLeave?.Invoke();
                    return IntPtr.Zero;
                case WM_SETCURSOR:
                    SetCursor(LoadCursor(IntPtr.Zero, self.CursorKind == 1 ? 32649u : 32512u)); // IDC_HAND : IDC_ARROW
                    return new IntPtr(1);                                                        // TRUE ---- handled
                case WM_TRAY:
                    uint trayMsg = (uint)(lParam.ToInt64() & 0xFFFF);
                    if (trayMsg == 0x0205 /*WM_RBUTTONUP*/ || trayMsg == 0x0202 /*WM_LBUTTONUP*/) self.ShowTrayMenu();
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                    self.OnRightClick?.Invoke();
                    return IntPtr.Zero;
                case WM_SHOW_REQUESTED:
                    self.OnShowRequested?.Invoke();
                    return IntPtr.Zero;
                case WM_HOTKEY:
                    self.OnHotkey?.Invoke((int)wParam);
                    return IntPtr.Zero;
                case WM_NCHITTEST:
                {
                    long lp = lParam.ToInt64();
                    int sx = (short)(lp & 0xFFFF), sy = (short)((lp >> 16) & 0xFFFF);
                    GetWindowRect(hWnd, out var wr);
                    bool hit = self.HitTest?.Invoke(sx - wr.Left, sy - wr.Top) ?? false;
                    return new IntPtr(hit ? 1 /*HTCLIENT*/ : -1 /*HTTRANSPARENT*/);
                }
                case WM_DISPLAYCHANGE:
                    self.PlaceTopCentre();
                    return IntPtr.Zero;
                case WM_DESTROY:
                    Live.Remove(hWnd);
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FreeSurface();
    }

    /// <summary>Runs the native message pump until the window is destroyed.</summary>
    public static void RunLoop()
    {
        while (GetMessage(out MSG m, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
    }

    // --------------------------------------------------------------------- interop
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256 * 4)] public byte[] bmiColors;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT src, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool SetWindowTextW(IntPtr h, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? win);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint elapse, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr h, IntPtr id);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, uint lpCursorName);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }
}
