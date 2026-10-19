// Scales the captured desktop down and converts it to 8-bit SDR in one pass.
//
// Port of the DX grabber's HDR shaders (VertexShaderHdr.h / PixelShaderHdr.h in the C++ reference, which only shipped
// bytecode; reconstructed from its disassembly): an scRGB (FP16) desktop is scaled so that the SDR white level becomes
// 1.0 (80 nits / monitor_nits) and encoded with the BT.709 transfer function. Extended here with
//  - area averaging: each output pixel averages a Block x Block area of the source (the C++ code took one bilinear
//    sample on the HDR path and used a mip chain on the SDR path);
//  - the crop origin (the C++ code cropped the scaled-down image on the CPU);
//  - PQ / BT.2020 desktops (DXGI_FORMAT_R10G10B10A2_UNORM duplication, when FP16 isn't offered);
//  - the tone mapping LUT as a 3D texture for 8-bit captures (the C++ code applied it on the CPU).
//
// Compiled by Shaders.targets with the Windows SDK's fxc (vs_5_0 / ps_5_0); the bytecode is checked in next to it.

#define MODE_SDR 0
#define MODE_SCRGB 1
#define MODE_PQ 2
#define MODE_LUT 3

cbuffer Params : register(b0)
{
    float2 Origin;        // top-left of the captured area, in source texels
    float2 InvSourceSize; // 1 / size of the source texture
    float Block;          // source texels per output pixel along each axis (the whole scale factor)
    uint Taps;            // samples per output pixel along each axis
    uint Mode;            // MODE_*
    float Scale;          // MODE_SCRGB: 80 / SDR white nits; MODE_PQ: 10000 / SDR white nits
};

Texture2D<float4> Source : register(t0);
Texture3D<float4> Lut : register(t1);
SamplerState Bilinear : register(s0);

// BT.2020 to BT.709 primaries (ColorSpaceMath::from_BT2020_to_BT709).
static const float3x3 Bt2020ToBt709 =
{
     1.6605, -0.5876, -0.0728,
    -0.1246,  1.1329, -0.0083,
    -0.0182, -0.1006,  1.1187,
};

// One triangle that covers the whole viewport; no vertex buffer needed.
float4 VSMain(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

// SMPTE ST 2084 EOTF: 1.0 is 10000 nits.
float3 PqToLinear(float3 e)
{
    const float m1 = 2610.0 / 16384.0;
    const float m2 = 2523.0 / 4096.0 * 128.0;
    const float c1 = 3424.0 / 4096.0;
    const float c2 = 2413.0 / 4096.0 * 32.0;
    const float c3 = 2392.0 / 4096.0 * 32.0;
    float3 p = pow(saturate(e), 1.0 / m2);
    return pow(max(p - c1, 0) / (c2 - c3 * p), 1.0 / m1);
}

// BT.709 OETF, as in the C++ pixel shader.
float3 Rec709(float3 l)
{
    l = saturate(l);
    return l < 0.018 ? 4.5 * l : 1.099 * pow(l, 0.45) - 0.099;
}

// Color at a source position (texel units), linear light for the HDR modes.
float3 Fetch(float2 p)
{
    float3 c;
    if (Mode == MODE_LUT)
    {
        uint3 index = (uint3)round(Source.Load(int3(p, 0)).rgb * 255.0);
        c = Lut.Load(int4(index, 0)).rgb;
    }
    else
    {
        c = Source.SampleLevel(Bilinear, p * InvSourceSize, 0).rgb;
        if (Mode == MODE_SCRGB)
        {
            c *= Scale;
        }
        else if (Mode == MODE_PQ)
        {
            c = mul(Bt2020ToBt709, PqToLinear(c)) * Scale;
        }
    }

    return c;
}

float4 PSMain(float4 position : SV_Position) : SV_Target
{
    float2 base = Origin + floor(position.xy) * Block;
    float step = Block / Taps;
    float3 sum = 0;
    for (uint y = 0; y < Taps; y++)
    {
        for (uint x = 0; x < Taps; x++)
        {
            sum += Fetch(base + (float2(x, y) + 0.5) * step);
        }
    }

    float3 c = sum / (Taps * Taps);
    return float4(Mode == MODE_SCRGB || Mode == MODE_PQ ? Rec709(c) : c, 1);
}
