using System;
using System.Runtime.InteropServices;

namespace Gem.UI.DirectX;

/// <summary>
/// Transparent Win32 Overlay Window configured with WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST
/// and DWM frame extension for per-pixel DXGI alpha composition.
/// </summary>
public sealed class D3D11OverlayWindow : IDisposable
{
    private const string WindowClassName = "Gem_D3D11_TransparentOverlay";
    private static bool _classRegistered = false;
    private static readonly object ClassLock = new();
    private static D3D11Native.WndProcDelegate? _staticWndProc;

    private readonly IntPtr _hwnd;
    private readonly int _x;
    private readonly int _y;
    private int _width;
    private int _height;
    private bool _disposed;

    public IntPtr Handle => _hwnd;
    public int X => _x;
    public int Y => _y;
    public int Width => _width;
    public int Height => _height;
    public bool IsVisible { get; private set; }

    public D3D11OverlayWindow(int x, int y, int width, int height)
    {
        _x = x;
        _y = y;
        _width = Math.Max(width, 100);
        _height = Math.Max(height, 100);

        EnsureClassRegistered();

        IntPtr hInstance = D3D11Native.GetModuleHandleW(null);

        int exStyle = D3D11Native.WS_EX_LAYERED |
                      D3D11Native.WS_EX_TRANSPARENT |
                      D3D11Native.WS_EX_TOPMOST |
                      D3D11Native.WS_EX_NOACTIVATE |
                      D3D11Native.WS_EX_TOOLWINDOW;

        int style = D3D11Native.WS_POPUP;

        _hwnd = D3D11Native.CreateWindowExW(
            exStyle,
            WindowClassName,
            "Gem D3D11 Overlay",
            style,
            _x,
            _y,
            _width,
            _height,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to create transparent overlay HWND (Error={err})");
        }

        // Extend DWM frame for per-pixel alpha transparency in DXGI SwapChain
        var margins = new D3D11Native.MARGINS
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1
        };
        D3D11Native.DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        D3D11Native.SetWindowPos(
            _hwnd,
            D3D11Native.HWND_TOPMOST,
            _x,
            _y,
            _width,
            _height,
            D3D11Native.SWP_NOACTIVATE);
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classRegistered) return;

            _staticWndProc = WndProc;
            IntPtr hInstance = D3D11Native.GetModuleHandleW(null);

            var wndClass = new D3D11Native.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<D3D11Native.WNDCLASSEXW>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_staticWndProc),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = WindowClassName,
                hIconSm = IntPtr.Zero
            };

            D3D11Native.RegisterClassExW(ref wndClass);
            _classRegistered = true;
        }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case D3D11Native.WM_ERASEBKGND:
                return new IntPtr(1); // Suppress background repaint flicker
            case D3D11Native.WM_NCHITTEST:
                return new IntPtr(D3D11Native.HTTRANSPARENT); // Strictly click-through
            case D3D11Native.WM_DESTROY:
                return IntPtr.Zero;
            default:
                return D3D11Native.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    public void Show()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        D3D11Native.ShowWindow(_hwnd, D3D11Native.SW_SHOWNOACTIVATE);
        IsVisible = true;
    }

    public void Hide()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        D3D11Native.ShowWindow(_hwnd, D3D11Native.SW_HIDE);
        IsVisible = false;
    }

    public void Resize(int width, int height)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        _width = Math.Max(width, 100);
        _height = Math.Max(height, 100);

        D3D11Native.SetWindowPos(
            _hwnd,
            D3D11Native.HWND_TOPMOST,
            _x,
            _y,
            _width,
            _height,
            D3D11Native.SWP_NOMOVE | D3D11Native.SWP_NOACTIVATE);
    }

    public void ProcessMessages()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        while (D3D11Native.PeekMessageW(out var msg, _hwnd, 0, 0, 1))
        {
            D3D11Native.TranslateMessage(ref msg);
            D3D11Native.DispatchMessageW(ref msg);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            D3D11Native.DestroyWindow(_hwnd);
        }
    }
}
