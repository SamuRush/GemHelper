using System;
using System.Runtime.InteropServices;
using System.Text;
using Gem.Win32;

namespace Gem.UI;

/// <summary>
/// AABB bounding box of a top-level Windows application window.
/// </summary>
public struct WindowAabb
{
    public double Left;
    public double Top;
    public double Right;
    public double Bottom;

    public readonly double Width => Right - Left;
    public readonly double Height => Bottom - Top;

    public WindowAabb(double left, double top, double right, double bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }
}

/// <summary>
/// High-performance, zero-allocation tracker of physical top-level Windows application windows.
/// Periodically queries EnumWindows with DWMWA_EXTENDED_FRAME_BOUNDS, filtering out minimized windows,
/// shell desktops, and HUD overlay windows to provide physical AABB collision obstacles for shards.
/// </summary>
public sealed class WindowBoundsTracker
{
    public const int MaxWindows = 64;

    private readonly WindowAabb[] _bounds = new WindowAabb[MaxWindows];
    private readonly WindowAabb[] _tempBounds = new WindowAabb[MaxWindows];
    private int _count = 0;
    private int _tempCount = 0;
    private double _lastScanTime = -10.0;
    private IntPtr _ignoreHwnd = IntPtr.Zero;
    private double _screenWidth = 1920.0;
    private double _screenHeight = 1080.0;
    private readonly StringBuilder _classBuffer = new(256);
    private readonly NativeMethods.EnumWindowsProc _enumProc;

    public int Count => _count;
    public WindowAabb[] Bounds => _bounds;

    public WindowBoundsTracker()
    {
        _enumProc = EnumWindowCallback;
    }

    public void Update(IntPtr myHwnd, double now, double screenW, double screenH)
    {
        if (now - _lastScanTime < 0.50) return; // 500 ms throttle
        _lastScanTime = now;
        _ignoreHwnd = myHwnd;
        _screenWidth = screenW > 0 ? screenW : 1920.0;
        _screenHeight = screenH > 0 ? screenH : 1080.0;

        _tempCount = 0;
        try
        {
            NativeMethods.EnumWindows(_enumProc, IntPtr.Zero);
            for (int i = 0; i < _tempCount; i++)
            {
                _bounds[i] = _tempBounds[i];
            }
            _count = _tempCount;
        }
        catch
        {
            // fallback gracefully
        }
    }

    private bool EnumWindowCallback(IntPtr hWnd, IntPtr lParam)
    {
        if (hWnd == IntPtr.Zero || hWnd == _ignoreHwnd) return true;
        if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd)) return true;

        _classBuffer.Clear();
        NativeMethods.GetClassName(hWnd, _classBuffer, 256);
        string cls = _classBuffer.ToString();

        // Exclude shell desktop, taskbar, tooltips, popup overlays
        if (cls.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Query visual bounds without drop shadows via DWM
        int hr = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rect, Marshal.SizeOf<RECT>());
        if (hr != 0)
        {
            // Fallback to standard GetWindowRect
            if (!NativeMethods.GetWindowRect(hWnd, out rect)) return true;
        }

        int w = rect.Width;
        int h = rect.Height;

        // Ignore zero/tiny windows or windows taking full virtual screen desktop
        if (w < 120 || h < 80) return true;
        if (w >= _screenWidth && h >= _screenHeight) return true; // Fullscreen app / desktop surface

        // Ignore off-screen windows
        if (rect.Right <= 0 || rect.Left >= _screenWidth || rect.Bottom <= 0 || rect.Top >= _screenHeight)
        {
            return true;
        }

        _tempBounds[_tempCount++] = new WindowAabb(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return _tempCount < MaxWindows;
    }
}
