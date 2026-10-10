using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Gem.Handlers;
using Gem.Services;
using Gem.Voice;
using Gem.Win32;
using Microsoft.Win32;
using NAudio.Wave;

namespace Gem.UI;

public sealed partial class SettingsWindow : Window
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryName = "JARVIS";

    private readonly LlmIntentService? _llmService;
    private readonly ObservableCollection<SteamGameEntry> _steamGames = new();
    private readonly List<AudioDeviceItem> _audioDevices = new();
    private readonly DispatcherTimer _statusTimer;
    private WaveInEvent? _previewWaveIn;
    private bool _isInitializing = true;

    public SettingsWindow(LlmIntentService? llmService = null)
    {
        InitializeComponent();
        _llmService = llmService;

        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _statusTimer.Tick += OnStatusTimerTick;

        LstSteamGames.ItemsSource = _steamGames;
        LoadSettingsIntoUi();
        _isInitializing = false;
    }

    private void LoadSettingsIntoUi()
    {
        try
        {
            var config = AppSettingsService.Load();

            // 1. Audio Devices
            RefreshAudioDevicesList(config.Audio?.DeviceIndex ?? 0);

            // 2. Noise Gate RMS (default 350, range 50-600)
            double noiseGate = config.WakeWord?.NoiseGateRms ?? 350.0;
            if (noiseGate < 50 || noiseGate > 600) noiseGate = 350.0;
            SldNoiseGate.Value = noiseGate;
            TxtNoiseGateVal.Text = $"{noiseGate:F0} RMS";

            // 3. TTS Engine and Voice
            bool isEdge = config.Tts?.EnableEdgeTts ?? true;
            if (config.Tts != null && config.Tts.PreferredEngine.Equals("SAPI5", StringComparison.OrdinalIgnoreCase))
            {
                isEdge = false;
            }
            CmbTtsEngine.SelectedIndex = isEdge ? 0 : 1;
            UpdateVoiceComboBox(isEdge, config.Tts);

            // 4. LLM settings
            TxtLlmUrl.Text = config.LlmBaseUrl;
            TxtLlmModel.Text = config.LlmModel;
            TxtLlmApiKey.Text = config.LlmApiKey;

            // 5. System settings
            TxtVoskPath.Text = config.VoskModelPath;
            TxtCityName.Text = config.CityName;
            ChkAutoStart.IsChecked = IsAutoStartEnabled();

            // 6. Steam Games
            _steamGames.Clear();
            var merged = new Dictionary<string, string>(AppHandler.SteamGames, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in config.GameAliases)
            {
                merged[kv.Key] = kv.Value;
            }

            foreach (var kv in merged.OrderBy(k => k.Key))
            {
                _steamGames.Add(new SteamGameEntry(kv.Key, kv.Value));
            }

            // 7. HUD visualizer settings
            var hud = config.HudSettings ?? new HudSettingsConfig();
            SldHudGain.Value = hud.Gain;
            TxtHudGainVal.Text = $"{hud.Gain:F1}x";

            SldHudBassScale.Value = hud.BassScale;
            TxtHudBassVal.Text = $"{hud.BassScale:F2}x";

            SldHudFalloff.Value = hud.Falloff;
            TxtHudFalloffVal.Text = $"{hud.Falloff:F2}";

            int paletteIdx = 0;
            if (hud.ColorPalette.Contains("Custom", StringComparison.OrdinalIgnoreCase) ||
                hud.ColorPalette.Contains("Пользователь", StringComparison.OrdinalIgnoreCase)) paletteIdx = 3;
            else if (hud.ColorPalette.Contains("Cyberpunk", StringComparison.OrdinalIgnoreCase)) paletteIdx = 1;
            else if (hud.ColorPalette.Contains("Solar", StringComparison.OrdinalIgnoreCase)) paletteIdx = 2;
            CmbHudPalette.SelectedIndex = paletteIdx;

            ChkShatterOverdrive.IsChecked = hud.EnableShatterOverdrive;
            ChkFullscreenOverdrive.IsChecked = hud.EnableFullscreenOverdrive;
            SldOverdriveThreshold.Value = hud.OverdriveThreshold;
            TxtOverdriveThresholdVal.Text = $"{hud.OverdriveThreshold:F2}";
            SldOverdriveCooldown.Value = hud.OverdriveCooldown;
            TxtOverdriveCooldownVal.Text = $"{hud.OverdriveCooldown:F0} сек";
            SldOverdriveDuration.Value = hud.OverdriveDuration;
            TxtOverdriveDurationVal.Text = $"{hud.OverdriveDuration:F0} сек";
            SldShardCount.Value = hud.ShardCount;
            TxtShardCountVal.Text = $"{hud.ShardCount} шт";
            ChkMouseRepulsion.IsChecked = hud.EnableMouseRepulsion;
            ChkWindowCollisions.IsChecked = hud.EnableWindowCollisions;
            ChkMemoryGlitch.IsChecked = hud.EnableMemoryGlitch;

            TxtCustomCoreColor.Text = hud.CustomCoreColor ?? "#00E5FF";
            TxtCustomGlowColor.Text = hud.CustomGlowColor ?? "#0040FF";
            TxtCustomRaysColor.Text = hud.CustomRaysColor ?? "#00FFAA";
            UpdateCustomColorSwatches();
            UpdateCustomPanelVisibility();

            ChkColorShift.IsChecked = hud.EnableColorShift;
            ChkIgnoreTts.IsChecked = hud.IgnoreTtsInLoopback;

            // 8. Start audio monitoring for VU meter
            StartAudioMonitoring();
        }
        catch (Exception ex)
        {
            TxtStatus.Foreground = Brushes.OrangeRed;
            TxtStatus.Text = $"Ошибка загрузки настроек: {ex.Message}";
        }
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string requestedCity = TxtCityName.Text.Trim();
            if (string.IsNullOrWhiteSpace(requestedCity))
            {
                requestedCity = "Санкт-Петербург";
            }

            TxtStatus.Foreground = Brushes.Cyan;
            TxtStatus.Text = "Геокодинг города...";

            string finalCityName = requestedCity;
            double lat = 59.9386;
            double lon = 30.3141;

            var geoResult = await WeatherService.GeocodeCityAsync(requestedCity);
            if (geoResult.HasValue)
            {
                finalCityName = geoResult.Value.Name;
                lat = geoResult.Value.Latitude;
                lon = geoResult.Value.Longitude;
            }

            TxtCityName.Text = finalCityName;

            var currentConfig = AppSettingsService.Load();

            // Extract Audio device
            int deviceIndex = 0;
            string deviceName = string.Empty;
            if (CmbAudioDevices.SelectedItem is AudioDeviceItem devItem && devItem.Index >= 0)
            {
                deviceIndex = devItem.Index;
                deviceName = devItem.Name;
            }

            // Extract TTS settings
            bool isEdge = CmbTtsEngine.SelectedIndex == 0;
            string selectedVoiceToken = (CmbTtsVoice.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                ?? (CmbTtsVoice.SelectedItem as ComboBoxItem)?.Content?.ToString()
                ?? "Aidar";

            if (selectedVoiceToken.Contains(" - ", StringComparison.Ordinal))
            {
                selectedVoiceToken = selectedVoiceToken.Substring(0, selectedVoiceToken.IndexOf(" - ", StringComparison.Ordinal)).Trim();
            }

            var ttsSettings = new TtsConfig
            {
                PreferredEngine = isEdge ? "Edge" : "SAPI5",
                Voice = selectedVoiceToken,
                EdgeVoice = isEdge ? selectedVoiceToken : (currentConfig.Tts?.EdgeVoice ?? "ru-RU-DmitryNeural"),
                Sapi5Voice = !isEdge ? selectedVoiceToken : (currentConfig.Tts?.Sapi5Voice ?? "Aidar"),
                ConnectionTimeoutMs = currentConfig.Tts?.ConnectionTimeoutMs ?? 5000,
                EnableEdgeTts = isEdge
            };

            var hudConfig = GetCurrentHudConfigFromUi();

            var data = new AppSettingsData
            {
                Audio = new AudioConfig
                {
                    DeviceIndex = deviceIndex,
                    DeviceName = deviceName
                },
                HudSettings = hudConfig,
                WakeWord = new WakeWordConfig
                {
                    Name = currentConfig.WakeWord?.Name ?? "джарвис",
                    OnnxModelPath = currentConfig.WakeWord?.OnnxModelPath ?? "Models/WakeWord/jarvis.onnx",
                    SmallModelPath = currentConfig.WakeWord?.SmallModelPath ?? "Models/VoskSmall/vosk-model-small-ru",
                    Threshold = currentConfig.WakeWord?.Threshold ?? 0.5f,
                    NoiseGateRms = SldNoiseGate.Value,
                    MinDurationMs = currentConfig.WakeWord?.MinDurationMs ?? 150
                },
                WakeWords = currentConfig.WakeWords,
                Tts = ttsSettings,
                LlmBaseUrl = TxtLlmUrl.Text.Trim(),
                LlmModel = TxtLlmModel.Text.Trim(),
                LlmApiKey = TxtLlmApiKey.Text.Trim(),
                VoskModelPath = TxtVoskPath.Text.Trim(),
                CityName = finalCityName,
                Latitude = lat,
                Longitude = lon,
                GameAliases = _steamGames
                    .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                    .ToDictionary(g => g.Key.Trim(), g => g.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            };

            // 1. Save to appsettings.json
            AppSettingsService.Save(data);

            // 2. Apply Windows autorun
            SetAutoStart(ChkAutoStart.IsChecked == true);

            // 3. Update runtime services
            VoiceListener.Instance?.SetAudioDevice(deviceIndex);
            VoiceListener.Instance?.SetNoiseGateRms(SldNoiseGate.Value);
            _llmService?.UpdateSettings(data.LlmBaseUrl, data.LlmModel, data.LlmApiKey);
            AppHandler.SetSteamGames(data.GameAliases);
            WeatherService.Instance?.UpdateLocation(finalCityName, lat, lon);
            CompositeVoiceFeedbackService.Instance = new CompositeVoiceFeedbackService(VoiceListener.Instance, ttsSettings);
            OverlayWidget.Instance?.ApplyHudSettings(hudConfig);

            TxtStatus.Foreground = Brushes.LightGreen;
            TxtStatus.Text = "✓ Настройки успешно сохранены и применены!";
            _statusTimer.Stop();
            _statusTimer.Start();
        }
        catch (Exception ex)
        {
            TxtStatus.Foreground = Brushes.Red;
            TxtStatus.Text = $"Ошибка сохранения: {ex.Message}";
            _statusTimer.Stop();
            _statusTimer.Start();
        }
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        TxtStatus.Text = string.Empty;
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            return key?.GetValue(AppRegistryName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key == null) return;

            if (enable)
            {
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppRegistryName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(AppRegistryName, false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SettingsWindow] Ошибка автозапуска: {ex.Message}");
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.EnableImmersiveDarkMode(hwnd);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SettingsWindow Warning] DWM Immersive Dark Mode: {ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        StopAudioMonitoring();
        LstSteamGames.ItemsSource = null;
        base.OnClosed(e);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
