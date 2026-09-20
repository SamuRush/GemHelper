using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vosk;

namespace Gem.Voice;

/// <summary>
/// Fast wake-word detector using a lightweight Vosk model (vosk-model-small-ru)
/// with a strictly constrained grammar [ "{customName}", "[unk]" ] for any arbitrary wake-words (e.g. "петрович", "гена").
/// </summary>
public sealed class VoskGrammarWakeWordDetector : IWakeWordDetector
{
    public const string DefaultSmallModelFolder = "Models/VoskSmall/vosk-model-small-ru";
    public const string SmallModelDownloadUrl = "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip";

    private static Model? _smallModelSingleton;
    private static readonly object _modelLock = new();

    private readonly string _modelPath;
    private readonly string _customName;
    private readonly Regex _wakeWordRegex;  // Скомпилированный кэш: строгий матчинг по границам слов (\b...\b)
    private VoskRecognizer? _recognizer;
    private bool _disposed = false;

    public string Name => "Vosk-Grammar";
    public string WakeWord => _customName;
    public long LastDetectionLatencyMs { get; private set; }

    public event Action? OnWakeWordDetected;

    public VoskGrammarWakeWordDetector(string customName, string? modelPath = null)
    {
        _customName = string.IsNullOrWhiteSpace(customName) ? "петрович" : customName.Trim().ToLowerInvariant();
        _modelPath = string.IsNullOrWhiteSpace(modelPath) ? ResolveModelPath(DefaultSmallModelFolder) : ResolveModelPath(modelPath);

        // Компилируем строгий Regex с границами слов один раз: \bджарвис\b
        // Это предотвращает ложные срабатывания на обрывки «рис», «вис», «джа», «сюрприз» и т.д.
        _wakeWordRegex = new Regex(
            $@"\b{Regex.Escape(_customName)}\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        EnsureSmallModelAvailable();
        InitializeRecognizer();
    }

    private static string ResolveModelPath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        string direct = Path.Combine(AppContext.BaseDirectory, path);
        if (Directory.Exists(direct)) return direct;
        string cwd = Path.Combine(Directory.GetCurrentDirectory(), path);
        if (Directory.Exists(cwd)) return cwd;
        return direct;
    }

    private void EnsureSmallModelAvailable()
    {
        if (Directory.Exists(_modelPath) &&
            (Directory.Exists(Path.Combine(_modelPath, "am")) || File.Exists(Path.Combine(_modelPath, "am", "final.mdl")) || Directory.Exists(Path.Combine(_modelPath, "conf"))))
        {
            return;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[WakeWord: Vosk Grammar] Малая модель Vosk не найдена в '{_modelPath}'. Начинается автозагрузка (~45 МБ)...");
        Console.ResetColor();

        try
        {
            Directory.CreateDirectory(_modelPath);
            string tempZip = Path.Combine(Path.GetTempPath(), $"vosk_small_{Guid.NewGuid():N}.zip");

            using (var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var response = httpClient.GetAsync(SmallModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                long? totalBytes = response.Content.Headers.ContentLength;

                using (var src = response.Content.ReadAsStream())
                using (var dst = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
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
            }

            // Extract archive
            string tempExtract = Path.Combine(Path.GetTempPath(), $"vosk_small_extract_{Guid.NewGuid():N}");
            ZipFile.ExtractToDirectory(tempZip, tempExtract, true);

            var subDirs = Directory.GetDirectories(tempExtract);
            string sourceFolder = subDirs.Length == 1 ? subDirs[0] : tempExtract;

            foreach (var file in Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(sourceFolder, file);
                string dest = Path.Combine(_modelPath, rel);
                string? destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(file, dest, true);
            }

            try { Directory.Delete(tempExtract, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[VoskGrammarWakeWordDetector] {ex.Message}"); }
            try { File.Delete(tempZip); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[VoskGrammarWakeWordDetector] {ex.Message}"); }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[WakeWord: Vosk Grammar] Малая модель Vosk успешно установлена в '{_modelPath}'.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[WakeWord Warning] Не удалось скачать малую модель Vosk: {ex.Message}. " +
                              $"Попытка fallback на основную модель Vosk...");
            Console.ResetColor();
        }
    }

    private static void DrawProgressBar(int percent, long currentBytes, long totalBytes)
    {
        const int barWidth = 30;
        int filled = (percent * barWidth) / 100;
        string bar = new string('=', filled) + (filled < barWidth ? ">" : "") + new string(' ', Math.Max(0, barWidth - filled - 1));
        Console.Write($"\r[WakeWord: Vosk Small] [{bar}] {percent}% ({currentBytes / 1024} KB / {totalBytes / 1024} KB)");
    }

    private static Model GetOrInitSmallModel(string path)
    {
        if (_smallModelSingleton != null) return _smallModelSingleton;

        lock (_modelLock)
        {
            if (_smallModelSingleton == null)
            {
                bool isPathValid = Directory.Exists(path) &&
                    (Directory.Exists(Path.Combine(path, "am")) || File.Exists(Path.Combine(path, "am", "final.mdl")) || Directory.Exists(Path.Combine(path, "conf")));
                string modelToUse = isPathValid ? path : VoskModelHelper.DefaultModelFolder;
                if (!Directory.Exists(modelToUse))
                {
                    throw new DirectoryNotFoundException($"Каталог модели Vosk '{modelToUse}' не найден.");
                }

                Vosk.Vosk.SetLogLevel(-1);
                _smallModelSingleton = new Model(modelToUse);
            }
            return _smallModelSingleton;
        }
    }

    private static class NativeMethods
    {
        [DllImport("libvosk", EntryPoint = "vosk_recognizer_new_grm", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr VoskRecognizerNewGrm(HandleRef model, float sampleRate, [MarshalAs(UnmanagedType.LPUTF8Str)] string grammar);
    }

    private static readonly MethodInfo? ModelGetCPtrMethod = typeof(Model).GetMethod("getCPtr", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly ConstructorInfo? RecognizerIntPtrCtor = typeof(VoskRecognizer).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        null,
        new[] { typeof(IntPtr) },
        null);

    private void InitializeRecognizer()
    {
        try
        {
            var model = GetOrInitSmallModel(_modelPath);

            // Set strictly constrained grammar: [ "{customName}", "[unk]" ] without Unicode escaping (\uXXXX)
            string grammarJson = JsonSerializer.Serialize(new[] { _customName, "[unk]" }, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            // Marshals grammar string strictly as UTF-8 via native P/Invoke to prevent Kaldi "Ignoring word missing in vocabulary"
            _recognizer = CreateGrammarRecognizer(model, 16000.0f, grammarJson);
            _recognizer.SetMaxAlternatives(0);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[WakeWord: Vosk Grammar] Настроен строгий грамматический детектор для имени '{_customName}'.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            _recognizer = null;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[WakeWord Warning] Ошибка инициализации Vosk Grammar recognizer: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static VoskRecognizer CreateGrammarRecognizer(Model model, float sampleRate, string grammarJson)
    {
        if (ModelGetCPtrMethod != null && RecognizerIntPtrCtor != null)
        {
            var modelHandle = (HandleRef)ModelGetCPtrMethod.Invoke(null, new object[] { model })!;
            IntPtr recognizerPtr = NativeMethods.VoskRecognizerNewGrm(modelHandle, sampleRate, grammarJson);
            if (recognizerPtr != IntPtr.Zero)
            {
                return (VoskRecognizer)RecognizerIntPtrCtor.Invoke(new object[] { recognizerPtr });
            }
        }

        // Небезопасный fallback на стандартный конструктор категорически исключён:
        // VoskRecognizer(Model, float, string) не гарантирует передачу строки как UTF-8,
        // что приводит к предупреждению Kaldi "Ignoring word missing in vocabulary: ''"
        // при кириллических именах пробуждения (например, "джарвис").
        // При недоступности Reflection — бросаем явное исключение с диагностикой.
        throw new InvalidOperationException(
            "[VoskGrammarWakeWordDetector] Не удалось создать Grammar Recognizer через UTF-8 P/Invoke. " +
            "Reflection getCPtr/IntPtr-конструктор недоступен. " +
            "Убедитесь, что libvosk.dll загружена и сборка не выполнена в режиме AOT без Reflection Metadata.");
    }

    public bool ProcessFrame(ReadOnlySpan<byte> pcmData)
    {
        if (_recognizer == null || pcmData.Length == 0)
        {
            return false;
        }

        var sw = Stopwatch.StartNew();
        byte[] buffer = pcmData.ToArray();
        bool isFinal = _recognizer.AcceptWaveform(buffer, buffer.Length);
        string json = isFinal ? _recognizer.Result() : _recognizer.PartialResult();
        string text = ExtractText(json, isFinal);
        sw.Stop();
        LastDetectionLatencyMs = sw.ElapsedMilliseconds;

        // СТРОГИЙ ТОКЕН-МАТЧИНГ: проверяем наличие полного изолированного слова с границами \b...\b.
        // text.Contains() запрещён — он срабатывает на обрывки «рис», «вис», «джа», «сюрприз».
        if (!string.IsNullOrWhiteSpace(text) && _wakeWordRegex.IsMatch(text))
        {
            Reset();
            OnWakeWordDetected?.Invoke();
            return true;
        }

        return false;
    }

    private static string ExtractText(string json, bool isFinal)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string prop = isFinal ? "text" : "partial";
            if (root.TryGetProperty(prop, out var elem))
            {
                return elem.GetString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VoskGrammarWakeWordDetector] Ошибка парсинга JSON: {ex.Message}");
        }
        return string.Empty;
    }

    public void Reset()
    {
        try
        {
            _recognizer?.Reset();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VoskGrammarWakeWordDetector] Ошибка Reset: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recognizer?.Dispose();
        _recognizer = null;
    }
}
