using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Gem.UI.DirectX;

namespace Gem.UI;

/// <summary>
/// Hardware-accelerated DirectX 11 transparent overlay window for Overdrive Shatter effects.
/// Replaces flat WPF DrawingVisual vector rendering with hardware Direct3D 11 pipeline:
/// - Transparent Win32 WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST window
/// - DXGI Premultiplied Alpha Flip SwapChain (R8G8B8A8_UNORM)
/// - Optical Glass Refraction & normal distortion with chromatic aberration (3-5 px split)
/// - Procedural Plasma Lightning Arcs with dynamic vertex jitter
/// - GPU Instanced 2D Shards with 4x explosion impulse and velocity motion blur
/// - Hardware Bloom & Neon Glow postprocessing pass
/// </summary>
public sealed class OverdriveParticleOverlayWindow : Window
{
    public const int ParticleCount = 42;

    public struct ScreenShardParticle
    {
        public double Angle;
        public double MaxDistance;
        public double CurrentDistance;
        public double BaseSize;
        public double Rotation;
        public double RotSpeed;
        public double CurveDirection;
        public float Alpha;
        public bool Active;
        public double ScreenX;
        public double ScreenY;
    }

    private readonly ScreenShardParticle[] _shards = new ScreenShardParticle[ParticleCount];
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private D3D11OverlayRenderer? _d3dRenderer;

    private double _lastTime = 0.0;
    private bool _isRenderingHooked = false;

    private Point _widgetScreenCenter = new(960, 540);
    private bool _isOverdriveActive = false;
    private double _targetProgress = 0.0;
    private double _currentProgress = 0.0;
    private float _beatIntensity = 0.0f;

    public OverdriveParticleOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Visibility = Visibility.Hidden;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = Math.Max(SystemParameters.VirtualScreenWidth, 800);
        Height = Math.Max(SystemParameters.VirtualScreenHeight, 600);

        InitParticles();

        try
        {
            _d3dRenderer = new D3D11OverlayRenderer((int)Width, (int)Height);
            _d3dRenderer.Initialize();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[D3D11Overlay] Hardware init warning: {ex.Message}");
        }
    }

    private void InitParticles()
    {
        var rand = new Random(42);
        for (int i = 0; i < ParticleCount; i++)
        {
            double angle = (i * (2.0 * Math.PI / ParticleCount)) + ((rand.NextDouble() - 0.5) * 0.2);
            double maxDist = 300.0 + rand.NextDouble() * 400.0; // 300 to 700 pixels
            double baseSize = 4.5 + rand.NextDouble() * 7.5;
            double rotSpeed = ((i % 2 == 0) ? 1.0 : -1.0) * (2.0 + rand.NextDouble() * 3.5);
            double curve = (rand.NextDouble() - 0.5) * 0.8;

            _shards[i] = new ScreenShardParticle
            {
                Angle = angle,
                MaxDistance = maxDist,
                CurrentDistance = 0.0,
                BaseSize = baseSize,
                Rotation = angle,
                RotSpeed = rotSpeed,
                CurveDirection = curve,
                Alpha = 0f,
                Active = false,
                ScreenX = 0,
                ScreenY = 0
            };
        }
    }

    public ScreenShardParticle[] GetShardSnapshot() => (ScreenShardParticle[])_shards.Clone();

    /// <summary>
    /// Updates overdrive state from the HUD widget and drives the D3D11 hardware renderer.
    /// </summary>
    public void UpdateOverdrive(Point widgetScreenCenter, bool isOverdriveActive, double overdriveProgress, float beatIntensity)
    {
        _widgetScreenCenter = widgetScreenCenter;
        _isOverdriveActive = isOverdriveActive;
        _targetProgress = Math.Clamp(overdriveProgress, 0.0, 1.0);
        _beatIntensity = Math.Clamp(beatIntensity, 0.0f, 1.0f);

        if (_targetProgress > 0.01 || _isOverdriveActive)
        {
            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
                _d3dRenderer?.Window?.Show();
            }
            StartRendering();
        }
        else if (_targetProgress <= 0.01 && !_isOverdriveActive && _currentProgress <= 0.01)
        {
            StopRendering();
            _d3dRenderer?.Window?.Hide();
            if (Visibility != Visibility.Hidden)
            {
                Visibility = Visibility.Hidden;
            }
        }
    }

    public void StartRendering()
    {
        if (!_isRenderingHooked)
        {
            _lastTime = _stopwatch.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnRendering;
            _isRenderingHooked = true;
        }
    }

    public void StopRendering()
    {
        if (_isRenderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRenderingHooked = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _stopwatch.Elapsed.TotalSeconds;
        double dt = now - _lastTime;
        _lastTime = now;
        if (dt <= 0.0 || dt > 0.1) dt = 0.016;

        double progressSpeed = _isOverdriveActive ? 8.0 : 4.0;
        _currentProgress += (_targetProgress - _currentProgress) * Math.Clamp(dt * progressSpeed, 0.0, 1.0);

        if (_currentProgress <= 0.01 && !_isOverdriveActive && _targetProgress <= 0.01)
        {
            StopRendering();
            _d3dRenderer?.Window?.Hide();
            Visibility = Visibility.Hidden;
            return;
        }

        UpdateCpuPhysics(dt);

        // Hardware DirectX 11 frame render pass
        _d3dRenderer?.RenderFrame(dt, _widgetScreenCenter, _isOverdriveActive, _currentProgress, _beatIntensity);
    }

    private void UpdateCpuPhysics(double dt)
    {
        for (int i = 0; i < ParticleCount; i++)
        {
            double targetDist = _shards[i].MaxDistance * _currentProgress;
            double beatKick = _beatIntensity * 38.0 * _currentProgress;
            double effectiveDist = targetDist + beatKick;

            double speed = _isOverdriveActive ? 11.0 : 5.5;
            _shards[i].CurrentDistance += (effectiveDist - _shards[i].CurrentDistance) * Math.Clamp(dt * speed, 0.0, 1.0);
            _shards[i].Rotation += _shards[i].RotSpeed * dt * (1.0 + _beatIntensity * 2.2);

            double curvedAngle = _shards[i].Angle + (_currentProgress * _shards[i].CurveDirection * 0.45);
            _shards[i].ScreenX = _widgetScreenCenter.X + _shards[i].CurrentDistance * Math.Cos(curvedAngle);
            _shards[i].ScreenY = _widgetScreenCenter.Y + _shards[i].CurrentDistance * Math.Sin(curvedAngle);

            _shards[i].Alpha = (float)Math.Clamp(_currentProgress * (0.6f + _beatIntensity * 0.4f), 0.0, 1.0);
            _shards[i].Active = _shards[i].CurrentDistance > 8.0 && _shards[i].Alpha > 0.02f;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopRendering();
        _d3dRenderer?.Dispose();
        _d3dRenderer = null;
        base.OnClosed(e);
    }
}
