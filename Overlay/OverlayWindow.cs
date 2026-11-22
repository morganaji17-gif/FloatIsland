using System.Runtime.InteropServices;
using SonoIsland.Services;

namespace SonoIsland.Overlay;

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

    /// <summary>Called on a timer tick: draw into <see cref="Bits"/> for this size/stride.</summary>
    public Action? OnFrame;
    public Action<int, int>? OnLeftClick;
    public Action? OnRightClick;
    public Action<int, int>? OnMouseMove;
    public Action? OnMouseLeave;

    private IntPtr _memDc, _dib, _oldBmp;
    private bool _disposed;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const byte AC_SRC_OVER = 0x00, AC_SRC_ALPHA = 0x01;
    private const uint ULW_ALPHA = 0x02;
    private const uint WM_TIMER = 0x0113, WM_DESTROY = 0x0002, WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_LBUTTONDOWN = 0x0201, WM_RBUTTONUP = 0x0205, WM_MOUSEMOVE = 0x0200, WM_MOUSELEAVE = 0x02A3, WM_SETCURSOR = 0x0020;
    private const int SW_SHOWNOACTIVATE = 4;

    private bool _trackingLeave;

    /// <summary>0 = arrow, 1 = hand. Set by the host from the current hover state.</summary>
    public int CursorKind;

    private void TrackLeave()
    {
        if (_trackingLeave) return;
        _trackingLeave = true;
        var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 2 /*TME_LEAVE*/, hwndTrack = Handle };
        TrackMouseEvent(ref tme);
    }

    public void Hide() => ShowWindow(Handle, 0);
    public void Show2() => ShowWindow(Handle, SW_SHOWNOACTIVATE);

    private static readonly Dictionary<IntPtr, OverlayWindow> Live = new();
    private static WndProcDelegate? _proc; // keep alive

    public OverlayWindow(double widthDip, double heightDip)
    {
        EnsureClass();
        SetProcessDpiAwarenessContext(new IntPtr(-4));   // PER_MONITOR_AWARE_V2 (no-op if the manifest already set it)

        int w = (int)Math.Round(widthDip), h = (int)Math.Round(heightDip);
        Handle = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            "SonoIslandSkia", "SonoIsland",
            WS_POPUP,
            0, 0, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (Handle == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
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

    public void PlaceTopCentre()
    {
        GetCursorPos(out var cur);
        var mon = MonitorFromPoint(cur, 2 /*MONITOR_DEFAULTTONEAREST*/);
        if (mon == IntPtr.Zero) mon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /*PRIMARY*/);
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
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA);
        ReleaseDC(IntPtr.Zero, screenDc);

        long now = Environment.TickCount64;
        if (now - _lastRectLog > 2000)
        {
            _lastRectLog = now;
            Log.Info($"[skia] rect L={rect.Left} T={rect.Top} W={rect.Right - rect.Left} H={rect.Bottom - rect.Top} centre={rect.Left + (rect.Right - rect.Left) / 2}");
        }
    }

    private long _lastRectLog;

    public void StartTimer(uint ms)
    {
        SetTimer(Handle, (IntPtr)1, ms, IntPtr.Zero);
        // paint one frame immediately so the window is never blank
        OnFrame?.Invoke();
        Present();
    }

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
            lpszClassName = "SonoIslandSkia",
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
                    self.OnFrame?.Invoke();
                    self.Present();
                    return IntPtr.Zero;
                case WM_LBUTTONDOWN:
                    self.OnLeftClick?.Invoke((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
                    return IntPtr.Zero;
                case WM_MOUSEMOVE:
                    self.TrackLeave();
                    self.OnMouseMove?.Invoke((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
                    return IntPtr.Zero;
                case WM_MOUSELEAVE:
                    self._trackingLeave = false;
                    self.OnMouseLeave?.Invoke();
                    return IntPtr.Zero;
                case WM_SETCURSOR:
                    SetCursor(LoadCursor(IntPtr.Zero, self.CursorKind == 1 ? 32649u : 32512u)); // IDC_HAND : IDC_ARROW
                    return new IntPtr(1);                                                        // TRUE ---- handled
                case WM_RBUTTONUP:
                    self.OnRightClick?.Invoke();
                    return IntPtr.Zero;
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
