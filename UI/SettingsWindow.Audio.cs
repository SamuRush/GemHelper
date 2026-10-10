using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Gem.Voice;
using NAudio.Wave;

namespace Gem.UI;

public sealed partial class SettingsWindow
{
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
                _previewWaveIn.DataAvailable += OnPreviewWaveInDataAvailable;
                _previewWaveIn.StartRecording();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SettingsWindow] Ошибка тест-мониторинга микрофона: {ex.Message}");
        }
    }

    private void OnPreviewWaveInDataAvailable(object? sender, WaveInEventArgs e)
    {
        double rms = VoiceListener.CalculateRms(e.Buffer, e.BytesRecorded);
        OnRmsLevelChanged((float)rms);
    }

    private void StopAudioMonitoring()
    {
        if (VoiceListener.Instance != null)
        {
            VoiceListener.Instance.OnRmsLevelChanged -= OnRmsLevelChanged;
        }

        if (_previewWaveIn != null)
        {
            _previewWaveIn.DataAvailable -= OnPreviewWaveInDataAvailable;
            try { _previewWaveIn.StopRecording(); }
            catch (Exception ex)
            {
                JarvisLog.Debug("SettingsWindow", $"StopRecording error: {ex.Message}");
            }
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
}
