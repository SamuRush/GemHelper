namespace Gem.UI.DirectX;

/// <summary>
/// HLSL Shader Model 5.0 shader sources for the Overdrive hardware graphics pipeline:
/// - Glass Refraction / Normal Distortion with chromatic aberration (3-5 px split)
/// - Procedural Plasma Lightning Arcs with dynamic vertex jitter
/// - Instanced 2D Shards with hypersonic motion blur velocity elongation
/// - Postprocessing HDR Bloom extraction and separable Gaussian blur
/// </summary>
public static class D3D11ShaderSources
{
    public const string RefractionHlsl = @"
cbuffer RefractionBuffer : register(b0)
{
    float2 Center;
    float DistortionIntensity;
    float ChromaticAberrationPixels;
    float2 Resolution;
    float Time;
    float Padding;
};

Texture2D BackdropTexture : register(t0);
Texture2D CrackDistortionMap : register(t1);
SamplerState BackdropSampler : register(s0);

struct VS_OUTPUT
{
    float4 Position : SV_POSITION;
    float2 UV : TEXCOORD0;
};

VS_OUTPUT VSMain(uint vertexId : SV_VertexID)
{
    VS_OUTPUT output;
    // Generate fullscreen triangle covering [-1, 1] NDC
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.Position = float4(uv * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
    output.UV = uv;
    return output;
}

float4 PSMain(VS_OUTPUT input) : SV_TARGET
{
    float2 uv = input.UV;
    float2 pixelPos = uv * Resolution;
    float2 delta = pixelPos - Center;
    float dist = length(delta);

    if (DistortionIntensity <= 0.001f || dist > 1400.0f)
    {
        return float4(0, 0, 0, 0);
    }

    float2 dir = dist > 0.001f ? (delta / dist) : float2(0, 1);

    // Procedural cracked matrix wave pattern & normal perturbation
    float angle = atan2(delta.y, delta.x);
    float crackPattern = sin(angle * 7.0f + cos(dist * 0.025f)) * cos(angle * 13.0f);
    float radialShock = sin(dist * 0.045f - Time * 8.0f) * exp(-dist * 0.003f);
    float riftDisplacement = (crackPattern * 0.6f + radialShock * 0.4f) * DistortionIntensity;

    float2 normal = dir * riftDisplacement;

    // Optical glass refraction UV displacement
    float2 displacedUv = uv + normal * (24.0f / Resolution);

    // Chromatic aberration: 3 to 5 pixels separation between R and B channels
    float caOffset = (ChromaticAberrationPixels / Resolution.x) * (0.6f + DistortionIntensity * 0.4f);
    float2 uvR = displacedUv + dir * caOffset;
    float2 uvG = displacedUv;
    float2 uvB = displacedUv - dir * caOffset;

    float4 sampleR = BackdropTexture.Sample(BackdropSampler, uvR);
    float4 sampleG = BackdropTexture.Sample(BackdropSampler, uvG);
    float4 sampleB = BackdropTexture.Sample(BackdropSampler, uvB);

    float3 color = float3(sampleR.r, sampleG.g, sampleB.b);

    // Volumetric 3D glass facet bevels: bright specular highlight and deep shadow
    float bevelLight = saturate(dot(normal, float2(0.707f, -0.707f)));
    float3 glint = float3(1.0f, 1.0f, 1.0f) * pow(bevelLight, 6.0f) * 0.85f * DistortionIntensity;
    float facetShadow = (1.0f - saturate(dot(-normal, float2(0.707f, -0.707f)))) * 0.5f;

    color = (color * (0.65f + facetShadow)) + glint;

    // Cracked matrix edge line highlights (scarlet and cyan grid veins)
    float crackVein = saturate(abs(crackPattern) * 2.2f - 1.2f) * DistortionIntensity;
    color += float3(1.0f, 0.0f, 0.235f) * crackVein * 0.65f; // Crimson #FF003C
    color += float3(0.0f, 0.898f, 1.0f) * crackVein * 0.35f; // Cyan #00E5FF

    float alpha = saturate(DistortionIntensity * (0.65f + abs(riftDisplacement) * 1.5f));
    return float4(color * alpha, alpha);
}
";

    public const string LightningHlsl = @"
cbuffer GlobalBuffer : register(b0)
{
    float2 Resolution;
    float Time;
    float BeatIntensity;
    float2 Center;
    float Progress;
    float WhiteoutIntensity;
};

struct VS_INPUT
{
    float2 Position : POSITION;
    float Thickness : THICKNESS;
    float Intensity : INTENSITY;
    float4 Color : COLOR;
};

struct PS_INPUT
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR;
    float Intensity : TEXCOORD0;
};

PS_INPUT VSMain(VS_INPUT input)
{
    PS_INPUT output;

    // Dynamic procedural jitter along vertex normals
    float2 p = input.Position;
    float seed = (p.x * 12.9898f + p.y * 78.233f);
    float jitter = sin(seed + Time * 75.0f) * (3.8f + BeatIntensity * 4.2f);
    float2 jitteredPos = p + float2(sin(jitter * 2.1f), cos(jitter * 1.7f)) * (input.Thickness * 0.45f);

    // Transform to NDC [-1, 1]
    float2 ndc = (jitteredPos / Resolution) * 2.0f - 1.0f;
    ndc.y = -ndc.y;

    output.Position = float4(ndc, 0.0f, 1.0f);
    output.Color = input.Color;
    output.Intensity = input.Intensity * (1.0f + BeatIntensity * 0.65f);
    return output;
}

float4 PSMain(PS_INPUT input) : SV_TARGET
{
    // Blinding core (#FFFFFF) with electric halo (#FF003C / #00E5FF)
    float4 c = input.Color;
    float3 hdrGlow = c.rgb * input.Intensity;
    float alpha = c.a * saturate(input.Intensity);
    return float4(hdrGlow * alpha, alpha);
}
";

    public const string ShardHlsl = @"
cbuffer GlobalBuffer : register(b0)
{
    float2 Resolution;
    float Time;
    float BeatIntensity;
    float2 Center;
    float Progress;
    float WhiteoutIntensity;
};

struct VS_INPUT
{
    float2 TemplatePos : POSITION; // Quad template [-1..1]
    float2 InstancePos : INSTANCE_POS;
    float2 Velocity : INSTANCE_VEL;
    float Size : INSTANCE_SIZE;
    float Rotation : INSTANCE_ROT;
    float Alpha : INSTANCE_ALPHA;
    float Stretch : INSTANCE_STRETCH;
    float4 Color : INSTANCE_COLOR;
};

struct PS_INPUT
{
    float4 Position : SV_POSITION;
    float2 LocalUV : TEXCOORD0;
    float4 Color : COLOR;
    float Alpha : TEXCOORD1;
};

PS_INPUT VSMain(VS_INPUT input)
{
    PS_INPUT output;

    float speed = length(input.Velocity);
    float2 vNorm = speed > 0.001f ? (input.Velocity / speed) : float2(cos(input.Rotation), sin(input.Rotation));
    float2 nNorm = float2(-vNorm.y, vNorm.x);

    // Hypersonic motion blur stretch along velocity vector
    float streakLen = clamp(speed * 0.022f * input.Stretch, input.Size, input.Size * 6.5f);
    float halfWidth = max(1.5f, input.Size * 0.35f);

    float2 worldPos = input.InstancePos;
    if (input.TemplatePos.x > 0.5f)
    {
        // Leading sharp head
        worldPos += vNorm * (input.Size * 1.2f);
    }
    else if (input.TemplatePos.x < -0.5f)
    {
        // Trailing motion blur streak tail
        worldPos -= vNorm * streakLen;
    }
    else
    {
        // Flank wings
        worldPos += nNorm * (input.TemplatePos.y * halfWidth);
    }

    float2 ndc = (worldPos / Resolution) * 2.0f - 1.0f;
    ndc.y = -ndc.y;

    output.Position = float4(ndc, 0.0f, 1.0f);
    output.LocalUV = input.TemplatePos;
    output.Color = input.Color;
    output.Alpha = input.Alpha;
    return output;
}

float4 PSMain(PS_INPUT input) : SV_TARGET
{
    float d = length(input.LocalUV);
    // Sharp hot core in center
    float core = saturate(1.0f - d * 1.8f);
    float3 baseColor = input.Color.rgb;
    float3 finalColor = lerp(baseColor, float3(1.0f, 1.0f, 1.0f), core * 0.85f);

    float a = input.Alpha * saturate(1.1f - d);
    return float4(finalColor * a, a);
}
";

    public const string BloomHlsl = @"
cbuffer BloomBuffer : register(b0)
{
    float Threshold;
    float BloomIntensity;
    float2 BlurDirection;
    float2 Resolution;
    float2 Padding;
};

Texture2D SourceTexture : register(t0);
SamplerState LinearSampler : register(s0);

struct VS_OUTPUT
{
    float4 Position : SV_POSITION;
    float2 UV : TEXCOORD0;
};

VS_OUTPUT VSMain(uint vertexId : SV_VertexID)
{
    VS_OUTPUT output;
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.Position = float4(uv * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
    output.UV = uv;
    return output;
}

// Pass 1: Extract bright HDR areas (lightning, shards, core)
float4 PSExtract(VS_OUTPUT input) : SV_TARGET
{
    float4 c = SourceTexture.Sample(LinearSampler, input.UV);
    float brightness = dot(c.rgb, float3(0.299f, 0.587f, 0.114f));
    if (brightness < Threshold)
    {
        return float4(0, 0, 0, 0);
    }
    return float4(c.rgb * BloomIntensity, c.a);
}

// Pass 2: Separable 9-tap Gaussian Blur
float4 PSBlur(VS_OUTPUT input) : SV_TARGET
{
    float2 texOffset = (BlurDirection / Resolution);
    float weights[5] = { 0.227027f, 0.1945946f, 0.1216216f, 0.054054f, 0.016216f };

    float4 result = SourceTexture.Sample(LinearSampler, input.UV) * weights[0];
    [unroll]
    for (int i = 1; i < 5; ++i)
    {
        float2 off = texOffset * (float)i;
        result += SourceTexture.Sample(LinearSampler, input.UV + off) * weights[i];
        result += SourceTexture.Sample(LinearSampler, input.UV - off) * weights[i];
    }
    return result;
}
";
}
