// The surface a broken thing is drawn with: pure emission (no lighting, no reflection) in a colour and pattern that
// says what broke, breathing on a slow sine so it is never mistaken for a real material. Magenta is a shader, red an
// asset that did not load. Its kind is set by the failure materials, Materials/Failures/<FailureType>.mat, which a
// renderer draws in a broken material's place (Assets.Failure<Material>); the numbers are FailureType's.
//
//   1  Shader   solid magenta       the material does not load, or its surface does not compile
//   2  Texture  red/black checker   a texture the material samples does not load
//   3  Mesh     solid red           the model does not load; drawn on the failure cube

#include "Include/Failure.hlsli"
#include "Include/Surface.hlsli"

static const uint FailureShader = 1u;
static const uint FailureTexture = 2u;
static const uint FailureMesh = 3u;

// Checker cells per unit of uv.
static const float CheckerCells = 8.0;

struct MaterialParams
{
    uint kind = 1;
};

// The pattern in uv space: solid magenta for a shader failure (and any kind this file does not know), solid red for a
// mesh, a checker of red and black for a texture.
float3 FailurePattern(uint kind, float2 uv)
{
    bool isRed = kind == FailureMesh || kind == FailureTexture;
    float3 color = isRed ? float3(1.0, 0.0, 0.0) : FailureColor;
    if (kind != FailureTexture)
    {
        return color;
    }

    int2 cell = int2(floor(uv * CheckerCells));
    bool isLit = ((cell.x + cell.y) & 1) == 0;
    return isLit ? color : float3(0.0, 0.0, 0.0);
}

Surface EvaluateSurface(SurfaceInputs input, MaterialParams material)
{
    // Cutoff 0 so a masked variant never clips the glow away.
    Surface surface = DefaultSurface(input);
    surface.baseColor = float3(0.0, 0.0, 0.0);
    surface.specular = 0.0;
    surface.emissive = FailurePattern(material.kind, input.uv) * FailureGlow * FailurePulse(input.frame.realSec);
    surface.alphaCutoff = 0.0;
    return surface;
}
