// The samples' floor: a grid drawn in world space, a line every spacing and a bolder one every majorEvery lines, so a
// floor of any size shows its metres without a texture to stretch over it. Each line covers the share of a pixel it
// really does, from the inputs' position derivatives, so far lines thin to a faint tone rather than shimmering.

#include "Include/Surface.hlsli"

struct MaterialParams
{
    Color color : GiColor = Color(0.5, 0.52, 0.55, 1.0);
    Color lineColor = Color(0.28, 0.3, 0.33, 1.0);
    float roughness = 0.75;
    float spacing = 1.0;      // metres between lines
    float majorEvery = 10.0;  // lines between bold ones
    float width = 0.03;       // a line's width, in metres
};

// How much of the pixel a grid's lines cover, at p in lines, the pixel's footprint and the lines' width in lines.
float Lines(float2 p, float2 footprint, float width)
{
    float2 from = abs(frac(p + 0.5) - 0.5);
    float2 across = max(footprint, 1e-5);
    float2 covered = saturate((width * 0.5 - from) / across + 0.5);
    float2 average = saturate(width / across);
    float2 coverage = lerp(covered, average, saturate(across / width - 1.0));
    return max(coverage.x, coverage.y);
}

Surface EvaluateSurface(SurfaceInputs input, MaterialParams material)
{
    Surface surface = DefaultSurface(input);
    float2 p = input.worldPosition.xz / material.spacing;
    float2 footprint = (abs(input.positionDdx.xz) + abs(input.positionDdy.xz)) / material.spacing;
    float minor = Lines(p, footprint, material.width / material.spacing);
    float major = Lines(p / material.majorEvery, footprint / material.majorEvery,
        material.width * 2.5 / (material.spacing * material.majorEvery));
    float drawn = max(minor * 0.6, major);
    surface.baseColor = lerp(material.color.rgb, material.lineColor.rgb, drawn);
    surface.roughness = material.roughness;
    return surface;
}
