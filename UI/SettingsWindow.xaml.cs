using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace Gem.UI;

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
    private readonly LlmIntentService? _llmService;
    private readonly ObservableCollection<SteamGameEntry> _steamGames = new();
    private readonly DispatcherTimer _statusTimer;

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
    }

    private void LoadSettingsIntoUi()
    {
        try
        {
            var config = AppSettingsService.Load();

            TxtLlmUrl.Text = config.LlmBaseUrl;
            TxtLlmModel.Text = config.LlmModel;
            TxtLlmApiKey.Text = config.LlmApiKey;
            TxtVoskPath.Text = config.VoskModelPath;
            TxtCityName.Text = config.CityName;

            _steamGames.Clear();

            // Merge AppHandler.SteamGames and config.GameAliases
            var merged = new Dictionary<string, string>(AppHandler.SteamGames, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in config.GameAliases)
            {
                merged[kv.Key] = kv.Value;
            }

            foreach (var kv in merged.OrderBy(k => k.Key))
            {
                _steamGames.Add(new SteamGameEntry(kv.Key, kv.Value));
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            TxtStatus.Text = $"Ошибка загрузки настроек: {ex.Message}";
        }
    }

    private void BtnBrowseVosk_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Выберите папку с моделью Vosk",
                InitialDirectory = AppContext.BaseDirectory
            };

            if (dialog.ShowDialog() == true)
            {
                TxtVoskPath.Text = dialog.FolderName;
            }
        }
        catch
        {
            // If OpenFolderDialog is not available on legacy shell, fallback to manual input
        }
    }

    private void BtnAddGame_Click(object sender, RoutedEventArgs e)
    {
        string key = TxtNewGameKey.Text.Trim();
        string appId = TxtNewGameAppId.Text.Trim();

        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(appId))
        {
            TxtStatus.Foreground = System.Windows.Media.Brushes.Yellow;
            TxtStatus.Text = "Укажите ключ команды и AppID игры.";
            _statusTimer.Start();
            return;
        }

        // Check if key already exists
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

            TxtStatus.Foreground = System.Windows.Media.Brushes.Cyan;
            TxtStatus.Text = "Геокодинг города...";

            // Geocode city via Open-Meteo
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
            var data = new AppSettingsData
            {
                WakeWords = currentConfig.WakeWords,
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

            // 1. Save to appsettings.json file
            AppSettingsService.Save(data);

            // 2. Update active runtime services on the fly
            _llmService?.UpdateSettings(data.LlmBaseUrl, data.LlmModel, data.LlmApiKey);
            AppHandler.SetSteamGames(data.GameAliases);
            WeatherService.Instance?.UpdateLocation(finalCityName, lat, lon);

            TxtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            TxtStatus.Text = "✓ Настройки успешно сохранены и применены!";
            _statusTimer.Stop();
            _statusTimer.Start();
        }
        catch (Exception ex)
        {
            TxtStatus.Foreground = System.Windows.Media.Brushes.Red;
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

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        LstSteamGames.ItemsSource = null;
        base.OnClosed(e);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
