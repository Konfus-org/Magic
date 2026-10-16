// What a view shows while the frame is viewed at a stage that makes no image for it (the renderer's debug view at a
// stage that only fills buffers, or whose passes are not ready): the failure's magenta, breathing as a failure does,
// saying "Nothing draws to this pass!" across the middle in the text a shader draws itself, encoded as the target
// expects, as the finished image is.

#include "Include/DebugText.hlsli"
#include "Include/Failure.hlsli"
#include "Include/Fullscreen.hlsli"
#include "Include/TargetEncode.hlsli"

static const uint MessageLength = 27u;
static const uint Message[27] =
{
    GlyphLetterA + 13u, GlyphLetterA + 14u, GlyphLetterA + 19u, GlyphLetterA + 7u, GlyphLetterA + 8u,
    GlyphLetterA + 13u, GlyphLetterA + 6u, GlyphSpace,
    GlyphLetterA + 3u, GlyphLetterA + 17u, GlyphLetterA + 0u, GlyphLetterA + 22u, GlyphLetterA + 18u, GlyphSpace,
    GlyphLetterA + 19u, GlyphLetterA + 14u, GlyphSpace,
    GlyphLetterA + 19u, GlyphLetterA + 7u, GlyphLetterA + 8u, GlyphLetterA + 18u, GlyphSpace,
    GlyphLetterA + 15u, GlyphLetterA + 0u, GlyphLetterA + 18u, GlyphLetterA + 18u, GlyphExclamation,
};

// View pixels a texel of the text covers, so it reads at any size.
static const uint TextScale = 4u;
static const float3 TextColor = float3(1.0, 1.0, 1.0);

float4 main(PassVaryings input) : SV_Target0
{
    uint2 size = uint2(OutputSize);
    uint2 pixel = uint2(input.uv * OutputSize);
    uint2 textTexels = uint2(MessageLength * GlyphCell.x, GlyphCell.y);
    uint2 origin = (size - min(size, textTexels * TextScale)) / 2u;
    uint2 texel = (pixel - origin) / TextScale;
    bool isInText = all(pixel >= origin) && all(texel < textTexels);
    uint glyph = Message[min(texel.x / GlyphCell.x, MessageLength - 1u)];
    bool isInk = isInText && GlyphPixel(glyph, uint2(texel.x % GlyphCell.x, texel.y));
    float3 color = isInk ? TextColor : FailureColor * FailurePulse(Time);
    return float4(EncodeForTarget(color, input.position.xy), 1.0);
}
