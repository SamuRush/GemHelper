using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;

namespace Gem.Services;

/// <summary>
/// Secondary / Offline TTS engine based on Silero TTS executed via Microsoft.ML.OnnxRuntime.
/// Generates 24/48 kHz PCM audio and outputs via NAudio.
/// If the model file is not found or ONNX runtime fails to initialize, IsAvailable evaluates to false.
/// </summary>
public sealed class SileroTtsEngine : ITtsEngine, IDisposable
{
    private readonly string _modelPath;
    private readonly string _speaker;
    private readonly int _sampleRate;
    private InferenceSession? _session;
    private bool _isAvailable = false;
    private bool _disposed = false;

    public string Name => "Silero";

    public bool IsAvailable => _isAvailable && _session != null && File.Exists(ResolveModelPath(_modelPath));

    public string ModelPath => _modelPath;
    public string Speaker => _speaker;
    public int SampleRate => _sampleRate;

    public SileroTtsEngine(string? modelPath = null, string? speaker = null, int sampleRate = 48000)
    {
        _modelPath = string.IsNullOrWhiteSpace(modelPath) ? "Models/TTS/silero_ru.onnx" : modelPath;
        _speaker = string.IsNullOrWhiteSpace(speaker) ? "aidar" : speaker;
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;

        InitializeSession();
    }

    private static string ResolveModelPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        string directPath = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(directPath))
        {
            return directPath;
        }

        string currentDirPath = Path.Combine(Directory.GetCurrentDirectory(), path);
        if (File.Exists(currentDirPath))
        {
            return currentDirPath;
        }

        return directPath;
    }

    private void InitializeSession()
    {
        string fullPath = ResolveModelPath(_modelPath);

        if (!File.Exists(fullPath))
        {
            _isAvailable = false;
            return;
        }

        try
        {
            var sessionOptions = new SessionOptions();
            sessionOptions.AppendExecutionProvider_CPU();
            _session = new InferenceSession(fullPath, sessionOptions);
            _isAvailable = true;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[TTS] Модель Silero ONNX успешно инициализирована: {fullPath} (голос: {_speaker})");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            _isAvailable = false;
            _session = null;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[TTS] Ошибка инициализации Silero ONNX ('{fullPath}'): {ex.Message}");
            Console.ResetColor();
        }
    }

    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (!IsAvailable || _session == null)
        {
            throw new InvalidOperationException("Локальный синтезатор Silero TTS недоступен или модель не загружена.");
        }

        float[] audioFloats = await Task.Run(() => InferAudio(text), ct);
        if (audioFloats.Length == 0)
        {
            throw new InvalidOperationException("Silero TTS вернул пустой аудиопоток.");
        }

        await PlayPcmWaveformAsync(audioFloats, _sampleRate, ct);
    }

    private float[] InferAudio(string text)
    {
        if (_session == null) return Array.Empty<float>();

        var inputs = new List<NamedOnnxValue>();

        foreach (var (name, metadata) in _session.InputMetadata)
        {
            string lowerName = name.ToLowerInvariant();

            if (lowerName.Contains("text") || lowerName.Contains("input"))
            {
                if (metadata.ElementType == typeof(string))
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<string>(new[] { text }, new[] { 1 })));
                }
                else
                {
                    long[] tokenIds = text.Select(c => (long)c).ToArray();
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(tokenIds, new[] { 1, tokenIds.Length })));
                }
            }
            else if (lowerName.Contains("speaker"))
            {
                if (metadata.ElementType == typeof(string))
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<string>(new[] { _speaker }, new[] { 1 })));
                }
                else
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new[] { 0L }, new[] { 1 })));
                }
            }
            else if (lowerName.Contains("sample_rate") || lowerName.Contains("sr"))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new[] { (long)_sampleRate }, new[] { 1 })));
            }
        }

        using var results = _session.Run(inputs);
        var outputTensor = results.FirstOrDefault()?.AsTensor<float>();
        if (outputTensor == null)
        {
            return Array.Empty<float>();
        }

        return outputTensor.ToArray();
    }

    private static async Task PlayPcmWaveformAsync(float[] audioSamples, int sampleRate, CancellationToken ct)
    {
        // Convert float [-1.0f..1.0f] to 16-bit signed PCM mono
        byte[] pcmData = new byte[audioSamples.Length * 2];
        int byteIndex = 0;
        for (int i = 0; i < audioSamples.Length; i++)
        {
            short sample = (short)Math.Clamp(audioSamples[i] * 32767f, -32768f, 32767f);
            pcmData[byteIndex++] = (byte)(sample & 0xFF);
            pcmData[byteIndex++] = (byte)((sample >> 8) & 0xFF);
        }

        var waveFormat = new WaveFormat(sampleRate, 16, 1);
        using var memoryStream = new MemoryStream(pcmData);
        using var rawSource = new RawSourceWaveStream(memoryStream, waveFormat);
        using var waveOut = new WaveOutEvent();

        waveOut.Init(rawSource);
        waveOut.Play();

        while (waveOut.PlaybackState == PlaybackState.Playing && !ct.IsCancellationRequested)
        {
            await Task.Delay(20, ct);
        }

        if (ct.IsCancellationRequested)
        {
            waveOut.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
        _session = null;
        _isAvailable = false;
    }
}
