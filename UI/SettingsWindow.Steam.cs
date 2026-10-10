using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Gem.UI;

public sealed partial class SettingsWindow
{
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
}
