using System;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Gem.UI;

public sealed partial class ParticleEngine
{
    public const int MaxGlitchBlocks = 8;
    public const double GlitchCharsPerSecond = 18.0;

    public static readonly string[] GlitchCodeTemplates = new[]
    {
        "0x7FFE9B40 :: MEM_CORRUPTION_DETECTED",
        "FATAL_CORE_BREACH -> IRQ_OVERRIDE_0x0F",
        "DISPATCH_THREAD :: GAIN_SATURATION > 140%",
        "SYS_SIGNAL_TRACE: 0xDEADBEEF [FLUSH]",
        "STACK_OVERFLOW :: VECTOR_TABLE_COLLAPSE",
        "SEGFAULT_IN_CORE :: KERNEL_HALT_0x89",
        "MEM_DECRYPT [0x00FF88] -> BYPASS_OK",
        "CRITICAL_TEMP :: VOLTAGE_SURGE_9.8V",
        "BUFFER_UNDERRUN :: WASAPI_STREAM_DESYNC",
        "DMA_CHANNEL_7 :: ADDR_FAULT_0xBADF00D",
        "SYNAPSE_FAULT -> NEURAL_DEFORMATION",
        "QUANTUM_LEAK: ENTROPY_SPIKE +320dB",
        "KERNEL_PANIC :: RING0_EXCEPTION_0x1C",
        "AUDIO_DSP_OVERFLOW: 128_BANDS_CLIPPED",
        "RESONANCE_CASCADE: SUB_BASS_UNSTABLE",
        "REGISTER_DUMP: EAX=0xDEAD EBX=0xBEEF"
    };

    private const string GlitchGlyphs = "0123456789ABCDEF$#@%&!*<>/?~_=";

    public static readonly Typeface StaticGlitchTypeface = new(new FontFamily("Consolas, Cascadia Code, Lucida Console, monospace"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    public static readonly SolidColorBrush StaticGlitchCrimsonBrush;
    public static readonly SolidColorBrush StaticGlitchCrimsonGlowBrush;
    public static readonly SolidColorBrush StaticGlitchCyanBrush;
    public static readonly SolidColorBrush StaticGlitchCyanGlowBrush;
    public static readonly SolidColorBrush StaticGlitchDarkBackdropBrush;
    public static readonly Pen StaticGlitchCrimsonBorderPen;
    public static readonly Pen StaticGlitchCyanBorderPen;

    static ParticleEngine()
    {
        StaticGlitchCrimsonBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x3C));
        StaticGlitchCrimsonBrush.Freeze();

        StaticGlitchCrimsonGlowBrush = new SolidColorBrush(Color.FromArgb(110, 0xFF, 0x00, 0x3C));
        StaticGlitchCrimsonGlowBrush.Freeze();

        StaticGlitchCyanBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
        StaticGlitchCyanBrush.Freeze();

        StaticGlitchCyanGlowBrush = new SolidColorBrush(Color.FromArgb(110, 0x00, 0xE5, 0xFF));
        StaticGlitchCyanGlowBrush.Freeze();

        StaticGlitchDarkBackdropBrush = new SolidColorBrush(Color.FromArgb(180, 8, 12, 18));
        StaticGlitchDarkBackdropBrush.Freeze();

        StaticGlitchCrimsonBorderPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 0xFF, 0x00, 0x3C)), 1.0);
        StaticGlitchCrimsonBorderPen.Freeze();

        StaticGlitchCyanBorderPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 0x00, 0xE5, 0xFF)), 1.0);
        StaticGlitchCyanBorderPen.Freeze();
    }

    public void UpdateGlitchBlocks(double dt, double now, double boundsW, double boundsH, Point center, bool isOverdriveActive)
    {
        if (EnableMemoryGlitch && isOverdriveActive)
        {
            _glitchSpawnTimer -= dt;
            int activeCount = 0;
            for (int i = 0; i < MaxGlitchBlocks; i++)
            {
                if (_glitchBlocks[i].Active) activeCount++;
            }

            if (activeCount == 0 || (_glitchSpawnTimer <= 0.0 && activeCount < 3))
            {
                int toSpawn = (activeCount == 0) ? Math.Min(2, MaxGlitchBlocks) : 1;
                for (int s = 0; s < toSpawn; s++)
                {
                    SpawnGlitchBlock(boundsW, boundsH, center);
                }
                _glitchSpawnTimer = 0.9 + _rand.NextDouble() * 0.7;
            }
        }
        else
        {
            _glitchSpawnTimer = 0.0;
        }

        for (int i = 0; i < MaxGlitchBlocks; i++)
        {
            if (!_glitchBlocks[i].Active) continue;

            _glitchBlocks[i].Age += dt;

            if (!isOverdriveActive)
            {
                double remaining = _glitchBlocks[i].TotalLife - _glitchBlocks[i].Age;
                if (remaining > 0.60)
                {
                    _glitchBlocks[i].TotalLife = _glitchBlocks[i].Age + 0.60;
                }
            }

            if (_glitchBlocks[i].Age >= _glitchBlocks[i].TotalLife)
            {
                _glitchBlocks[i].Active = false;
                _glitchBlocks[i].CachedFormattedText = null;
                _glitchBlocks[i].CachedGlowFormattedText = null;
                continue;
            }

            int currentStep = (int)(_glitchBlocks[i].Age * GlitchCharsPerSecond);
            if (currentStep != _glitchBlocks[i].LastDecodedStep || _glitchBlocks[i].CachedFormattedText == null)
            {
                _glitchBlocks[i].LastDecodedStep = currentStep;
                RebuildGlitchBlockText(ref _glitchBlocks[i], now);
            }
        }
    }

    public bool SpawnGlitchBlock(double boundsW, double boundsH, Point center)
    {
        int slot = -1;
        for (int i = 0; i < MaxGlitchBlocks; i++)
        {
            if (!_glitchBlocks[i].Active)
            {
                slot = i;
                break;
            }
        }
        if (slot == -1) return false;

        double cx = center.X;
        double cy = center.Y;
        const double minDistance = 250.0;
        const double minDistanceSq = minDistance * minDistance;

        double marginX = 40.0;
        double marginY = 40.0;
        double maxW = Math.Max(marginX + 100.0, boundsW - 420.0);
        double maxH = Math.Max(marginY + 100.0, boundsH - 140.0);

        double chosenX = marginX;
        double chosenY = marginY;
        bool found = false;

        for (int attempt = 0; attempt < 25; attempt++)
        {
            double testX = marginX + _rand.NextDouble() * (maxW - marginX);
            double testY = marginY + _rand.NextDouble() * (maxH - marginY);

            double clampX = Math.Clamp(cx, testX - 10.0, testX + 370.0);
            double clampY = Math.Clamp(cy, testY - 8.0, testY + 100.0);
            double dx = cx - clampX;
            double dy = cy - clampY;

            if (dx * dx + dy * dy >= minDistanceSq)
            {
                chosenX = testX;
                chosenY = testY;
                found = true;
                break;
            }
        }

        if (!found)
        {
            chosenX = (cx > boundsW / 2.0) ? 60.0 : Math.Max(60.0, boundsW - 420.0);
            chosenY = (cy > boundsH / 2.0) ? 60.0 : Math.Max(60.0, boundsH - 150.0);
        }

        int lines = 3 + _rand.Next(3);
        int tCount = GlitchCodeTemplates.Length;
        int t0 = _rand.Next(tCount);
        int t1 = (t0 + 1 + _rand.Next(tCount - 1)) % tCount;
        int t2 = (t0 + 2 + _rand.Next(tCount - 2)) % tCount;
        int t3 = (t0 + 3 + _rand.Next(tCount - 3)) % tCount;
        int t4 = (t0 + 4 + _rand.Next(tCount - 4)) % tCount;

        _glitchBlocks[slot].Active = true;
        _glitchBlocks[slot].X = chosenX;
        _glitchBlocks[slot].Y = chosenY;
        _glitchBlocks[slot].Age = 0.0;
        _glitchBlocks[slot].TotalLife = 1.5 + _rand.NextDouble() * 1.0;
        _glitchBlocks[slot].IsCrimson = _rand.Next(2) == 0;
        _glitchBlocks[slot].LineCount = lines;
        _glitchBlocks[slot].Template0 = t0;
        _glitchBlocks[slot].Template1 = t1;
        _glitchBlocks[slot].Template2 = t2;
        _glitchBlocks[slot].Template3 = t3;
        _glitchBlocks[slot].Template4 = t4;
        _glitchBlocks[slot].LastDecodedStep = -1;
        _glitchBlocks[slot].CachedFormattedText = null;
        _glitchBlocks[slot].CachedGlowFormattedText = null;

        return true;
    }

    private void RebuildGlitchBlockText(ref GlitchCodeBlock block, double now)
    {
        _glitchTextBuffer.Clear();

        int lines = block.LineCount;
        for (int l = 0; l < lines; l++)
        {
            int tIdx = l switch
            {
                0 => block.Template0,
                1 => block.Template1,
                2 => block.Template2,
                3 => block.Template3,
                _ => block.Template4
            };
            string template = GlitchCodeTemplates[tIdx];

            double lineElapsed = block.Age - l * 0.12;
            int revealed = (int)(lineElapsed * GlitchCharsPerSecond);

            if (lineElapsed <= 0.0)
            {
                _glitchTextBuffer.Append(">> [SYS_DECRYPT]");
            }
            else if (revealed < template.Length)
            {
                _glitchTextBuffer.Append(template, 0, Math.Max(0, revealed));
                int gIdx = Math.Abs((int)(now * 40.0) + l * 5 + revealed) % GlitchGlyphs.Length;
                _glitchTextBuffer.Append(GlitchGlyphs[gIdx]);
            }
            else
            {
                _glitchTextBuffer.Append(template);
            }

            if (l < lines - 1)
            {
                _glitchTextBuffer.AppendLine();
            }
        }

        string text = _glitchTextBuffer.ToString();
        if (string.IsNullOrWhiteSpace(text)) text = ">> [SYS_DECRYPT]";

        var mainBrush = block.IsCrimson ? StaticGlitchCrimsonBrush : StaticGlitchCyanBrush;
        var glowBrush = block.IsCrimson ? StaticGlitchCrimsonGlowBrush : StaticGlitchCyanGlowBrush;

        block.CachedFormattedText = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            StaticGlitchTypeface,
            14.5,
            mainBrush,
            1.0
        );

        block.CachedGlowFormattedText = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            StaticGlitchTypeface,
            14.5,
            glowBrush,
            1.0
        );
    }
}
