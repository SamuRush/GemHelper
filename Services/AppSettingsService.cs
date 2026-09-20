using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gem.Services;

public sealed class WakeWordConfig
{
    public string Name { get; set; } = "джарвис";
    public string OnnxModelPath { get; set; } = "Models/WakeWord/jarvis.onnx";
    public string SmallModelPath { get; set; } = "Models/VoskSmall/vosk-model-small-ru";
    public float Threshold { get; set; } = 0.5f;
}

public sealed class AppSettingsData
{
    public string LlmBaseUrl { get; set; } = "http://127.0.0.1:1234/v1";
    public string LlmModel { get; set; } = "qwen2.5-1.5b-instruct";
    public string LlmApiKey { get; set; } = "lm";
    public string VoskModelPath { get; set; } = "./model";
    public string CityName { get; set; } = "Санкт-Петербург";
    public double Latitude { get; set; } = 59.9386;
    public double Longitude { get; set; } = 30.3141;
    public WakeWordConfig WakeWord { get; set; } = new();
    public List<string> WakeWords { get; set; } =
    [
        "джарвис", "jarvis", "рис", "вис",
        "джемини", "гемини", "джеминай", "gemini",
        "димон", "петрович", "алиса", "компьютер"
    ];
    public TtsConfig Tts { get; set; } = new();
    public Dictionary<string, string> GameAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ведьмак"] = "292030",
        ["дота"] = "570",
        ["киберпанк"] = "1091500",
        ["ноу менс скай"] = "275850"
    };
}

public sealed class TtsConfig
{
    public string PreferredEngine { get; set; } = "Edge";
    public string EdgeVoice { get; set; } = "ru-RU-DmitryNeural";
    public string SileroModelPath { get; set; } = "Models/Silero/v4_ru.onnx";
    public string SileroSpeaker { get; set; } = "aidar";
    public int ConnectionTimeoutMs { get; set; } = 3000;
}

public static class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string GetConfigFilePath()
    {
        string localAppsettings = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        if (File.Exists(localAppsettings)) return localAppsettings;

        return Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    public static AppSettingsData Load()
    {
        var data = new AppSettingsData();
        string path = GetConfigFilePath();

        if (!File.Exists(path))
        {
            return data;
        }

        try
        {
            string json = File.ReadAllText(path);
            var node = JsonNode.Parse(json);
            if (node == null) return data;

            if (node["Llm"] is JsonObject llm)
            {
                if (llm["BaseUrl"]?.GetValue<string>() is string url) data.LlmBaseUrl = url;
                if (llm["Model"]?.GetValue<string>() is string model) data.LlmModel = model;
                if (llm["ApiKey"]?.GetValue<string>() is string key) data.LlmApiKey = key;
            }

            if (node["Vosk"] is JsonObject vosk)
            {
                if (vosk["ModelPath"]?.GetValue<string>() is string modelPath) data.VoskModelPath = modelPath;
            }

            if (node["Tts"] is JsonObject tts)
            {
                if (tts["PreferredEngine"]?.GetValue<string>() is string pe) data.Tts.PreferredEngine = pe;
                if (tts["EdgeVoice"]?.GetValue<string>() is string ev) data.Tts.EdgeVoice = ev;
                if (tts["SileroModelPath"]?.GetValue<string>() is string smp) data.Tts.SileroModelPath = smp;
                if (tts["SileroSpeaker"]?.GetValue<string>() is string ss) data.Tts.SileroSpeaker = ss;
                if (tts["ConnectionTimeoutMs"]?.GetValue<int>() is int ctMs && ctMs > 0) data.Tts.ConnectionTimeoutMs = ctMs;
            }

            if (node["WakeWord"] is JsonObject ww)
            {
                if (ww["Name"]?.GetValue<string>() is string name && !string.IsNullOrWhiteSpace(name))
                    data.WakeWord.Name = name.Trim().ToLowerInvariant();
                if (ww["OnnxModelPath"]?.GetValue<string>() is string onnxPath)
                    data.WakeWord.OnnxModelPath = onnxPath;
                if (ww["SmallModelPath"]?.GetValue<string>() is string smallPath)
                    data.WakeWord.SmallModelPath = smallPath;
                if (ww["Threshold"]?.GetValue<float>() is float th)
                    data.WakeWord.Threshold = th;
            }

            if (node["WakeWords"] is JsonArray wakeWordsArr)
            {
                data.WakeWords.Clear();
                foreach (var item in wakeWordsArr)
                {
                    if (item?.GetValue<string>() is string w && !string.IsNullOrWhiteSpace(w))
                    {
                        data.WakeWords.Add(w.Trim().ToLowerInvariant());
                    }
                }
            }

            // Weather & City settings
            if (node["CityName"]?.GetValue<string>() is string city) data.CityName = city;
            if (node["Latitude"]?.GetValue<double>() is double lat) data.Latitude = lat;
            if (node["Longitude"]?.GetValue<double>() is double lon) data.Longitude = lon;

            if (node["GameAliases"] is JsonObject aliases)
            {
                foreach (var (k, v) in aliases)
                {
                    if (v != null)
                    {
                        data.GameAliases[k] = v.ToString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AppSettings Warning] Ошибка чтения конфигурации: {ex.Message}");
        }

        return data;
    }

    public static void Save(AppSettingsData data)
    {
        try
        {
            var root = new JsonObject
            {
                ["CityName"] = data.CityName,
                ["Latitude"] = data.Latitude,
                ["Longitude"] = data.Longitude,
                ["WakeWord"] = new JsonObject
                {
                    ["Name"] = data.WakeWord.Name,
                    ["OnnxModelPath"] = data.WakeWord.OnnxModelPath,
                    ["SmallModelPath"] = data.WakeWord.SmallModelPath,
                    ["Threshold"] = data.WakeWord.Threshold
                },
                ["Llm"] = new JsonObject
                {
                    ["BaseUrl"] = data.LlmBaseUrl,
                    ["ApiKey"] = data.LlmApiKey,
                    ["Model"] = data.LlmModel,
                    ["UseResponseFormat"] = false,
                    ["Debug"] = true
                },
                ["Vosk"] = new JsonObject
                {
                    ["ModelPath"] = data.VoskModelPath
                },
                ["Tts"] = new JsonObject
                {
                    ["PreferredEngine"] = data.Tts.PreferredEngine,
                    ["EdgeVoice"] = data.Tts.EdgeVoice,
                    ["SileroModelPath"] = data.Tts.SileroModelPath,
                    ["SileroSpeaker"] = data.Tts.SileroSpeaker,
                    ["ConnectionTimeoutMs"] = data.Tts.ConnectionTimeoutMs
                }
            };

            var wakeWordsArr = new JsonArray();
            foreach (var w in data.WakeWords)
            {
                wakeWordsArr.Add(w);
            }
            root["WakeWords"] = wakeWordsArr;

            var aliasesNode = new JsonObject();
            foreach (var kv in data.GameAliases)
            {
                aliasesNode[kv.Key] = kv.Value;
            }
            root["GameAliases"] = aliasesNode;

            string json = root.ToJsonString(JsonOptions);

            string fileName = "appsettings.json";
            string[] dirs = [AppContext.BaseDirectory, Directory.GetCurrentDirectory()];

            foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string fullPath = Path.Combine(dir, fileName);
                    File.WriteAllText(fullPath, json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AppSettings Warning] Не удалось записать '{Path.Combine(dir, fileName)}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AppSettings Error] Ошибка сохранения конфигурации: {ex.Message}");
            throw;
        }
    }
}
