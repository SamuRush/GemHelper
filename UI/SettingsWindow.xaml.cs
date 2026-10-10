using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

public sealed class AudioDeviceItem
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;

    public AudioDeviceItem() { }

    public AudioDeviceItem(int index, string name)
    {
        Index = index;
        Name = name;
    }

    public override string ToString() => $"[{Index}] {Name}";
}

public sealed class SteamGameEntry
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public SteamGameEntry() { }

    public SteamGameEntry(string key, string value)
    {
        Key = key;
        Value = value;
    }
}

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

            string selectedVoice = config.Tts?.Voice ?? config.Tts?.Sapi5Voice ?? "Aidar";
            SelectTtsVoiceInComboBox(selectedVoice);

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

            // 7. Start audio monitoring for VU meter
            StartAudioMonitoring();
        }
        catch (Exception ex)
        {
            TxtStatus.Foreground = Brushes.OrangeRed;
            TxtStatus.Text = $"Ошибка загрузки настроек: {ex.Message}";
        }
    }

    private void RefreshAudioDevicesList(int preferredIndex = 0)
    {
        _audioDevices.Clear();
        CmbAudioDevices.ItemsSource = null;

        int count = WaveInEvent.DeviceCount;
        if (count == 0)
        {
            _audioDevices.Add(new AudioDeviceItem(-1, "Микрофоны не обнаружены"));
            CmbAudioDevices.ItemsSource = _audioDevices;
            CmbAudioDevices.SelectedIndex = 0;
            return;
        }

        for (int i = 0; i < count; i++)
        {
            try
            {
                var caps = WaveInEvent.GetCapabilities(i);
                _audioDevices.Add(new AudioDeviceItem(i, caps.ProductName));
            }
            catch
            {
                _audioDevices.Add(new AudioDeviceItem(i, $"Устройство #{i}"));
            }
        }

        CmbAudioDevices.ItemsSource = _audioDevices;

        var selected = _audioDevices.FirstOrDefault(d => d.Index == preferredIndex) ?? _audioDevices.FirstOrDefault();
        if (selected != null)
        {
            CmbAudioDevices.SelectedItem = selected;
        }
    }

    private void SelectTtsVoiceInComboBox(string voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            CmbTtsVoice.SelectedIndex = 0;
            return;
        }

        for (int i = 0; i < CmbTtsVoice.Items.Count; i++)
        {
            if (CmbTtsVoice.Items[i] is ComboBoxItem item &&
                item.Content?.ToString()?.Contains(voiceName, StringComparison.OrdinalIgnoreCase) == true)
            {
                CmbTtsVoice.SelectedIndex = i;
                return;
            }
        }

        CmbTtsVoice.SelectedIndex = 0;
    }

    private void StartAudioMonitoring()
    {
        StopAudioMonitoring();

        // If VoiceListener is running, bind to its event
        if (VoiceListener.Instance != null && VoiceListener.Instance.IsRunning)
        {
            VoiceListener.Instance.OnRmsLevelChanged += OnRmsLevelChanged;
            return;
        }

        // Fallback: start a local diagnostic capture monitor on the chosen device
        try
        {
            if (CmbAudioDevices.SelectedItem is AudioDeviceItem item && item.Index >= 0 && item.Index < WaveInEvent.DeviceCount)
            {
                _previewWaveIn = new WaveInEvent
                {
                    DeviceNumber = item.Index,
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 40,
                    NumberOfBuffers = 2
                };
                _previewWaveIn.DataAvailable += (s, e) =>
                {
                    double rms = VoiceListener.CalculateRms(e.Buffer, e.BytesRecorded);
                    OnRmsLevelChanged((float)rms);
                };
                _previewWaveIn.StartRecording();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SettingsWindow] Ошибка тест-мониторинга микрофона: {ex.Message}");
        }
    }

    private void StopAudioMonitoring()
    {
        if (VoiceListener.Instance != null)
        {
            VoiceListener.Instance.OnRmsLevelChanged -= OnRmsLevelChanged;
        }

        if (_previewWaveIn != null)
        {
            try { _previewWaveIn.StopRecording(); } catch { }
            _previewWaveIn.Dispose();
            _previewWaveIn = null;
        }
    }

    private void OnRmsLevelChanged(float rms)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnRmsLevelChanged(rms));
            return;
        }

        PrgAudioLevel.Value = Math.Clamp(rms, 0, 600);
        TxtVuLevel.Text = $"Уровень: {rms:F0} RMS";

        double threshold = SldNoiseGate.Value;
        if (rms >= threshold)
        {
            TxtVuStatus.Text = "Статус: 🎙 СИГНАЛ ОБНАРУЖЕН (РЕЧЬ)";
            TxtVuStatus.Foreground = Brushes.SpringGreen;
        }
        else
        {
            TxtVuStatus.Text = "Статус: 🔇 Фоновый шум / Тишина";
            TxtVuStatus.Foreground = Brushes.SlateGray;
        }
    }

    private void CmbAudioDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (CmbAudioDevices.SelectedItem is AudioDeviceItem item && item.Index >= 0)
        {
            if (VoiceListener.Instance != null && VoiceListener.Instance.IsRunning)
            {
                VoiceListener.Instance.SetAudioDevice(item.Index);
            }
            else
            {
                StartAudioMonitoring();
            }
        }
    }

    private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        int currentIndex = (CmbAudioDevices.SelectedItem as AudioDeviceItem)?.Index ?? 0;
        RefreshAudioDevicesList(currentIndex);
        StartAudioMonitoring();
    }

    private void SldNoiseGate_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtNoiseGateVal != null)
        {
            TxtNoiseGateVal.Text = $"{e.NewValue:F0} RMS";
        }

        if (!_isInitializing && VoiceListener.Instance != null)
        {
            VoiceListener.Instance.SetNoiseGateRms(e.NewValue);
        }
    }

    private void CmbTtsEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Helpful automatic hint or adaptation when changing engines
    }

    private async void BtnTestVoice_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnTestVoice.IsEnabled = false;
            string selectedVoice = (CmbTtsVoice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Aidar";

            if (CompositeVoiceFeedbackService.Instance != null)
            {
                await CompositeVoiceFeedbackService.Instance.SpeakAsync("Тест звуковой подсистемы. Джарвис готов к работе.");
            }
            else
            {
                using var sapi = new SystemSpeechTtsEngine(selectedVoice);
                await sapi.SpeakAsync("Тест синтеза речи. Система функционирует нормально.");
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Foreground = Brushes.OrangeRed;
            TxtStatus.Text = $"Ошибка теста речи: {ex.Message}";
            _statusTimer.Start();
        }
        finally
        {
            BtnTestVoice.IsEnabled = true;
        }
    }

    private async void BtnTestLlm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnTestLlm.IsEnabled = false;
            TxtLlmStatus.Foreground = Brushes.Cyan;
            TxtLlmStatus.Text = "Проверка соединения...";

            string url = TxtLlmUrl.Text.Trim().TrimEnd('/');
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2.5) };

            string modelsUrl = url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? $"{url}/models" : $"{url}/v1/models";
            var response = await client.GetAsync(modelsUrl);

            if (response.IsSuccessStatusCode)
            {
                TxtLlmStatus.Foreground = Brushes.LightGreen;
                TxtLlmStatus.Text = "✓ Подключение к LM Studio успешно установлено!";
            }
            else
            {
                TxtLlmStatus.Foreground = Brushes.Yellow;
                TxtLlmStatus.Text = $"Ответ сервера: {(int)response.StatusCode} {response.ReasonPhrase}";
            }
        }
        catch (Exception ex)
        {
            TxtLlmStatus.Foreground = Brushes.OrangeRed;
            TxtLlmStatus.Text = $"⚠ Сервер недоступен: {ex.Message}";
        }
        finally
        {
            BtnTestLlm.IsEnabled = true;
        }
    }

    private void BtnBrowseVosk_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Выберите каталог с моделью Vosk",
                InitialDirectory = AppContext.BaseDirectory
            };

            if (dialog.ShowDialog() == true)
            {
                TxtVoskPath.Text = dialog.FolderName;
            }
        }
        catch
        {
            // Fallback for environments where OpenFolderDialog is not available
        }
    }

    private void BtnAddGame_Click(object sender, RoutedEventArgs e)
    {
        string key = TxtNewGameKey.Text.Trim();
        string appId = TxtNewGameAppId.Text.Trim();

        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(appId))
        {
            TxtStatus.Foreground = Brushes.Yellow;
            TxtStatus.Text = "Укажите ключ команды и AppID игры.";
            _statusTimer.Start();
            return;
        }

        var existing = _steamGames.FirstOrDefault(g => g.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Value = appId;
            LstSteamGames.Items.Refresh();
        }
        else
        {
            _steamGames.Add(new SteamGameEntry(key, appId));
        }

        TxtNewGameKey.Text = string.Empty;
        TxtNewGameAppId.Text = string.Empty;
    }

    private void BtnRemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (LstSteamGames.SelectedItem is SteamGameEntry selected)
        {
            _steamGames.Remove(selected);
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
            string selectedVoiceString = (CmbTtsVoice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Aidar";
            string voiceToken = "Aidar";
            if (selectedVoiceString.Contains("Pavel", StringComparison.OrdinalIgnoreCase)) voiceToken = "Microsoft Pavel";
            else if (selectedVoiceString.Contains("Aidar", StringComparison.OrdinalIgnoreCase)) voiceToken = "Aidar";
            else if (selectedVoiceString.Contains("Dmitry", StringComparison.OrdinalIgnoreCase)) voiceToken = "ru-RU-DmitryNeural";
            else if (selectedVoiceString.Contains("Svetlana", StringComparison.OrdinalIgnoreCase)) voiceToken = "ru-RU-SvetlanaNeural";

            var data = new AppSettingsData
            {
                Audio = new AudioConfig
                {
                    DeviceIndex = deviceIndex,
                    DeviceName = deviceName
                },
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
                Tts = new TtsConfig
                {
                    PreferredEngine = isEdge ? "Edge" : "SAPI5",
                    Voice = voiceToken,
                    EdgeVoice = voiceToken.StartsWith("ru-RU", StringComparison.OrdinalIgnoreCase) ? voiceToken : (currentConfig.Tts?.EdgeVoice ?? "ru-RU-DmitryNeural"),
                    Sapi5Voice = voiceToken.StartsWith("ru-RU", StringComparison.OrdinalIgnoreCase) ? "Aidar" : voiceToken,
                    ConnectionTimeoutMs = currentConfig.Tts?.ConnectionTimeoutMs ?? 5000,
                    EnableEdgeTts = isEdge
                },
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
