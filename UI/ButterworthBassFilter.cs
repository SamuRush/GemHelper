using System;

namespace Gem.UI;

/// <summary>
/// 2nd-order Butterworth low-pass filter (IIR) at fc ≈ 150 Hz.
/// Pre-filters raw audio samples to isolate sub-bass and bass transients
/// for beat detection while maintaining zero runtime allocations.
/// </summary>
public sealed class ButterworthBassFilter
{
    private const double LpfA1 = -1.99786986602808;   // -a1
    private const double LpfA2 = 0.99787895688660;    // -a2
    private const double LpfB0 = 2.27770845847543e-5;
    private const double LpfB1 = 4.55541691695086e-5; // 2 × B0
    private const double LpfB2 = 2.27770845847543e-5;

    private double _w0 = 0.0;
    private double _w1 = 0.0;

    /// <summary>
    /// Filters a single audio sample through the 2nd-order Butterworth IIR filter.
    /// </summary>
    public float Apply(float x)
    {
        double w = (double)x - LpfA1 * _w0 - LpfA2 * _w1;
        double y = LpfB0 * w + LpfB1 * _w0 + LpfB2 * _w1;
        _w1 = _w0;
        _w0 = w;
        return (float)Math.Clamp(y, -1.0, 1.0);
    }

    /// <summary>
    /// Resets the internal IIR filter delay state to zero.
    /// </summary>
    public void Reset()
    {
        _w0 = 0.0;
        _w1 = 0.0;
    }
}
