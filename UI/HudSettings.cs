using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gem.Services;

namespace Gem.UI;

/// <summary>
/// Сервис сохранения и загрузки настроек HUD-визуализатора в HudSettings.json.
/// Обеспечивает независимую работу конфигурации HUD и синхронизацию с общим appsettings.json.
/// </summary>
public static class HudSettingsStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string GetDefaultFilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "HudSettings.json");
    }

    public static void SaveToFile(HudSettingsConfig config, string filePath)
    {
        if (config == null) return;

        var node = new JsonObject
        {
            ["Gain"] = config.Gain,
            ["BassScale"] = config.BassScale,
            ["Falloff"] = config.Falloff,
            ["ColorPalette"] = config.ColorPalette,
            ["EnableColorShift"] = config.EnableColorShift,
            ["IgnoreTtsInLoopback"] = config.IgnoreTtsInLoopback,
            ["EnableShatterOverdrive"] = config.EnableShatterOverdrive,
            ["EnableFullscreenOverdrive"] = config.EnableFullscreenOverdrive,
            ["OverdriveThreshold"] = config.OverdriveThreshold,
            ["OverdriveCooldown"] = config.OverdriveCooldown,
            ["ShardCount"] = config.ShardCount,
            ["OverdriveDuration"] = config.OverdriveDuration,
            ["EnableMouseRepulsion"] = config.EnableMouseRepulsion,
            ["EnableWindowCollisions"] = config.EnableWindowCollisions,
            ["CustomCoreColor"] = config.CustomCoreColor,
            ["CustomGlowColor"] = config.CustomGlowColor,
            ["CustomRaysColor"] = config.CustomRaysColor
        };

        string json = node.ToJsonString(JsonOptions);
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(filePath, json);

        // Также синхронизируем с текущим каталогом если пути различаются
        try
        {
            string cwdFile = Path.Combine(Directory.GetCurrentDirectory(), Path.GetFileName(filePath));
            if (!string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(cwdFile), StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(cwdFile, json);
            }
        }
        catch { }
    }

    public static HudSettingsConfig LoadFromFile(string filePath)
    {
        var config = new HudSettingsConfig();
        if (!File.Exists(filePath))
        {
            return config;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            var parsed = JsonNode.Parse(json);
            if (parsed is JsonObject node)
            {
                ApplyJsonNode(config, node);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HudSettings Warning] Ошибка чтения '{filePath}': {ex.Message}");
        }

        return config;
    }

    public static void ApplyJsonNode(HudSettingsConfig config, JsonObject node)
    {
        if (node["Gain"]?.GetValue<double>() is double g) config.Gain = g;
        if (node["BassScale"]?.GetValue<double>() is double b) config.BassScale = b;
        if (node["Falloff"]?.GetValue<double>() is double f) config.Falloff = f;
        if (node["ColorPalette"]?.GetValue<string>() is string cp) config.ColorPalette = cp;
        if (node["EnableColorShift"] is { } cs) config.EnableColorShift = cs.GetValue<bool>();
        if (node["IgnoreTtsInLoopback"] is { } it) config.IgnoreTtsInLoopback = it.GetValue<bool>();
        if (node["EnableShatterOverdrive"] is { } so) config.EnableShatterOverdrive = so.GetValue<bool>();
        if (node["EnableFullscreenOverdrive"] is { } fso) config.EnableFullscreenOverdrive = fso.GetValue<bool>();
        if (node["OverdriveThreshold"]?.GetValue<double>() is double ot) config.OverdriveThreshold = ot;
        if (node["OverdriveCooldown"]?.GetValue<double>() is double oc) config.OverdriveCooldown = oc;
        if (node["ShardCount"]?.GetValue<int>() is int sc) config.ShardCount = sc;
        if (node["OverdriveDuration"]?.GetValue<double>() is double od) config.OverdriveDuration = od;
        if (node["EnableMouseRepulsion"] is { } mr) config.EnableMouseRepulsion = mr.GetValue<bool>();
        if (node["EnableWindowCollisions"] is { } wc) config.EnableWindowCollisions = wc.GetValue<bool>();
        if (node["CustomCoreColor"]?.GetValue<string>() is string ccc) config.CustomCoreColor = ccc;
        if (node["CustomGlowColor"]?.GetValue<string>() is string cgc) config.CustomGlowColor = cgc;
        if (node["CustomRaysColor"]?.GetValue<string>() is string crc) config.CustomRaysColor = crc;
    }
}
