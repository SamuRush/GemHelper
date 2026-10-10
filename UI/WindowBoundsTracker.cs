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
    private readonly StringBuilder _titleBuffer = new(256);
    private readonly NativeMethods.EnumWindowsProc _enumProc;

    public int Count => _count;
    public WindowAabb[] Bounds => _bounds;

    public WindowBoundsTracker()
    {
        _enumProc = EnumWindowCallback;
    }

    public void Clear()
    {
        _count = 0;
        _tempCount = 0;
        _lastScanTime = -10.0;
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

        // 1. Проверка владельца: только корневые окна приложений (без GW_OWNER)
        if (NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;

        // 2. Проверка стилей: окно должно быть WS_VISIBLE и не быть WS_EX_TOOLWINDOW
        int style = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.WS_VISIBLE) == 0) return true;

        int exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

        // 3. Отсекать свернутые окна (IsIconic)
        if (NativeMethods.IsIconic(hWnd)) return true;

        // 4. Отсекать скрытые UWP / висячие виртуальные десктопы через DWMWA_CLOAKED
        int hrCloaked = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out int isCloaked, sizeof(int));
        if (hrCloaked == 0 && isCloaked != 0) return true;

        // 5. Проверка заголовка окна: отсекать пустые и системные заголовки
        int titleLen = NativeMethods.GetWindowTextLength(hWnd);
        if (titleLen <= 0) return true;

        _titleBuffer.Clear();
        NativeMethods.GetWindowText(hWnd, _titleBuffer, 256);
        string title = _titleBuffer.ToString().Trim();
        if (string.IsNullOrEmpty(title)) return true;

        if (title.Equals("Default IME", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Settings", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Параметры", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 6. Проверка класса окна: отсекать служебные оболочки Windows
        _classBuffer.Clear();
        NativeMethods.GetClassName(hWnd, _classBuffer, 256);
        string cls = _classBuffer.ToString();

        if (cls.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("Tooltip", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("Popup", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 7. Снятие геометрии через DWM (без теней) или GetWindowRect
        int hr = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rect, Marshal.SizeOf<RECT>());
        if (hr != 0)
        {
            if (!NativeMethods.GetWindowRect(hWnd, out rect)) return true;
        }

        int w = rect.Width;
        int h = rect.Height;

        // 8. Отсекать окна размером менее 300x300 px и полноэкранные перекрытия десктопа
        if (w < 300 || h < 300) return true;
        if (w >= _screenWidth && h >= _screenHeight) return true;

        // 9. Игнорировать окна за пределами экрана
        if (rect.Right <= 0 || rect.Left >= _screenWidth || rect.Bottom <= 0 || rect.Top >= _screenHeight)
        {
            return true;
        }

        _tempBounds[_tempCount++] = new WindowAabb(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return _tempCount < MaxWindows;
    }
}
