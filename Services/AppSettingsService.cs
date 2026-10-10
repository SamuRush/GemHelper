using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gem.Services;

public sealed class AudioConfig
{
    public int DeviceIndex { get; set; } = 0;
    public string DeviceName { get; set; } = string.Empty;
}

public sealed class WakeWordConfig
{
    public string Name { get; set; } = "джарвис";
    public string OnnxModelPath { get; set; } = "Models/WakeWord/jarvis.onnx";
    public string SmallModelPath { get; set; } = "Models/VoskSmall/vosk-model-small-ru";
    public float Threshold { get; set; } = 0.5f;
    public double NoiseGateRms { get; set; } = 350.0;
    public int MinDurationMs { get; set; } = 150;
}

public sealed class VoskConfig
{
    public string ModelPath { get; set; } = "./model";
    public bool UseSmallModel { get; set; } = true;
    public string SmallModelPath { get; set; } = "Models/VoskSmall/vosk-model-small-ru";
}

public sealed class HudSettingsConfig
{
    public double Gain { get; set; } = 3.5;
    public double BassScale { get; set; } = 1.4;
    public double Falloff { get; set; } = 0.88;
    public string ColorPalette { get; set; } = "Arc Reactor (Циан / Электрик)";
    public bool EnableColorShift { get; set; } = true;
    public bool IgnoreTtsInLoopback { get; set; } = true;
    public bool EnableShatterOverdrive { get; set; } = true;
    public string CustomCoreColor { get; set; } = "#00E5FF";
    public string CustomGlowColor { get; set; } = "#0040FF";
    public string CustomRaysColor { get; set; } = "#00FFAA";
    public bool EnableFullscreenOverdrive { get; set; } = true;
    public double OverdriveThreshold { get; set; } = 0.85;
}

public sealed class AppSettingsData
{
    public AudioConfig Audio { get; set; } = new();
    public HudSettingsConfig HudSettings { get; set; } = new();
    public string LlmBaseUrl { get; set; } = "http://127.0.0.1:1234/v1";
    public string LlmModel { get; set; } = "qwen2.5-1.5b-instruct";
    public string LlmApiKey { get; set; } = "lm";
    public VoskConfig Vosk { get; set; } = new();
    public string VoskModelPath
    {
        get => Vosk.UseSmallModel && !string.IsNullOrWhiteSpace(Vosk.SmallModelPath) ? Vosk.SmallModelPath : Vosk.ModelPath;
        set => Vosk.ModelPath = value;
    }
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
    public string Voice { get; set; } = "Aidar";
    public string EdgeVoice { get; set; } = "ru-RU-DmitryNeural";
    public string Sapi5Voice { get; set; } = "Aidar";
    public int ConnectionTimeoutMs { get; set; } = 5000;
    /// <summary>
    /// Если false — Edge-TTS (Дмитрий, онлайн) полностью исключается из пайплайна.
    /// Первичным голосом становится System.Speech с установленным Silero SAPI5 Aidar.
    /// </summary>
    public bool EnableEdgeTts { get; set; } = true;
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

            if (node["Audio"] is JsonObject audio)
            {
                if (audio["DeviceIndex"]?.GetValue<int>() is int devIdx) data.Audio.DeviceIndex = devIdx;
                if (audio["DeviceName"]?.GetValue<string>() is string devName) data.Audio.DeviceName = devName;
            }

            if (node["Llm"] is JsonObject llm)
            {
                if (llm["BaseUrl"]?.GetValue<string>() is string url) data.LlmBaseUrl = url;
                if (llm["Model"]?.GetValue<string>() is string model) data.LlmModel = model;
                if (llm["ApiKey"]?.GetValue<string>() is string key) data.LlmApiKey = key;
            }

            if (node["Vosk"] is JsonObject vosk)
            {
                if (vosk["ModelPath"]?.GetValue<string>() is string modelPath) data.Vosk.ModelPath = modelPath;
                if (vosk["UseSmallModel"] is { } useSmallNode) data.Vosk.UseSmallModel = useSmallNode.GetValue<bool>();
                if (vosk["SmallModelPath"]?.GetValue<string>() is string smallPath) data.Vosk.SmallModelPath = smallPath;
            }

            if (node["Tts"] is JsonObject tts)
            {
                if (tts["PreferredEngine"]?.GetValue<string>() is string pe) data.Tts.PreferredEngine = pe;
                if (tts["Voice"]?.GetValue<string>() is string v) data.Tts.Voice = v;
                if (tts["EdgeVoice"]?.GetValue<string>() is string ev) data.Tts.EdgeVoice = ev;
                if (tts["Sapi5Voice"]?.GetValue<string>() is string sv) data.Tts.Sapi5Voice = sv;
                if (tts["ConnectionTimeoutMs"]?.GetValue<int>() is int ctMs && ctMs > 0) data.Tts.ConnectionTimeoutMs = ctMs;
                if (tts["EnableEdgeTts"] is { } enableEdgeNode) data.Tts.EnableEdgeTts = enableEdgeNode.GetValue<bool>();
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
                if (ww["NoiseGateRms"]?.GetValue<double>() is double ng && ng > 0)
                    data.WakeWord.NoiseGateRms = ng;
                if (ww["MinDurationMs"]?.GetValue<int>() is int md && md > 0)
                    data.WakeWord.MinDurationMs = md;
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

            if (node["HudSettings"] is JsonObject hudNode || node["Hud"] is JsonObject)
            {
                var target = (node["HudSettings"] as JsonObject) ?? (node["Hud"] as JsonObject);
                if (target != null)
                {
                    if (target["Gain"]?.GetValue<double>() is double g) data.HudSettings.Gain = g;
                    if (target["BassScale"]?.GetValue<double>() is double b) data.HudSettings.BassScale = b;
                    if (target["Falloff"]?.GetValue<double>() is double f) data.HudSettings.Falloff = f;
                    if (target["ColorPalette"]?.GetValue<string>() is string cp) data.HudSettings.ColorPalette = cp;
                    if (target["EnableColorShift"] is { } cs) data.HudSettings.EnableColorShift = cs.GetValue<bool>();
                    if (target["IgnoreTtsInLoopback"] is { } it) data.HudSettings.IgnoreTtsInLoopback = it.GetValue<bool>();
                    if (target["EnableShatterOverdrive"] is { } so) data.HudSettings.EnableShatterOverdrive = so.GetValue<bool>();
                    if (target["EnableFullscreenOverdrive"] is { } fso) data.HudSettings.EnableFullscreenOverdrive = fso.GetValue<bool>();
                    if (target["OverdriveThreshold"]?.GetValue<double>() is double ot) data.HudSettings.OverdriveThreshold = ot;
                    if (target["CustomCoreColor"]?.GetValue<string>() is string ccc) data.HudSettings.CustomCoreColor = ccc;
                    if (target["CustomGlowColor"]?.GetValue<string>() is string cgc) data.HudSettings.CustomGlowColor = cgc;
                    if (target["CustomRaysColor"]?.GetValue<string>() is string crc) data.HudSettings.CustomRaysColor = crc;
                }
            }

            // Sync with HudSettings.json if exists
            string standaloneHudFile = Path.Combine(AppContext.BaseDirectory, "HudSettings.json");
            if (File.Exists(standaloneHudFile))
            {
                var standalone = Gem.UI.HudSettingsStorage.LoadFromFile(standaloneHudFile);
                if (standalone != null)
                {
                    data.HudSettings = standalone;
                }
            }

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
                ["Audio"] = new JsonObject
                {
                    ["DeviceIndex"] = data.Audio.DeviceIndex,
                    ["DeviceName"] = data.Audio.DeviceName
                },
                ["WakeWord"] = new JsonObject
                {
                    ["Name"] = data.WakeWord.Name,
                    ["OnnxModelPath"] = data.WakeWord.OnnxModelPath,
                    ["SmallModelPath"] = data.WakeWord.SmallModelPath,
                    ["Threshold"] = data.WakeWord.Threshold,
                    ["NoiseGateRms"] = data.WakeWord.NoiseGateRms,
                    ["MinDurationMs"] = data.WakeWord.MinDurationMs
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
                    ["ModelPath"] = data.Vosk.ModelPath,
                    ["UseSmallModel"] = data.Vosk.UseSmallModel,
                    ["SmallModelPath"] = data.Vosk.SmallModelPath
                },
                ["Tts"] = new JsonObject
                {
                    ["PreferredEngine"] = data.Tts.PreferredEngine,
                    ["Voice"] = data.Tts.Voice,
                    ["EdgeVoice"] = data.Tts.EdgeVoice,
                    ["Sapi5Voice"] = data.Tts.Sapi5Voice,
                    ["ConnectionTimeoutMs"] = data.Tts.ConnectionTimeoutMs,
                    ["EnableEdgeTts"] = data.Tts.EnableEdgeTts
                },
                ["HudSettings"] = new JsonObject
                {
                    ["Gain"] = data.HudSettings.Gain,
                    ["BassScale"] = data.HudSettings.BassScale,
                    ["Falloff"] = data.HudSettings.Falloff,
                    ["ColorPalette"] = data.HudSettings.ColorPalette,
                    ["EnableColorShift"] = data.HudSettings.EnableColorShift,
                    ["IgnoreTtsInLoopback"] = data.HudSettings.IgnoreTtsInLoopback,
                    ["EnableShatterOverdrive"] = data.HudSettings.EnableShatterOverdrive,
                    ["EnableFullscreenOverdrive"] = data.HudSettings.EnableFullscreenOverdrive,
                    ["OverdriveThreshold"] = data.HudSettings.OverdriveThreshold,
                    ["CustomCoreColor"] = data.HudSettings.CustomCoreColor,
                    ["CustomGlowColor"] = data.HudSettings.CustomGlowColor,
                    ["CustomRaysColor"] = data.HudSettings.CustomRaysColor
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

            // Also persist HudSettings.json
            try
            {
                Gem.UI.HudSettingsStorage.SaveToFile(data.HudSettings, Gem.UI.HudSettingsStorage.GetDefaultFilePath());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppSettings Warning] Не удалось сохранить HudSettings.json: {ex.Message}");
            }

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
