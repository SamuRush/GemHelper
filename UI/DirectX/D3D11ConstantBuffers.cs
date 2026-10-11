using System.Numerics;
using System.Runtime.InteropServices;

namespace Gem.UI.DirectX;

[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct GlobalCBuffer
{
    public Vector2 Resolution;
    public float Time;
    public float BeatIntensity;
    public Vector2 Center;
    public float Progress;
    public float WhiteoutIntensity;
}

[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct RefractionCBuffer
{
    public Vector2 Center;
    public float DistortionIntensity;
    public float ChromaticAberrationPixels;
    public Vector2 Resolution;
    public float Time;
    public float Padding;
}

[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct BloomCBuffer
{
    public float Threshold;
    public float BloomIntensity;
    public Vector2 BlurDirection;
    public Vector2 Resolution;
    public Vector2 Padding;
}

[StructLayout(LayoutKind.Sequential, Size = 48)]
public struct ShardInstanceData
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Size;
    public float Rotation;
    public float Alpha;
    public float Stretch;
    public Vector4 Color;
}

[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct LightningVertex
{
    public Vector2 Position;
    public float Thickness;
    public float Intensity;
    public Vector4 Color;
}

[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct QuadVertex
{
    public Vector2 Position;

    public QuadVertex(float x, float y)
    {
        Position = new Vector2(x, y);
    }
}
