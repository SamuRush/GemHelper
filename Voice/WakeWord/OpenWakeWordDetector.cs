using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Gem.Voice;

/// <summary>
/// Lightweight, ultra-low latency (<80ms) wake-word detector based on openWakeWord ONNX model.
/// Automatically downloads the lightweight jarvis.onnx model if absent.
/// </summary>
public sealed class OpenWakeWordDetector : IWakeWordDetector
{
    public const string DefaultModelFolder = "Models/WakeWord";
    public const string DefaultModelFileName = "jarvis.onnx";
    public static readonly string DefaultModelPath = Path.Combine(DefaultModelFolder, DefaultModelFileName);

    private static readonly string[] DownloadMirrors =
    [
        "https://raw.githubusercontent.com/dscripka/openWakeWord/v0.5.1/openwakeword/resources/models/hey_jarvis_v0.1.onnx",
        "https://github.com/dscripka/openWakeWord/raw/v0.5.1/openwakeword/resources/models/hey_jarvis_v0.1.onnx",
        "https://raw.githubusercontent.com/dscripka/openWakeWord/main/openwakeword/resources/models/hey_jarvis_v0.1.onnx",
        "https://huggingface.co/Soulcreek2/speechkit-wakeword-models/resolve/main/hey_jarvis.onnx"
    ];

    private readonly string _modelPath;
    private readonly float _threshold;
    private InferenceSession? _session;
    private string? _inputTensorName;
    private int[]? _inputDimensions;
    private readonly int _frameSizeSamples; // Typically 1280 samples = 80ms at 16kHz
    private readonly float[] _audioBuffer;
    private int _bufferFill = 0;
    private bool _disposed = false;

    public string Name => "OpenWakeWord-ONNX";
    public string WakeWord { get; }
    public long LastDetectionLatencyMs { get; private set; }

    public event Action? OnWakeWordDetected;

    public bool IsAvailable => _session != null;

    public OpenWakeWordDetector(
        string wakeWord = "джарвис",
        string? modelPath = null,
        float threshold = 0.5f,
        int frameSizeSamples = 1280)
    {
        WakeWord = string.IsNullOrWhiteSpace(wakeWord) ? "джарвис" : wakeWord.Trim().ToLowerInvariant();
        _modelPath = string.IsNullOrWhiteSpace(modelPath) ? ResolveModelPath(DefaultModelPath) : ResolveModelPath(modelPath);
        _threshold = Math.Clamp(threshold, 0.1f, 0.99f);
        _frameSizeSamples = frameSizeSamples > 0 ? frameSizeSamples : 1280;
        _audioBuffer = new float[_frameSizeSamples * 2];

        EnsureModelAvailable();
        InitializeSession();
    }

    private static string ResolveModelPath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        string direct = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(direct)) return direct;
        string cwd = Path.Combine(Directory.GetCurrentDirectory(), path);
        if (File.Exists(cwd)) return cwd;
        return direct;
    }

    private void EnsureModelAvailable()
    {
        if (File.Exists(_modelPath))
        {
            return;
        }

        string? dir = Path.GetDirectoryName(_modelPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[WakeWord] Модель 'jarvis.onnx' не найдена. Начинается автозагрузка в '{_modelPath}'...");
        Console.ResetColor();

        bool downloaded = false;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        foreach (var url in DownloadMirrors)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[WakeWord] Загрузка из источника: {url}");
                Console.ResetColor();

                using var response = httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                long? totalBytes = response.Content.Headers.ContentLength;
                string tempPath = _modelPath + ".download";

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

                if (File.Exists(_modelPath)) File.Delete(_modelPath);
                File.Move(tempPath, _modelPath);
                downloaded = true;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n[WakeWord] Модель 'jarvis.onnx' успешно загружена ({new FileInfo(_modelPath).Length / 1024} КБ).");
                Console.ResetColor();
                break;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[WakeWord Warning] Сбой загрузки из {url}: {ex.Message}");
                Console.ResetColor();
            }
        }

        if (!downloaded && !File.Exists(_modelPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[WakeWord Error] Не удалось скачать модель jarvis.onnx со всех зеркал. " +
                              $"Поместите jarvis.onnx вручную в '{_modelPath}'.");
            Console.ResetColor();
        }
    }

    private static void DrawProgressBar(int percent, long currentBytes, long totalBytes)
    {
        const int barWidth = 30;
        int filled = (percent * barWidth) / 100;
        string bar = new string('=', filled) + (filled < barWidth ? ">" : "") + new string(' ', Math.Max(0, barWidth - filled - 1));
        Console.Write($"\r[WakeWord] [{bar}] {percent}% ({currentBytes / 1024} KB / {totalBytes / 1024} KB)");
    }

    private void InitializeSession()
    {
        if (!File.Exists(_modelPath))
        {
            return;
        }

        try
        {
            var options = new SessionOptions();
            options.AppendExecutionProvider_CPU();
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            _session = new InferenceSession(_modelPath, options);

            // Discover input tensor name & dimensions
            var firstInput = _session.InputMetadata.FirstOrDefault();
            _inputTensorName = firstInput.Key;
            _inputDimensions = firstInput.Value?.Dimensions;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[WakeWord: ONNX] Инициализирован инференс '{_modelPath}' (вход: {_inputTensorName ?? "input"}, задержка 50–80 мс).");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            _session = null;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[WakeWord Warning] Ошибка инициализации ONNX Runtime для {_modelPath}: {ex.Message}");
            Console.ResetColor();
        }
    }

    public bool ProcessFrame(ReadOnlySpan<byte> pcmData)
    {
        if (_session == null || pcmData.Length < 2)
        {
            return false;
        }

        int sampleCount = pcmData.Length / 2;
        int spaceLeft = _audioBuffer.Length - _bufferFill;
        int toCopy = Math.Min(sampleCount, spaceLeft);

        for (int i = 0; i < toCopy; i++)
        {
            short s = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
            _audioBuffer[_bufferFill + i] = s / 32768.0f;
        }
        _bufferFill += toCopy;

        // Run inference once we accumulated at least _frameSizeSamples (80ms)
        if (_bufferFill >= _frameSizeSamples)
        {
            var sw = Stopwatch.StartNew();
            bool triggered = RunInference(_audioBuffer.AsSpan(0, _frameSizeSamples));
            sw.Stop();
            LastDetectionLatencyMs = sw.ElapsedMilliseconds;

            // Shift buffer by half-frame or full-frame for smooth overlapping detection
            int shift = _frameSizeSamples / 2;
            Array.Copy(_audioBuffer, shift, _audioBuffer, 0, _bufferFill - shift);
            _bufferFill -= shift;

            if (triggered)
            {
                Reset();
                OnWakeWordDetected?.Invoke();
                return true;
            }
        }

        return false;
    }

    private bool RunInference(ReadOnlySpan<float> frameSamples)
    {
        if (_session == null || string.IsNullOrEmpty(_inputTensorName))
        {
            return false;
        }

        try
        {
            float[] sampleArray = frameSamples.ToArray();
            DenseTensor<float> inputTensor;

            // Dynamically match input tensor dimensions required by the ONNX model
            if (_inputDimensions != null && _inputDimensions.Length == 3)
            {
                int dim1 = _inputDimensions[1] > 0 ? _inputDimensions[1] : 16;
                int dim2 = _inputDimensions[2] > 0 ? _inputDimensions[2] : (sampleArray.Length / dim1);
                inputTensor = new DenseTensor<float>(sampleArray, new[] { 1, dim1, dim2 });
            }
            else if (_inputDimensions != null && _inputDimensions.Length == 2)
            {
                inputTensor = new DenseTensor<float>(sampleArray, new[] { 1, sampleArray.Length });
            }
            else
            {
                inputTensor = new DenseTensor<float>(sampleArray, new[] { 1, sampleArray.Length });
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputTensorName, inputTensor)
            };

            using var results = _session.Run(inputs);
            var output = results.FirstOrDefault()?.AsTensor<float>();
            if (output != null)
            {
                foreach (float score in output)
                {
                    if (score >= _threshold)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpenWakeWordDetector] Ошибка инференса: {ex.Message}");
        }

        return false;
    }

    public void Reset()
    {
        _bufferFill = 0;
        Array.Clear(_audioBuffer, 0, _audioBuffer.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
        _session = null;
    }
}
