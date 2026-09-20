using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;

namespace Gem.Services;

/// <summary>
/// Secondary / Offline TTS engine based on Silero TTS executed via Microsoft.ML.OnnxRuntime.
/// Generates 24/48 kHz PCM audio and outputs via NAudio.
/// Guarantees strictly male voice (aidar by default) and automatic background download of ru_v3.onnx.
/// </summary>
public sealed class SileroTtsEngine : ITtsEngine, IDisposable
{
    public const string DefaultModelFolder = "Models/Silero";
    public const string DefaultModelFileName = "ru_v3.onnx";
    public static readonly string DefaultModelPath = Path.Combine(DefaultModelFolder, DefaultModelFileName);

    private static readonly string[] DownloadMirrors =
    [
        "https://models.silero.ai/models/tts/ru/ru_v3.onnx",
        "https://huggingface.co/Derur/silero-models/resolve/main/ru_v3.onnx",
        "https://huggingface.co/onnx-community/silero-models/resolve/main/ru_v3.onnx",
        "https://raw.githubusercontent.com/snakers4/silero-models/master/models/ru/ru_v3.onnx"
    ];

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
        // 1. По умолчанию зафиксировать мужской голос: спикер aidar (или baya)
        _speaker = string.IsNullOrWhiteSpace(speaker) ? "aidar" : speaker.Trim().ToLowerInvariant();
        if (_speaker is not ("aidar" or "baya"))
        {
            _speaker = "aidar"; // строгая фиксация мужского голоса
        }

        _modelPath = string.IsNullOrWhiteSpace(modelPath) ? DefaultModelPath : modelPath;
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;

        EnsureModelAvailable();
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

        // Check fallback legacy path Models/TTS/silero_ru.onnx
        string legacyPath = Path.Combine(AppContext.BaseDirectory, "Models", "TTS", "silero_ru.onnx");
        if (File.Exists(legacyPath))
        {
            return legacyPath;
        }

        return directPath;
    }

    private void EnsureModelAvailable()
    {
        string fullPath = ResolveModelPath(_modelPath);
        if (File.Exists(fullPath))
        {
            return;
        }

        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[TTS: Silero] Модель '{fullPath}' отсутствует. Начинается автоматическая загрузка Silero v3 ONNX...");
        Console.ResetColor();

        bool downloaded = false;
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        foreach (var url in DownloadMirrors)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[TTS: Silero] Попытка загрузки из: {url}");
                Console.ResetColor();

                using var response = httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                long? totalBytes = response.Content.Headers.ContentLength;
                string tempPath = fullPath + ".download";

                using (var src = response.Content.ReadAsStream())
                using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[16384];
                    long totalRead = 0;
                    int read;
                    int lastPercent = -1;

                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        dst.Write(buffer, 0, read);
                        totalRead += read;

                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            int percent = (int)(totalRead * 100 / totalBytes.Value);
                            if (percent != lastPercent && percent % 10 == 0)
                            {
                                lastPercent = percent;
                                DrawProgressBar(percent, totalRead, totalBytes.Value);
                            }
                        }
                    }
                }

                if (File.Exists(fullPath)) File.Delete(fullPath);
                File.Move(tempPath, fullPath);
                downloaded = true;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n[TTS: Silero] Модель Silero v3 ONNX успешно загружена ({new FileInfo(fullPath).Length / 1024} КБ).");
                Console.ResetColor();
                break;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[TTS Warning] Ошибка загрузки с {url}: {ex.Message}");
                Console.ResetColor();
            }
        }

        if (!downloaded && !File.Exists(fullPath))
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[TTS: Silero Warning] Автозагрузка модели Silero не удалась. При отсутствии файла будет задействован мужской System.Speech fallback.");
            Console.ResetColor();
        }
    }

    private static void DrawProgressBar(int percent, long currentBytes, long totalBytes)
    {
        const int barWidth = 30;
        int filled = (percent * barWidth) / 100;
        string bar = new string('=', filled) + (filled < barWidth ? ">" : "") + new string(' ', Math.Max(0, barWidth - filled - 1));
        Console.Write($"\r[TTS: Silero Download] [{bar}] {percent}% ({currentBytes / 1024} KB / {totalBytes / 1024} KB)");
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
            Console.WriteLine($"[TTS] Модель Silero ONNX успешно инициализирована: {fullPath} (мужской голос: {_speaker})");
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
