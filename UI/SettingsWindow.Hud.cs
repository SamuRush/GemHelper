using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Gem.UI;

public sealed partial class SettingsWindow
{
    private void HudSetting_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (TxtHudGainVal != null && SldHudGain != null)
            TxtHudGainVal.Text = $"{SldHudGain.Value:F1}x";
        if (TxtHudBassVal != null && SldHudBassScale != null)
            TxtHudBassVal.Text = $"{SldHudBassScale.Value:F2}x";
        if (TxtHudFalloffVal != null && SldHudFalloff != null)
            TxtHudFalloffVal.Text = $"{SldHudFalloff.Value:F2}";
        if (TxtOverdriveThresholdVal != null && SldOverdriveThreshold != null)
            TxtOverdriveThresholdVal.Text = $"{SldOverdriveThreshold.Value:F2}";
        if (TxtOverdriveCooldownVal != null && SldOverdriveCooldown != null)
            TxtOverdriveCooldownVal.Text = $"{SldOverdriveCooldown.Value:F0} сек";
        if (TxtOverdriveDurationVal != null && SldOverdriveDuration != null)
            TxtOverdriveDurationVal.Text = $"{SldOverdriveDuration.Value:F0} сек";
        if (TxtShardCountVal != null && SldShardCount != null)
            TxtShardCountVal.Text = $"{SldShardCount.Value:F0} шт";

        UpdateCustomPanelVisibility();

        if (_isInitializing) return;

        ApplyCurrentHudSettingsToLiveWidget();
    }

    private void TxtCustomColor_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCustomColorSwatches();

        if (_isInitializing) return;

        ApplyCurrentHudSettingsToLiveWidget();
    }

    private void UpdateCustomPanelVisibility()
    {
        if (PnlCustomColors == null || CmbHudPalette == null) return;
        bool isCustom = CmbHudPalette.SelectedIndex == 3;
        PnlCustomColors.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCustomColorSwatches()
    {
        if (BrdCustomCoreColor != null && TxtCustomCoreColor != null)
        {
            var col = ColorMath.ParseHex(TxtCustomCoreColor.Text, Color.FromRgb(0x00, 0xE5, 0xFF));
            BrdCustomCoreColor.Background = new SolidColorBrush(col);
        }
        if (BrdCustomGlowColor != null && TxtCustomGlowColor != null)
        {
            var col = ColorMath.ParseHex(TxtCustomGlowColor.Text, Color.FromRgb(0x00, 0x40, 0xFF));
            BrdCustomGlowColor.Background = new SolidColorBrush(col);
        }
        if (BrdCustomRaysColor != null && TxtCustomRaysColor != null)
        {
            var col = ColorMath.ParseHex(TxtCustomRaysColor.Text, Color.FromRgb(0x00, 0xFF, 0xAA));
            BrdCustomRaysColor.Background = new SolidColorBrush(col);
        }
    }

    private void ApplyCurrentHudSettingsToLiveWidget()
    {
        var config = GetCurrentHudConfigFromUi();
        OverlayWidget.Instance?.ApplyHudSettings(config);
    }

    private HudSettingsConfig GetCurrentHudConfigFromUi()
    {
        string palette = (CmbHudPalette?.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? "Arc Reactor (Циан / Электрик)";

        return new HudSettingsConfig
        {
            Gain = SldHudGain?.Value ?? 3.5,
            BassScale = SldHudBassScale?.Value ?? 1.4,
            Falloff = SldHudFalloff?.Value ?? 0.88,
            ColorPalette = palette,
            EnableColorShift = ChkColorShift?.IsChecked == true,
            IgnoreTtsInLoopback = ChkIgnoreTts?.IsChecked == true,
            EnableShatterOverdrive = ChkShatterOverdrive?.IsChecked == true,
            EnableFullscreenOverdrive = ChkFullscreenOverdrive?.IsChecked == true,
            OverdriveThreshold = SldOverdriveThreshold?.Value ?? 0.85,
            OverdriveCooldown = SldOverdriveCooldown?.Value ?? 10.0,
            ShardCount = (int)(SldShardCount?.Value ?? 48),
            OverdriveDuration = SldOverdriveDuration?.Value ?? 10.0,
            EnableMouseRepulsion = ChkMouseRepulsion?.IsChecked == true,
            EnableWindowCollisions = ChkWindowCollisions?.IsChecked == true,
            EnableMemoryGlitch = ChkMemoryGlitch?.IsChecked == true,
            CustomCoreColor = TxtCustomCoreColor?.Text?.Trim() ?? "#00E5FF",
            CustomGlowColor = TxtCustomGlowColor?.Text?.Trim() ?? "#0040FF",
            CustomRaysColor = TxtCustomRaysColor?.Text?.Trim() ?? "#00FFAA"
        };
    }
}
