using NAudio.Wave;

namespace Gem.Voice;

/// <summary>
/// Encapsulates NAudio WaveIn capture, lifecycle, buffer pooling, and device switching.
/// </summary>
public sealed class MicrophoneAudioRecorder : IDisposable
{
    private WaveInEvent? _waveIn;
    private int _deviceNumber;
    private readonly object _lock = new();
    private bool _isRecording = false;

    public int DeviceNumber => _deviceNumber;
    public bool IsRecording => _isRecording;

    public event Action<byte[], int>? OnAudioDataAvailable;

    public MicrophoneAudioRecorder(int deviceNumber = 0)
    {
        _deviceNumber = deviceNumber;
    }

    public void StartRecording()
    {
        lock (_lock)
        {
            if (_isRecording) return;

            StopRecordingInternal();

            int bufferMs = 35;
            int devCount = WaveInEvent.DeviceCount;
            int devIndex = (_deviceNumber >= 0 && _deviceNumber < devCount) ? _deviceNumber : 0;

            _waveIn = new WaveInEvent
            {
                DeviceNumber = devIndex,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = bufferMs,
                NumberOfBuffers = 3
            };

            _waveIn.DataAvailable += HandleDataAvailable;
            _waveIn.StartRecording();
            _isRecording = true;
        }
    }

    public void StopRecording()
    {
        lock (_lock)
        {
            if (!_isRecording) return;
            StopRecordingInternal();
            _isRecording = false;
        }
    }

    public void SetDeviceNumber(int deviceNumber)
    {
        lock (_lock)
        {
            _deviceNumber = deviceNumber;
            if (_isRecording)
            {
                StopRecordingInternal();
                StartRecording();
            }
        }
    }

    private void HandleDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0)
        {
            OnAudioDataAvailable?.Invoke(e.Buffer, e.BytesRecorded);
        }
    }

    private void StopRecordingInternal()
    {
        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= HandleDataAvailable;
            try
            {
                _waveIn.StopRecording();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MicrophoneAudioRecorder Warning] StopRecording: {ex.Message}");
            }
            _waveIn.Dispose();
            _waveIn = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _isRecording = false;
            StopRecordingInternal();
            OnAudioDataAvailable = null;
        }
    }
}
