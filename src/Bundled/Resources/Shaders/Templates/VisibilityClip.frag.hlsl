// The visibility buffer's bin of a surface whose pixels clip: composed after the surface and its LoadMaterialParams, it
// keeps a pixel where a card's mesh covers it, its dissolve keeps it (a fade, or a card and its clusters taking over
// from each other), and, for a masked surface, its alpha reaches its cutoff; the pixel then takes its triangle's name.
// A card's pixel is put at the depth of the mesh it shows, never nearer than its quad, so the depth test still culls
// early.

#include "Include/PieceFragment.hlsli"
#include "Include/Pieces.hlsli"

uint main(PieceVaryings input, bool isFrontFace : SV_IsFrontFace, out float depth : SV_DepthLessEqual) : SV_Target0
{
    float2 uvDdx = ddx(input.uv);
    float2 uvDdy = ddy(input.uv);
    float3 positionDdx = ddx(input.position);
    float3 positionDdy = ddy(input.position);
    float cover;
    float surfaceDepth;
    SurfaceInputs inputs = PieceInputsOf(input, uvDdx, uvDdy, positionDdx, positionDdy, isFrontFace, cover, surfaceDepth);
    Surface surface = EvaluateSurface(inputs, LoadMaterialParams(input.material));
    float keep = min(cover - 0.5, DissolveKeep(input.dissolve, input.clip.xy));
#if SURFACE_MASKED
    keep = min(keep, surface.alpha - surface.alphaCutoff);
#endif
    clip(keep);
    depth = surfaceDepth;
    return input.id;
}
