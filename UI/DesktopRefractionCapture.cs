using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gem.Win32;

namespace Gem.UI;

/// <summary>
/// Layer 1: Captures desktop / window pixels beneath the fracture epicenter
/// and renders optical refraction, slight magnification (1.09x), and chromatic aberration (2-4 px RGB shift).
/// Operates with Zero GC allocations in the 60 FPS animation loop.
/// </summary>
public sealed class DesktopRefractionCapture
{
    public bool HasCapturedBackdrop => _backdropBrush != null;
    public ImageBrush? BackdropBrush => _backdropBrush;
    public double ChromaticShiftPixels { get; set; } = 2.8; // 2-3 px chromatic aberration

    private ImageBrush? _backdropBrush;
    private ImageBrush? _redChannelBrush;
    private ImageBrush? _blueChannelBrush;

    private readonly TranslateTransform _transRed = new();
    private readonly TranslateTransform _transBlue = new();
    private readonly ScaleTransform _magnifyTransform = new();
    private readonly TransformGroup _transformGroupRed = new();
    private readonly TransformGroup _transformGroupBlue = new();
    private readonly TransformGroup _transformGroupCenter = new();

    private Rect _capturedBounds;
    private Point _capturedCenter;

    private static readonly SolidColorBrush GlassSheenBrush = InitFrozenBrush(Color.FromArgb(28, 0, 229, 255));
    private static readonly SolidColorBrush GlassHighlightBrush = InitFrozenBrush(Color.FromArgb(36, 255, 255, 255));

    private static SolidColorBrush InitFrozenBrush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    public DesktopRefractionCapture()
    {
        _transformGroupRed.Children.Add(_magnifyTransform);
        _transformGroupRed.Children.Add(_transRed);

        _transformGroupBlue.Children.Add(_magnifyTransform);
        _transformGroupBlue.Children.Add(_transBlue);

        _transformGroupCenter.Children.Add(_magnifyTransform);
    }

    public void CaptureOrFallback(Point center, double radius, double screenW, double screenH)
    {
        _capturedCenter = center;
        double x = Math.Max(0, center.X - radius);
        double y = Math.Max(0, center.Y - radius);
        double w = Math.Min(screenW - x, radius * 2.0);
        double h = Math.Min(screenH - y, radius * 2.0);
        _capturedBounds = new Rect(x, y, Math.Max(w, 100), Math.Max(h, 100));

        BitmapSource? bmp = TryCaptureDesktop(_capturedBounds);
        if (bmp == null)
        {
            bmp = GenerateProceduralDesktopPattern((int)_capturedBounds.Width, (int)_capturedBounds.Height);
        }

        // Initialize reusable frozen brushes with semi-transparent blends for chromatic aberration
        var baseBrush = new ImageBrush(bmp)
        {
            Viewbox = new Rect(0, 0, 1, 1),
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Stretch = Stretch.Fill,
            Opacity = 0.65
        };
        baseBrush.Freeze();
        _backdropBrush = baseBrush;

        var redBrush = new ImageBrush(bmp)
        {
            Viewbox = new Rect(0, 0, 1, 1),
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Stretch = Stretch.Fill,
            Opacity = 0.50
        };
        redBrush.Freeze();
        _redChannelBrush = redBrush;

        var blueBrush = new ImageBrush(bmp)
        {
            Viewbox = new Rect(0, 0, 1, 1),
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Stretch = Stretch.Fill,
            Opacity = 0.50
        };
        blueBrush.Freeze();
        _blueChannelBrush = blueBrush;
    }

    public void RenderRefractionLayer(
        DrawingContext dc,
        Point center,
        double dt,
        StreamGeometry? shardClipGeometry,
        double opacity)
    {
        if (_backdropBrush == null || opacity <= 0.01) return;

        // Optical magnification (1.09x) centered at the split rift
        _magnifyTransform.ScaleX = 1.09;
        _magnifyTransform.ScaleY = 1.09;
        _magnifyTransform.CenterX = center.X;
        _magnifyTransform.CenterY = center.Y;

        // Chromatic aberration: horizontal and vertical split (2 - 3 px)
        double caShift = ChromaticShiftPixels;
        _transRed.X = caShift;
        _transRed.Y = 1.0;

        _transBlue.X = -caShift;
        _transBlue.Y = -1.0;

        dc.PushOpacity(opacity);

        if (shardClipGeometry != null)
        {
            dc.PushClip(shardClipGeometry);
        }

        // 1. Center magnified base pass
        dc.PushTransform(_transformGroupCenter);
        dc.DrawRectangle(_backdropBrush, null, _capturedBounds);
        dc.Pop();

        // 2. Red channel chromatic shift pass (+caShift)
        if (_redChannelBrush != null)
        {
            dc.PushTransform(_transformGroupRed);
            dc.DrawRectangle(_redChannelBrush, null, _capturedBounds);
            dc.Pop();
        }

        // 3. Blue channel chromatic shift pass (-caShift)
        if (_blueChannelBrush != null)
        {
            dc.PushTransform(_transformGroupBlue);
            dc.DrawRectangle(_blueChannelBrush, null, _capturedBounds);
            dc.Pop();
        }

        // 4. Subtle refractive glass sheen & highlights
        dc.DrawRectangle(GlassSheenBrush, null, _capturedBounds);
        dc.DrawRectangle(GlassHighlightBrush, null, _capturedBounds);

        if (shardClipGeometry != null)
        {
            dc.Pop(); // Clip
        }

        dc.Pop(); // Opacity
    }

    private static BitmapSource? TryCaptureDesktop(Rect bounds)
    {
        int w = (int)bounds.Width;
        int h = (int)bounds.Height;
        if (w <= 0 || h <= 0) return null;

        IntPtr hDesktopDc = IntPtr.Zero;
        IntPtr hMemDc = IntPtr.Zero;
        IntPtr hBmp = IntPtr.Zero;
        IntPtr hOldBmp = IntPtr.Zero;

        try
        {
            hDesktopDc = NativeMethods.GetDC(IntPtr.Zero);
            if (hDesktopDc == IntPtr.Zero) return null;

            hMemDc = NativeMethods.CreateCompatibleDC(hDesktopDc);
            if (hMemDc == IntPtr.Zero) return null;

            hBmp = NativeMethods.CreateCompatibleBitmap(hDesktopDc, w, h);
            if (hBmp == IntPtr.Zero) return null;

            hOldBmp = NativeMethods.SelectObject(hMemDc, hBmp);
            bool success = NativeMethods.BitBlt(
                hMemDc, 0, 0, w, h,
                hDesktopDc, (int)bounds.X, (int)bounds.Y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            if (!success) return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBmp,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hMemDc != IntPtr.Zero && hOldBmp != IntPtr.Zero) NativeMethods.SelectObject(hMemDc, hOldBmp);
            if (hBmp != IntPtr.Zero) NativeMethods.DeleteObject(hBmp);
            if (hMemDc != IntPtr.Zero) NativeMethods.DeleteDC(hMemDc);
            if (hDesktopDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, hDesktopDc);
        }
    }

    private static BitmapSource GenerateProceduralDesktopPattern(int width, int height)
    {
        int w = Math.Clamp(width, 100, 1920);
        int h = Math.Clamp(height, 100, 1080);
        int stride = w * 4;
        byte[] pixels = new byte[h * stride];

        // Creates synthetic dark IDE background with code lines (as seen in the reference visual)
        for (int y = 0; y < h; y++)
        {
            int lineIdx = y / 22;
            bool isCodeLine = (y % 22) >= 6 && (y % 22) <= 14 && (lineIdx % 3 != 0);

            for (int x = 0; x < w; x++)
            {
                int idx = y * stride + x * 4;
                if (isCodeLine && (x % 140 < 100))
                {
                    // Code syntax colors (cyan, white, gray)
                    byte lum = (byte)(80 + (x * 7 + y * 13) % 90);
                    pixels[idx] = (byte)(lum + 30); // B
                    pixels[idx + 1] = lum;          // G
                    pixels[idx + 2] = (byte)(lum / 2); // R
                    pixels[idx + 3] = 255;          // A
                }
                else
                {
                    // Dark theme desktop background (#141924)
                    pixels[idx] = 36;
                    pixels[idx + 1] = 25;
                    pixels[idx + 2] = 20;
                    pixels[idx + 3] = 255;
                }
            }
        }

        var bmp = BitmapSource.Create(
            w, h, 96, 96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);

        bmp.Freeze();
        return bmp;
    }
}
