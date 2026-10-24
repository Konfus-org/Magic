// The samples' floor: a grid drawn in world space, a line every spacing and a bolder one every majorEvery lines, so a
// floor of any size shows its metres without a texture to stretch over it. Each line covers the share of a pixel it
// really does, from the inputs' position derivatives, so far lines thin to a faint tone rather than shimmering.

module "Grid";

import Core.Surface;

public struct Grid : ISurface
{
    [GiColor] Color color = { 0.5, 0.52, 0.55, 1.0 };
    Color lineColor = { 0.28, 0.3, 0.33, 1.0 };
    float roughness = 0.75;
    float spacing = 1.0;      // metres between lines
    float majorEvery = 10.0;  // lines between bold ones
    float width = 0.03;       // a line's width, in metres

    public Surface Evaluate(SurfaceInputs input)
    {
        Surface surface = CreateDefaultSurface(input);
        float2 p = input.worldPosition.xz / this.spacing;
        float2 footprint = (abs(input.positionDdx.xz) + abs(input.positionDdy.xz)) / this.spacing;
        float minor = GetLineCoverage(p, footprint, this.width / this.spacing);
        float major = GetLineCoverage(p / this.majorEvery, footprint / this.majorEvery,
            this.width * 2.5 / (this.spacing * this.majorEvery));
        float drawn = max(minor * 0.6, major);
        surface.baseColor = lerp(this.color.rgb, this.lineColor.rgb, drawn);
        surface.roughness = this.roughness;
        return surface;
    }
}

// How much of the pixel a grid's lines cover, at p in lines, the pixel's footprint and the lines' width in lines.
float GetLineCoverage(float2 p, float2 footprint, float width)
{
    float2 from = abs(frac(p + 0.5) - 0.5);
    float2 across = max(footprint, 1e-5);
    float2 covered = saturate((width * 0.5 - from) / across + 0.5);
    float2 average = saturate(width / across);
    float2 coverage = lerp(covered, average, saturate(across / width - 1.0));
    return max(coverage.x, coverage.y);
}

