using System;
using Gem.Core;
using Gem.Handlers;
using Gem.Services;
using Gem.Voice;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gem.Services;

/// <summary>
/// Dependency Injection service collection extensions for JARVIS.
/// Decouples core, handlers, and TTS registration from Program.cs.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers core services (Configuration, AppSettings, Weather, Steam, Orchestrator, Timers, LLM, Router).
    /// </summary>
    public static IServiceCollection AddJarvisCore(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var config = configuration ?? new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        services.AddSingleton(config);

        var settingsData = AppSettingsService.Load();
        services.AddSingleton(settingsData);

        services.AddSingleton(sp => new WeatherService(
            cityName: settingsData.CityName,
            latitude: settingsData.Latitude,
            longitude: settingsData.Longitude
        ));

        services.AddSingleton(SteamService.Instance);
        services.AddSingleton(JarvisOrchestrator.Instance);

        services.AddSingleton<ITimerService>(sp =>
        {
            var feedback = sp.GetService<IVoiceFeedbackService>();
            return new TimerService(feedback);
        });

        services.AddSingleton(sp => new LlmIntentService(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<CommandRouter>();

        return services;
    }

    /// <summary>
    /// Registers Text-to-Speech feedback services (Edge-TTS, SAPI5, and Composite fallback orchestrator).
    /// </summary>
    public static IServiceCollection AddJarvisTts(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddSingleton<IVoiceFeedbackService>(sp =>
        {
            var config = configuration ?? sp.GetService<IConfiguration>();
            var listener = sp.GetService<VoiceListener>() ?? VoiceListener.Instance;
            return new CompositeVoiceFeedbackService(listener, config);
        });

        return services;
    }

    /// <summary>
    /// Registers all command handlers (Volume, App, Hotkey, Media, Weather, System, Timer).
    /// </summary>
    public static IServiceCollection AddJarvisHandlers(this IServiceCollection services)
    {
        services.AddSingleton<VolumeCommandHandler>();
        services.AddSingleton<AppControlCommandHandler>(sp =>
        {
            var feedback = sp.GetService<IVoiceFeedbackService>();
            var steam = sp.GetService<SteamService>() ?? SteamService.Instance;
            var orchestrator = sp.GetService<JarvisOrchestrator>() ?? JarvisOrchestrator.Instance;
            return new AppControlCommandHandler(feedback, steam, orchestrator);
        });
        services.AddSingleton<HotkeyCommandHandler>();
        services.AddSingleton<MediaCommandHandler>();
        services.AddSingleton<WeatherCommandHandler>(sp =>
        {
            var weather = sp.GetService<WeatherService>() ?? new WeatherService();
            var feedback = sp.GetService<IVoiceFeedbackService>();
            return new WeatherCommandHandler(weather, feedback);
        });
        services.AddSingleton<SystemCommandHandler>(sp =>
        {
            var feedback = sp.GetService<IVoiceFeedbackService>();
            return new SystemCommandHandler(feedback);
        });
        services.AddSingleton<TimerCommandHandler>(sp =>
        {
            var timerService = sp.GetService<ITimerService>() ?? new TimerService();
            return new TimerCommandHandler(timerService);
        });

        return services;
    }

    /// <summary>
    /// Populates and configures the CommandRouter with all registered handlers from DI.
    /// </summary>
    public static CommandRouter ConfigureJarvisRouter(this IServiceProvider sp)
    {
        var router = sp.GetRequiredService<CommandRouter>();
        router.Register(sp.GetRequiredService<VolumeCommandHandler>())
              .Register(sp.GetRequiredService<AppControlCommandHandler>())
              .Register(sp.GetRequiredService<HotkeyCommandHandler>())
              .Register(sp.GetRequiredService<MediaCommandHandler>())
              .Register(sp.GetRequiredService<WeatherCommandHandler>())
              .Register(sp.GetRequiredService<SystemCommandHandler>())
              .Register(sp.GetRequiredService<TimerCommandHandler>());
        return router;
    }
}
