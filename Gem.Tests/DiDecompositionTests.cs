using System;
using System.IO;
using System.Threading.Tasks;
using Gem.Core;
using Gem.Handlers;
using Gem.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gem.Tests;

public class DiDecompositionTests
{
    [Fact]
    public void ServiceCollectionExtensions_RegistersCoreHandlersAndTts()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddJarvisCore(configuration);
        services.AddJarvisTts(configuration);
        services.AddJarvisHandlers();

        using var serviceProvider = services.BuildServiceProvider();

        Assert.NotNull(serviceProvider.GetService<IVoiceFeedbackService>());
        Assert.NotNull(serviceProvider.GetService<CommandRouter>());
        Assert.NotNull(serviceProvider.GetService<ITimerService>());
        Assert.NotNull(serviceProvider.GetService<WeatherService>());
    }

    [Fact]
    public async Task CliRunner_WithNonCliArgs_ReturnsFalse()
    {
        bool handled = await CliRunner.RunCliCommandsAsync(["--some-unrelated-arg"]);
        Assert.False(handled);
    }

    [Fact]
    public void Handlers_SupportConstructorInjection_WithoutSingletonInstances()
    {
        var timerService = new TimerService();
        var timerHandler = new TimerCommandHandler(timerService);
        Assert.Equal("timer", timerHandler.CommandName);

        var weatherService = new WeatherService();
        var feedbackService = new CompositeVoiceFeedbackService();
        var weatherHandler = new WeatherCommandHandler(weatherService, feedbackService);
        Assert.Equal("weather", weatherHandler.CommandName);

        var appHandler = new AppControlCommandHandler(feedbackService, SteamService.Instance, JarvisOrchestrator.Instance);
        Assert.Equal("app", appHandler.CommandName);
    }

    [Fact]
    public void ProgramCs_FileSize_MustNotExceed20Kb()
    {
        // Technical requirement 4: Program.cs must be decomposed to <= 20 KB
        string solutionDir = AppContext.BaseDirectory;
        // Search up for Program.cs in solution
        string current = solutionDir;
        string? programPath = null;
        for (int i = 0; i < 5; i++)
        {
            string candidate = Path.Combine(current, "Program.cs");
            if (File.Exists(candidate))
            {
                programPath = candidate;
                break;
            }
            string candidateParent = Path.Combine(current, "..", "Program.cs");
            if (File.Exists(candidateParent))
            {
                programPath = Path.GetFullPath(candidateParent);
                break;
            }
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        Assert.True(programPath != null && File.Exists(programPath), "Program.cs must exist");
        var fileInfo = new FileInfo(programPath);
        Assert.True(fileInfo.Length <= 20 * 1024,
            $"Program.cs size ({fileInfo.Length} bytes) exceeds 20 KB limit ({20 * 1024} bytes)");
    }
}
