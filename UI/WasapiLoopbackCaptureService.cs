using System;
using Gem.Core;
using NAudio.Wave;

namespace Gem.UI;

/// <summary>
/// Service managing WASAPI Loopback system audio capture for music and beat visualization.
/// Dispatches incoming PCM/Float audio buffers strictly in-place with zero queue allocations.
/// </summary>
public sealed class WasapiLoopbackCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _loopbackCapture;

    public bool IsCapturing => _loopbackCapture != null;

    public event Action<byte[], int, WaveFormat>? OnDataAvailable;

    public void Start()
    {
        if (_loopbackCapture != null) return;

        try
        {
            _loopbackCapture = new WasapiLoopbackCapture();
            _loopbackCapture.DataAvailable += HandleDataAvailable;
            _loopbackCapture.StartRecording();
            JarvisLog.Info("WASAPI", "WASAPI Loopback capture started successfully.");
        }
        catch (Exception ex)
        {
            JarvisLog.Warn("WASAPI", $"WASAPI Loopback capture not available: {ex.Message}");
            _loopbackCapture?.Dispose();
            _loopbackCapture = null;
        }
    }

    private void HandleDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_loopbackCapture == null || e.BytesRecorded <= 0) return;

        try
        {
            OnDataAvailable?.Invoke(e.Buffer, e.BytesRecorded, _loopbackCapture.WaveFormat);
        }
        catch (Exception ex)
        {
            JarvisLog.Debug("WASAPI", $"Error processing loopback buffer: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_loopbackCapture != null)
        {
            _loopbackCapture.DataAvailable -= HandleDataAvailable;
            try
            {
                _loopbackCapture.StopRecording();
            }
            catch (Exception ex)
            {
                JarvisLog.Debug("WASAPI", $"StopRecording: {ex.Message}");
            }
            _loopbackCapture.Dispose();
            _loopbackCapture = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
