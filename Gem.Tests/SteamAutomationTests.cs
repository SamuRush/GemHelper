using System;
using System.Drawing;
using Gem.Services;
using Xunit;

namespace Gem.Tests;

public class SteamAutomationTests
{
    [Theory]
    [InlineData("Удалить", true)]
    [InlineData("Uninstall", true)]
    [InlineData("Установить", true)]
    [InlineData("Install", true)]
    [InlineData("  УДАЛИТЬ  ", true)]
    [InlineData("install game", true)]
    [InlineData("Отмена", false)]
    [InlineData("Cancel", false)]
    [InlineData("Закрыть", false)]
    [InlineData("", false)]
    public void IsMatchingButtonName_ValidatesTargetButtons(string buttonName, bool expected)
    {
        bool result = SteamService.IsMatchingButtonName(buttonName);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Удаление - ASTRONEER", "ASTRONEER", true)]
    [InlineData("Uninstall No Man's Sky", "No Man's Sky", true)]
    [InlineData("Установка - Cyberpunk 2077", "Cyberpunk 2077", true)]
    [InlineData("Steam News", "ASTRONEER", false)]
    [InlineData("Библиотека Steam", null, false)]
    [InlineData("ASTRONEER", "ASTRONEER", true)]
    public void IsMatchingDialogTitle_MatchesKeywordsAndGameTitle(string windowTitle, string? gameTitle, bool expected)
    {
        bool result = SteamService.IsMatchingDialogTitle(windowTitle, gameTitle);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindSteamButtonViaUiAutomation_WithZeroHwnd_ReturnsNullGracefully()
    {
        Point? result = SteamService.FindSteamButtonViaUiAutomation(IntPtr.Zero);
        Assert.Null(result);
    }

    [Fact]
    public void FindSteamBlueButtonInWindow_MethodExists_AndHandlesZeroHwnd()
    {
        // Technical requirement 2: FindSteamBlueButtonInWindow exists as pixel fallback
        Point? result = SteamService.FindSteamBlueButtonInWindow(IntPtr.Zero);
        Assert.Null(result);
    }
}
