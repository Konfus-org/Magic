// The surface a broken thing is drawn with: pure emission (no lighting) in a colour and pattern that says
// what broke, breathing on a slow sine so it is never mistaken for a real material. The renderer's C# never
// encodes a colour: it only decides the kind, packed into the material's record (MaterialTable.cs carries
// the same numbers).
//
//   FailureShader   solid magenta          the material's surface shader did not compile or parse
//   FailureRecord   magenta/black checker  the material file, or the shader it names, is not an asset
//   FailureMesh     red/black checker      the model did not load; drawn on the renderer's unit cube
//   FailureTexture  red/black checker      a texture the material samples did not load
//
// When the failure is a surface shader that does not compile, its pipeline is this surface compiled with
// FAILURE_FORCE 1 instead: the records of that class still have the broken surface's layout, so the kind is
// fixed here and the record is never read.

#include "Include/Failure.hlsli"
#include "Include/Surface.hlsli"

#ifndef FAILURE_FORCE
#define FAILURE_FORCE 0
#endif

static const uint FailureShader = 1u;
static const uint FailureRecord = 2u;
static const uint FailureMesh = 3u;
static const uint FailureTexture = 4u;

// Checker cells per unit of uv. A mesh failure is coarser so the unit cube reads as one object rather than
// a texture.
static const float CheckerCells = 8.0;
static const float CheckerCellsMesh = 4.0;

struct MaterialParams
{
    uint kind = 2;
};

// The pattern in uv space: solid magenta for a shader failure (and any kind this file does not know), else
// a checker of the kind's colour and black.
float3 FailurePattern(uint kind, float2 uv)
{
    bool isRed = kind == FailureMesh || kind == FailureTexture;
    bool isCheckered = kind == FailureRecord || isRed;
    float3 color = isRed ? float3(1.0, 0.0, 0.0) : FailureColor;

    int2 cell = (int2)floor(uv * (kind == FailureMesh ? CheckerCellsMesh : CheckerCells));
    bool isLit = ((cell.x + cell.y) & 1) == 0;
    return (isLit || !isCheckered) ? color : float3(0.0, 0.0, 0.0);
}

Surface EvaluateSurface(SurfaceInputs input, MaterialParams material)
{
#if FAILURE_FORCE
    uint kind = FailureShader;
#else
    uint kind = material.kind;
#endif

    // Cutoff 0 so a masked variant never clips the glow away.
    Surface surface = DefaultSurface(input);
    surface.baseColor = float3(0.0, 0.0, 0.0);
    surface.emissive = FailurePattern(kind, input.uv) * FailureGlow * FailurePulse(input.time);
    surface.alphaCutoff = 0.0;
    return surface;
}
