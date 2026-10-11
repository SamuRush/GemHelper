using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using Gem.Win32;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Gem.UI.DirectX;

/// <summary>
/// GPU Glass Refraction and Cracked Matrix displacement pass with chromatic aberration (3-5 px split).
/// </summary>
public sealed class D3D11RefractionPass : IDisposable
{
    private ID3D11Texture2D? _backdropTexture;
    private ID3D11ShaderResourceView? _backdropSrv;
    private ID3D11Buffer? _constantBuffer;

    private int _texWidth;
    private int _texHeight;
    private bool _disposed;

    public void Initialize(ID3D11Device device, int width, int height)
    {
        _texWidth = Math.Max(width, 100);
        _texHeight = Math.Max(height, 100);

        // Constant buffer for RefractionCBuffer
        var cbDesc = new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<RefractionCBuffer>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        };
        _constantBuffer = device.CreateBuffer(cbDesc);

        CreateBackdropTexture(device, _texWidth, _texHeight);
    }

    private void CreateBackdropTexture(ID3D11Device device, int width, int height)
    {
        _backdropSrv?.Dispose();
        _backdropTexture?.Dispose();

        var texDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None
        };

        _backdropTexture = device.CreateTexture2D(texDesc);
        _backdropSrv = device.CreateShaderResourceView(_backdropTexture);

        // Populate initial pattern or captured desktop
        UpdateBackdrop(device);
    }

    public void UpdateBackdrop(ID3D11Device device)
    {
        if (_backdropTexture == null || _texWidth <= 0 || _texHeight <= 0) return;

        byte[] pixels = CaptureDesktopPixels(_texWidth, _texHeight);
        unsafe
        {
            fixed (byte* pData = pixels)
            {
                var box = new SubresourceData(new IntPtr(pData), (uint)(_texWidth * 4), 0);
                device.ImmediateContext.UpdateSubresource(_backdropTexture, 0, null, box.DataPointer, box.RowPitch, 0);
            }
        }
    }

    public void Render(
        ID3D11DeviceContext context,
        D3D11Shaders shaders,
        Point center,
        double progress,
        double time,
        int width,
        int height)
    {
        if (_constantBuffer == null || _backdropSrv == null || progress <= 0.001) return;

        // Upload constant buffer parameters
        var cb = new RefractionCBuffer
        {
            Center = new Vector2((float)center.X, (float)center.Y),
            DistortionIntensity = (float)progress,
            ChromaticAberrationPixels = 4.2f, // 3-5 px chromatic aberration
            Resolution = new Vector2(width, height),
            Time = (float)time,
            Padding = 0f
        };

        var mapped = context.Map(_constantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(cb, mapped.DataPointer, false);
        context.Unmap(_constantBuffer, 0);

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(null);

        context.VSSetShader(shaders.RefractionVS);
        context.PSSetShader(shaders.RefractionPS);
        context.PSSetConstantBuffer(0, _constantBuffer);
        context.PSSetShaderResource(0, _backdropSrv);

        // Draw fullscreen triangle (SV_VertexID 0..3)
        context.Draw(3, 0);

        context.PSSetShaderResource(0, null!);
    }

    private static byte[] CaptureDesktopPixels(int width, int height)
    {
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];

        IntPtr hDesktopDc = NativeMethods.GetDC(IntPtr.Zero);
        if (hDesktopDc != IntPtr.Zero)
        {
            IntPtr hMemDc = NativeMethods.CreateCompatibleDC(hDesktopDc);
            IntPtr hBmp = NativeMethods.CreateCompatibleBitmap(hDesktopDc, width, height);

            if (hMemDc != IntPtr.Zero && hBmp != IntPtr.Zero)
            {
                IntPtr hOld = NativeMethods.SelectObject(hMemDc, hBmp);
                bool success = NativeMethods.BitBlt(
                    hMemDc, 0, 0, width, height,
                    hDesktopDc, 0, 0,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

                if (success)
                {
                    // Copy bitmap bits
                    var bmi = new BITMAPINFO
                    {
                        bmiHeader = new BITMAPINFOHEADER
                        {
                            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                            biWidth = width,
                            biHeight = -height, // top-down
                            biPlanes = 1,
                            biBitCount = 32,
                            biCompression = 0
                        }
                    };
                    GetDIBits(hMemDc, hBmp, 0, (uint)height, pixels, ref bmi, 0);
                }

                NativeMethods.SelectObject(hMemDc, hOld);
                NativeMethods.DeleteObject(hBmp);
                NativeMethods.DeleteDC(hMemDc);
            }
            NativeMethods.ReleaseDC(IntPtr.Zero, hDesktopDc);
        }

        return pixels;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc,
        IntPtr hbmp,
        uint uStartScan,
        uint cScanLines,
        [Out] byte[] lpvBits,
        ref BITMAPINFO lpbi,
        uint uUsage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _constantBuffer?.Dispose();
        _backdropSrv?.Dispose();
        _backdropTexture?.Dispose();
    }
}
