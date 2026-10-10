namespace Gem.Voice;

/// <summary>
/// Audio energy RMS calculator and noise gate utilities.
/// </summary>
public static class AudioEnergyGate
{
    /// <summary>
    /// Calculates the Root Mean Square (RMS) energy level of 16-bit PCM audio samples.
    /// </summary>
    public static double CalculateRms(byte[] buffer, int bytesRecorded)
    {
        int sampleCount = bytesRecorded / 2;
        if (sampleCount == 0) return 0;

        double sumSquares = 0;
        for (int i = 0; i < bytesRecorded; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sumSquares += (double)sample * sample;
        }

        return Math.Sqrt(sumSquares / sampleCount);
    }

    /// <summary>
    /// Checks whether the audio buffer's RMS energy meets or exceeds the noise gate threshold.
    /// </summary>
    public static bool IsSpeech(byte[] buffer, int bytesRecorded, double thresholdRms, out double calculatedRms)
    {
        calculatedRms = CalculateRms(buffer, bytesRecorded);
        return calculatedRms >= thresholdRms;
    }
}
