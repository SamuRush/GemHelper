using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Gem.Handlers;
using Gem.Services;
using Microsoft.Win32;

namespace Gem.UI;

public sealed partial class SettingsWindow
{
    private void SelectTtsVoiceInComboBox(string voiceName)
    {
        if (CmbTtsVoice.Items.Count == 0) return;

        if (!string.IsNullOrWhiteSpace(voiceName))
        {
            for (int i = 0; i < CmbTtsVoice.Items.Count; i++)
            {
                if (CmbTtsVoice.Items[i] is ComboBoxItem item)
                {
                    string tag = item.Tag?.ToString() ?? string.Empty;
                    string content = item.Content?.ToString() ?? string.Empty;

                    if (tag.Equals(voiceName, StringComparison.OrdinalIgnoreCase) ||
                        tag.Contains(voiceName, StringComparison.OrdinalIgnoreCase) ||
                        voiceName.Contains(tag, StringComparison.OrdinalIgnoreCase) ||
                        content.Contains(voiceName, StringComparison.OrdinalIgnoreCase))
                    {
                        CmbTtsVoice.SelectedIndex = i;
                        return;
                    }
                }
            }
        }

        CmbTtsVoice.SelectedIndex = 0;
    }

    private void UpdateVoiceComboBox(bool isEdge, TtsConfig? ttsConfig)
    {
        CmbTtsVoice.Items.Clear();

        if (isEdge)
        {
            CmbTtsVoice.Items.Add(new ComboBoxItem
            {
                Content = "ru-RU-DmitryNeural (Мужской)",
                Tag = "ru-RU-DmitryNeural"
            });
            CmbTtsVoice.Items.Add(new ComboBoxItem
            {
                Content = "ru-RU-SvetlanaNeural (Женский)",
                Tag = "ru-RU-SvetlanaNeural"
            });

            string activeVoice = ttsConfig?.EdgeVoice ?? (ttsConfig?.Voice is { } v && v.StartsWith("ru-RU", StringComparison.OrdinalIgnoreCase) ? v : "ru-RU-DmitryNeural");
            SelectTtsVoiceInComboBox(activeVoice);
        }
        else
        {
            var sapiVoices = GetInstalledSapi5Voices();
            foreach (var (voiceName, description) in sapiVoices)
            {
                CmbTtsVoice.Items.Add(new ComboBoxItem
                {
                    Content = description,
                    Tag = voiceName
                });
            }

            string activeVoice = ttsConfig?.Sapi5Voice ?? (ttsConfig?.Voice is { } v && !v.StartsWith("ru-RU", StringComparison.OrdinalIgnoreCase) ? v : "Aidar");
            SelectTtsVoiceInComboBox(activeVoice);
        }
    }

    public static List<(string Name, string Description)> GetInstalledSapi5Voices()
    {
        var list = new List<(string Name, string Description)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Through System.Speech.Synthesis.SpeechSynthesizer
        try
        {
            using var synth = new SpeechSynthesizer();
            foreach (var v in synth.GetInstalledVoices())
            {
                if (v.Enabled && seen.Add(v.VoiceInfo.Name))
                {
                    string genderStr = v.VoiceInfo.Gender == VoiceGender.Female ? "Женский" :
                                       v.VoiceInfo.Gender == VoiceGender.Male ? "Мужской" : "Нейтральный";
                    if (v.VoiceInfo.Name.Contains("Irina", StringComparison.OrdinalIgnoreCase) ||
                        v.VoiceInfo.Name.Contains("Zira", StringComparison.OrdinalIgnoreCase) ||
                        v.VoiceInfo.Name.Contains("Elena", StringComparison.OrdinalIgnoreCase) ||
                        v.VoiceInfo.Name.Contains("Kseniya", StringComparison.OrdinalIgnoreCase) ||
                        v.VoiceInfo.Name.Contains("Xenia", StringComparison.OrdinalIgnoreCase))
                    {
                        genderStr = "Женский";
                    }
                    else if (v.VoiceInfo.Name.Contains("Pavel", StringComparison.OrdinalIgnoreCase) ||
                             v.VoiceInfo.Name.Contains("Aidar", StringComparison.OrdinalIgnoreCase) ||
                             v.VoiceInfo.Name.Contains("Baya", StringComparison.OrdinalIgnoreCase) ||
                             v.VoiceInfo.Name.Contains("David", StringComparison.OrdinalIgnoreCase) ||
                             v.VoiceInfo.Name.Contains("Eugene", StringComparison.OrdinalIgnoreCase))
                    {
                        genderStr = "Мужской";
                    }

                    list.Add((v.VoiceInfo.Name, $"{v.VoiceInfo.Name} ({genderStr})"));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsWindow] Error enumerating System.Speech voices: {ex.Message}");
        }

        // 2. Through SAPI.SpVoice COM tokens
        try
        {
            Type? spVoiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (spVoiceType != null)
            {
                dynamic spVoice = Activator.CreateInstance(spVoiceType)!;
                dynamic tokens = spVoice.GetVoices();
                int count = tokens.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic token = tokens.Item(i);
                    string desc = (string)token.GetDescription();
                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        string baseName = desc;
                        int dashIdx = desc.IndexOf(" - ", StringComparison.Ordinal);
                        if (dashIdx > 0)
                        {
                            baseName = desc.Substring(0, dashIdx).Trim();
                        }

                        if (!seen.Contains(desc) && !seen.Contains(baseName))
                        {
                            seen.Add(desc);
                            string genderStr = desc.Contains("Female", StringComparison.OrdinalIgnoreCase) ||
                                               desc.Contains("Irina", StringComparison.OrdinalIgnoreCase) ||
                                               desc.Contains("Zira", StringComparison.OrdinalIgnoreCase) ||
                                               desc.Contains("Kseniya", StringComparison.OrdinalIgnoreCase) ||
                                               desc.Contains("Xenia", StringComparison.OrdinalIgnoreCase) ? "Женский" : "Мужской";
                            list.Add((desc, $"{desc} ({genderStr})"));
                        }
                    }
                }
                try { Marshal.ReleaseComObject(spVoice); }
                catch (Exception ex)
                {
                    JarvisLog.Debug("SettingsWindow", $"ReleaseComObject error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsWindow] Error enumerating SAPI COM voices: {ex.Message}");
        }

        // Fallback default list if no SAPI voices detected
        if (list.Count == 0)
        {
            list.Add(("Aidar", "Aidar (Silero SAPI5, Мужской)"));
            list.Add(("Microsoft Pavel", "Microsoft Pavel (Windows SAPI5, Мужской)"));
            list.Add(("Microsoft Irina Desktop", "Microsoft Irina Desktop (Windows SAPI5, Женский)"));
        }

        return list;
    }

    private void CmbTtsEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        bool isEdge = CmbTtsEngine.SelectedIndex == 0;
        var config = AppSettingsService.Load();
        UpdateVoiceComboBox(isEdge, config.Tts);
    }

    private async void BtnTestVoice_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnTestVoice.IsEnabled = false;
            bool isEdge = CmbTtsEngine.SelectedIndex == 0;
            string selectedVoice = (CmbTtsVoice.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                ?? (CmbTtsVoice.SelectedItem as ComboBoxItem)?.Content?.ToString()
                ?? "Aidar";

            if (selectedVoice.Contains(" - ", StringComparison.Ordinal))
            {
                selectedVoice = selectedVoice.Substring(0, selectedVoice.IndexOf(" - ", StringComparison.Ordinal)).Trim();
            }

            if (isEdge)
            {
                using var edge = new EdgeTtsEngine(selectedVoice, 3000);
                await edge.SpeakAsync("Тест звуковой подсистемы. Джарвис готов к работе.");
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
}
