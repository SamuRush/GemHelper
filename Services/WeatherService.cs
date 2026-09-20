using System.Globalization;
using System.Text.Json;

namespace Gem.Services;

/// <summary>
/// Service providing weather forecasts and city geocoding via the free Open-Meteo API.
/// </summary>
public sealed class WeatherService
{
    private static readonly HttpClient SharedGeocodeHttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly HttpClient _httpClient;
    private string _cityName;
    private double _latitude;
    private double _longitude;

    public static WeatherService? Instance { get; set; }

    public string CityName => _cityName;
    public double Latitude => _latitude;
    public double Longitude => _longitude;

    public WeatherService(
        string cityName = "Санкт-Петербург",
        double latitude = 59.9386,
        double longitude = 30.3141,
        HttpClient? httpClient = null)
    {
        _cityName = string.IsNullOrWhiteSpace(cityName) ? "Санкт-Петербург" : cityName;
        _latitude = latitude;
        _longitude = longitude;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        Instance = this;
    }

    /// <summary>
    /// Updates current location coordinates and canonical city name.
    /// </summary>
    public void UpdateLocation(string cityName, double latitude, double longitude)
    {
        if (!string.IsNullOrWhiteSpace(cityName))
        {
            _cityName = cityName.Trim();
        }
        _latitude = latitude;
        _longitude = longitude;
    }

    /// <summary>
    /// Geocodes a city name to its canonical name and coordinates using Open-Meteo Geocoding API.
    /// Returns null if the city could not be found or on network error.
    /// </summary>
    public static async Task<(string Name, double Latitude, double Longitude)?> GeocodeCityAsync(
        string cityName,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(cityName))
        {
            return null;
        }

        var client = httpClient ?? SharedGeocodeHttpClient;
        try
        {
            string escapedCity = Uri.EscapeDataString(cityName.Trim());
            string url = $"https://geocoding-api.open-meteo.com/v1/search?name={escapedCity}&count=1&language=ru";

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array &&
                results.GetArrayLength() > 0)
            {
                var first = results[0];
                string canonicalName = first.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString() ?? cityName.Trim()
                    : cityName.Trim();

                double lat = first.TryGetProperty("latitude", out var latProp) ? latProp.GetDouble() : 59.9386;
                double lon = first.TryGetProperty("longitude", out var lonProp) ? lonProp.GetDouble() : 30.3141;

                return (canonicalName, lat, lon);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[WeatherService Geocoding Warning] {ex.Message}");
            Console.ResetColor();
        }

        return null;
    }

    /// <summary>
    /// Fetches current weather data from Open-Meteo and formats a butler-style report.
    /// If targetCity is provided, resolves coordinates via Open-Meteo geocoding API.
    /// Format: "В {City} сейчас {temp}°C, {описание погоды}, ветер {wind} м/с, сэр."
    /// </summary>
    public async Task<string> GetCurrentWeatherReportAsync(string? targetCity = null)
    {
        string resolvedCityName = _cityName;
        double resolvedLat = _latitude;
        double resolvedLon = _longitude;

        if (!string.IsNullOrWhiteSpace(targetCity))
        {
            string cleanCity = targetCity.Trim();
            if (cleanCity.StartsWith("в ", StringComparison.OrdinalIgnoreCase)) cleanCity = cleanCity[2..].Trim();
            if (cleanCity.StartsWith("во ", StringComparison.OrdinalIgnoreCase)) cleanCity = cleanCity[3..].Trim();

            var geoResult = await GeocodeCityAsync(cleanCity, _httpClient);
            // Resilient fallback for inflected Russian city names (e.g. "Москве" -> "Москва", "Париже" -> "Париж")
            if (geoResult == null && cleanCity.EndsWith("е", StringComparison.OrdinalIgnoreCase) && cleanCity.Length > 3)
            {
                geoResult = await GeocodeCityAsync(cleanCity[..^1] + "а", _httpClient)
                         ?? await GeocodeCityAsync(cleanCity[..^1], _httpClient);
            }

            if (geoResult == null)
            {
                return $"К сожалению, мне не удалось найти информацию о погоде для города {targetCity}, сэр.";
            }

            resolvedCityName = geoResult.Value.Name;
            resolvedLat = geoResult.Value.Latitude;
            resolvedLon = geoResult.Value.Longitude;
        }

        try
        {
            string latStr = resolvedLat.ToString(CultureInfo.InvariantCulture);
            string lonStr = resolvedLon.ToString(CultureInfo.InvariantCulture);

            string url = $"https://api.open-meteo.com/v1/forecast?latitude={latStr}&longitude={lonStr}&current=temperature_2m,apparent_temperature,precipitation,weather_code,wind_speed_10m&wind_speed_unit=ms&timezone=auto";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return $"К сожалению, сервис погоды временно недоступен, сэр. (HTTP {(int)response.StatusCode})";
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("current", out var current))
            {
                return "Не удалось получить текущие метеоданные, сэр.";
            }

            double temp = current.TryGetProperty("temperature_2m", out var tempProp) ? tempProp.GetDouble() : 0.0;
            int weatherCode = current.TryGetProperty("weather_code", out var codeProp) ? codeProp.GetInt32() : 0;
            double windSpeed = current.TryGetProperty("wind_speed_10m", out var windProp) ? windProp.GetDouble() : 0.0;

            string weatherDesc = GetWmoCodeDescription(weatherCode);
            int roundedTemp = (int)Math.Round(temp, MidpointRounding.AwayFromZero);
            string windFormatted = windSpeed.ToString("0.#", CultureInfo.InvariantCulture);

            return $"В {resolvedCityName} сейчас {roundedTemp}°C, {weatherDesc}, ветер {windFormatted} м/с, сэр.";
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[WeatherService Error] {ex.Message}");
            Console.ResetColor();
            return "Не удалось получить данные о погоде из-за ошибки сети, сэр.";
        }
    }

    /// <summary>
    /// Converts WMO Weather interpretation code into human-friendly Russian description.
    /// </summary>
    public static string GetWmoCodeDescription(int code) => code switch
    {
        0 => "ясно",
        1 => "преимущественно ясно",
        2 => "переменная облачность",
        3 => "пасмурно",
        45 => "туман",
        48 => "изморозь и туман",
        51 => "легкая морось",
        53 => "умеренная морось",
        55 => "плотная морось",
        56 => "ледяная морось",
        57 => "плотная ледяная морось",
        61 => "небольшой дождь",
        63 => "дождь",
        65 => "сильный дождь",
        66 => "ледяной дождь",
        67 => "сильный ледяной дождь",
        71 => "небольшой снегопад",
        73 => "снегопад",
        75 => "сильный снегопад",
        77 => "снежные зерна",
        80 => "небольшой ливень",
        81 => "ливень",
        82 => "сильный ливень",
        85 => "слабый снежный ливень",
        86 => "сильный снежный ливень",
        95 => "гроза",
        96 => "гроза с небольшим градом",
        99 => "гроза с сильным градом",
        _ => "переменная облачность"
    };
}
