using System;
using System.IO;
using System.Threading;
using System.Windows;
using Gem.UI;
using Gem.UI.DirectX;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;
using Point = System.Windows.Point;

namespace Gem.Tests;

public class D3D11OverlayTests
{
    [Fact]
    public void D3D11DeviceContextManager_InitializesDeviceAndSwapChainDescription()
    {
        using var manager = new D3D11DeviceContextManager();
        bool initialized = manager.InitializeDevice();
        Assert.True(initialized, "D3D11 device should initialize on hardware or WARP fallback");
        Assert.NotNull(manager.Device);
        Assert.NotNull(manager.Context);

        var desc = manager.CreateSwapChainDescription(800, 600);
        Assert.Equal(Format.R8G8B8A8_UNorm, desc.Format);
        Assert.Equal(AlphaMode.Premultiplied, desc.AlphaMode);
        Assert.Equal(SwapEffect.FlipDiscard, desc.SwapEffect);
        Assert.Equal(800u, desc.Width);
        Assert.Equal(600u, desc.Height);
    }

    [Fact]
    public void D3D11ShaderSources_CompilesAllShadersSuccessfully()
    {
        using var manager = new D3D11DeviceContextManager();
        manager.InitializeDevice();
        Assert.NotNull(manager.Device);

        using var shaders = new D3D11Shaders(manager.Device);
        shaders.CompileAll();

        Assert.NotNull(shaders.RefractionVS);
        Assert.NotNull(shaders.RefractionPS);
        Assert.NotNull(shaders.LightningVS);
        Assert.NotNull(shaders.LightningPS);
        Assert.NotNull(shaders.ShardVS);
        Assert.NotNull(shaders.ShardPS);
        Assert.NotNull(shaders.BloomExtractPS);
        Assert.NotNull(shaders.BloomBlurPS);
    }

    [Fact]
    public void D3D11ShardEmitter_ImpulseIncreased4xAndRicochetsOffBoundaries()
    {
        var emitter = new D3D11ShardEmitter(42);
        Assert.Equal(42, emitter.Count);

        var center = new Point(500, 400);
        emitter.Explode(center, 1.0f);

        var shards = emitter.Shards;
        for (int i = 0; i < emitter.Count; i++)
        {
            ref readonly var s = ref shards[i];
            double speed = Math.Sqrt(s.Vx * s.Vx + s.Vy * s.Vy);
            // 4x explosion impulse requirement: range 2200 to 5000 px/s
            Assert.InRange(speed, 2000.0, 5500.0);
            Assert.True(s.Active);
        }

        // Test boundary ricochets over multiple physics steps
        double boundsW = 1920.0;
        double boundsH = 1080.0;
        for (int step = 0; step < 60; step++)
        {
            emitter.UpdatePhysics(0.016, center, boundsW, boundsH, isOverdriveActive: true, 1.0, 0.8f);
        }

        // Verify shards stay within bounds or ricocheted
        for (int i = 0; i < emitter.Count; i++)
        {
            ref readonly var s = ref shards[i];
            Assert.InRange(s.X, -100.0, boundsW + 100.0);
            Assert.InRange(s.Y, -100.0, boundsH + 100.0);
        }
    }

    [Fact]
    public void D3D11OverlayWindow_CreatesTransparentWin32Window()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new D3D11OverlayWindow(100, 100, 400, 300);
                Assert.NotEqual(IntPtr.Zero, window.Handle);
                Assert.Equal(400, window.Width);
                Assert.Equal(300, window.Height);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx != null) throw threadEx;
    }

    [Fact]
    public void D3D11OverlayRenderer_RendersFrameWithoutExceptions()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var renderer = new D3D11OverlayRenderer(320, 240);
                renderer.Initialize();
                renderer.RenderFrame(0.016, new Point(160, 120), isOverdriveActive: true, 0.8, 0.9f);
                renderer.RenderFrame(0.016, new Point(160, 120), isOverdriveActive: false, 0.0, 0.0f);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TEST ERROR] {ex}");
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx != null) throw threadEx;
    }

    [Fact]
    public void OverdriveParticleOverlayWindow_CompatibilityAndHardwareIntegration()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var overlay = new OverdriveParticleOverlayWindow();
                Assert.Equal(42, OverdriveParticleOverlayWindow.ParticleCount);

                var snapshot = overlay.GetShardSnapshot();
                Assert.Equal(42, snapshot.Length);
                foreach (var shard in snapshot)
                {
                    Assert.InRange(shard.MaxDistance, 300.0, 700.0);
                }

                overlay.UpdateOverdrive(new Point(500, 400), true, 0.9, 0.95f);
                overlay.Close();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx != null) throw threadEx;
    }
}
