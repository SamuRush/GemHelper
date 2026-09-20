using System.Text.Json;
using Gem.Core;
using Gem.Services;

namespace Gem.Handlers;

/// <summary>
/// Command handler for executing weather queries via Open-Meteo API.
/// </summary>
public sealed class WeatherCommandHandler : ICommandHandler
{
    public string CommandName => "weather";

    private readonly WeatherService? _weatherService;
    private readonly IVoiceFeedbackService? _voiceFeedback;

    public WeatherCommandHandler(
        WeatherService? weatherService = null,
        IVoiceFeedbackService? voiceFeedback = null)
    {
        _weatherService = weatherService;
        _voiceFeedback = voiceFeedback;
    }

    public async Task<CommandResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken = default)
    {
        var weather = _weatherService ?? WeatherService.Instance ?? new WeatherService();
        string? targetCity = null;
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("city", out var cityProp) &&
            cityProp.ValueKind == JsonValueKind.String)
        {
            targetCity = cityProp.GetString();
        }

        string report = await weather.GetCurrentWeatherReportAsync(targetCity);

        var feedback = _voiceFeedback ?? CompositeVoiceFeedbackService.Instance;
        if (feedback != null)
        {
            await feedback.SpeakAsync(report, cancellationToken);
        }

        return CommandResult.Ok(report, new { Report = report, City = targetCity ?? weather.CityName });
    }
}
