// Direct2D/Win2D pixel effect, not a Composition or DWM-injected shader.
// Capture is linear scRGB (1 = 80 nits). Preserve the desktop SDR white scale,
// roll off highlights, then encode sRGB BEFORE handing BGRA8 to WinUI.
#define D2D_INPUT_COUNT 1
#define D2D_INPUT0_SIMPLE
#include "d2d1effecthelpers.hlsli"

cbuffer Parameters : register(b0)
{
    float WhiteScale;
};

float EncodeSrgb(float value)
{
    return value <= 0.0031308 ? 12.92 * value : 1.055 * pow(max(value, 0.0), 1.0 / 2.4) - 0.055;
}

D2D_PS_ENTRY(main)
{
    float3 rgb = clamp(D2DGetInput(0).rgb, 0.0, 65504.0) / max(WhiteScale, 0.1);
    float peak = max(rgb.r, max(rgb.g, rgb.b));
    // Identity below the shoulder: ordinary SDR midtones stay unchanged.
    // Peak-based scaling preserves channel ratios without per-channel clipping.
    const float knee = 0.75;
    if (peak > knee)
    {
        float mapped = knee + (1.0 - knee) * (1.0 - exp(-(peak - knee) / (1.0 - knee)));
        rgb *= mapped / peak;
    }
    return float4(EncodeSrgb(rgb.r), EncodeSrgb(rgb.g), EncodeSrgb(rgb.b), 1.0);
}
