using System.Windows.Media;

namespace Gem.UI;

public enum BoundaryEdge : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 3,
    Bottom = 4
}

/// <summary>
/// Lightweight struct representing a single energy shard in the reactor overdrive shatter particle system.
/// Fixed-size pool in ParticleEngine guarantees 0 allocations in the render loop.
/// Supports screen boundary collisions, elastic bounce physics, and stuck shard mini-visualizer.
/// </summary>
public struct ShardParticle
{
    public double X;
    public double Y;
    public double Vx;
    public double Vy;
    public double BaseAngle;
    public double BaseRadius;
    public double CurrentRadius;
    public double Size;
    public double Rotation;
    public double RotSpeed;
    public float Life;
    public bool Active;

    // Screen boundary collision and stuck mini-equalizer state
    public bool IsStuck;
    public BoundaryEdge StuckEdge;
    public double StuckX;
    public double StuckY;
    public double StuckAngle;

    // 8 miniature spectral bars for Stuck Shard Mini-Visualizer
    public float MiniBar0;
    public float MiniBar1;
    public float MiniBar2;
    public float MiniBar3;
    public float MiniBar4;
    public float MiniBar5;
    public float MiniBar6;
    public float MiniBar7;
}

/// <summary>
/// Lightweight struct representing a cyberpunk memory dump glitch decryptor block.
/// Fixed-size pool in ParticleEngine guarantees 0 allocations in the 60 FPS render loop.
/// Decrypts at 15-20 characters per second and renders formatted text directly via DrawingVisual.
/// </summary>
public struct GlitchCodeBlock
{
    public bool Active;
    public double X;
    public double Y;
    public double TotalLife;
    public double Age;
    public bool IsCrimson;
    public int LineCount;
    public int Template0;
    public int Template1;
    public int Template2;
    public int Template3;
    public int Template4;
    public int LastDecodedStep;
    public FormattedText? CachedFormattedText;
    public FormattedText? CachedGlowFormattedText;
}
