// The samples' trees: bark, needles and leaves, which answer the world's weather. Under cloud they are wet, darker and
// glossier, the more the sky is covered; in wind, needles and leaves fleck lighter where gusts turn them, the flecks
// running downwind as fast as it blows; and snow lies on whatever faces up above the snow line, the terrain's own, which a
// script moves with the season. Bark is its maps; needles are drawn across a frond's uv, which runs across it 0..1 and
// along it in metres, as rows of needles, dark between, with their normals tilted to each side; a leaf's uv runs across
// it 0..1 and from its stem to its tip, which draws its midrib. Needles and leaves let light through.

#include "Include/Noise.hlsli"
#include "Include/Surface.hlsli"

struct MaterialParams
{
    Color color : GiColor = Color(1.0, 1.0, 1.0, 1.0); // tints the colour map, or is the colour without one
    float roughness = 0.85;
    float needles = 0.0;        // rows of needles per metre along a frond; 0 for bark and leaves
    float leaf = 0.0;           // 1 draws a leaf's midrib and lighter edges
    float translucency = 0.0;   // how much light passes through, for needles and leaves
    float shimmer = 0.0;        // how much the wind flecks it
    float snowLine = 100000.0;  // metres above the world's origin snow lies above, on what faces up; none by default
    float wetness = 1.0;        // how wet a covered sky makes it, 0..1
    TextureRef colorMap : GiColorMap;
    TextureRef normalMap;
    TextureRef ormMap;
};

Surface EvaluateSurface(SurfaceInputs input, MaterialParams material)
{
    Surface surface = DefaultSurface(input);
    float3 color = material.color.rgb;
    float roughness = material.roughness;
    float occlusion = 1.0;
    float3 normal = input.normal;

    [branch] if (HasTexture(material.colorMap))
    {
        color *= SampleTexture(input, material.colorMap).rgb;
    }

    [branch] if (HasTexture(material.ormMap))
    {
        float3 orm = SampleTexture(input, material.ormMap).rgb;
        occlusion = orm.r;
        roughness *= orm.g;
    }

    [branch] if (HasTexture(material.normalMap))
    {
        normal = SampleNormal(input, material.normalMap, 1.0);
    }

    // Each tree, and each part of one, a little its own shade.
    float patch = GradientNoise(input.worldPosition * 0.7);
    color *= 0.85 + 0.3 * saturate(patch * 0.5 + 0.5);

    [branch] if (material.needles > 0.0)
    {
        // Rows of needles across the frond: each row a ridge, its sides turned away from the frond's middle and
        // darker in the gaps, and the frond lighter toward its edges, where it grew last.
        float row = frac(input.uv.y * material.needles);
        float across = abs(input.uv.x - 0.5) * 2.0;
        float ridge = sin(row * TwoPi);
        float gap = smoothstep(0.0, 0.35, abs(row - 0.5) * 2.0);
        normal = PerturbNormal(input, float3((input.uv.x - 0.5) * 0.8, ridge * 0.6, 1.0));
        color *= lerp(1.0, 0.55, gap) * lerp(0.8, 1.15, across);
        occlusion *= lerp(1.0, 0.6, gap * (1.0 - across));
    }

    [branch] if (material.leaf > 0.0)
    {
        float midrib = 1.0 - smoothstep(0.0, 0.05, abs(input.uv.x - 0.5));
        float edge = smoothstep(0.25, 0.5, abs(input.uv.x - 0.5));
        color *= lerp(1.0, 1.25, midrib * material.leaf) * lerp(1.0, 1.1, edge * material.leaf);
    }

    // Gusts: flecks that run downwind, as many as it blows hard.
    float windSpeed = length(input.weather.wind);
    [branch] if (material.shimmer > 0.0 && windSpeed > 0.1)
    {
        float3 moving = input.worldPosition * 0.9 - input.weather.wind * (input.frame.worldSec * 0.35);
        float fleck = saturate(GradientNoise(moving) * 2.0 - 0.4) * saturate(windSpeed / 12.0) * material.shimmer;
        color *= 1.0 + fleck * 0.6;
        normal = NormalizeOrZero(normal + NormalizeOrZero(input.weather.wind) * fleck * 0.5);
    }

    // Wet under cloud: darker, as water fills the surface, and glossier.
    float wet = saturate((input.weather.coverage - 0.45) * 2.0) * material.wetness;
    color *= lerp(1.0, 0.6, wet);
    roughness = lerp(roughness, 0.3, wet);

    // Snow on what faces up above the snow line, coming in over a band below it, broken up a little so it lies in
    // patches.
    float above = smoothstep(material.snowLine - 250.0, material.snowLine + 50.0, input.worldPosition.y + patch * 60.0);
    float settled = smoothstep(0.25, 0.75, normal.y + patch * 0.25) * above;
    color = lerp(color, float3(0.9, 0.92, 0.95), settled);
    roughness = lerp(roughness, 0.75, settled);

    surface.baseColor = color;
    surface.normal = normal;
    surface.roughness = roughness;
    surface.occlusion = occlusion;
    surface.metallic = 0.0;
    float through = material.translucency * (1.0 - settled);
    [branch] if (through > 0.0)
    {
        surface.shadingModel = ShadingSubsurface;
        surface.modelData = float2(through, 0.5);
    }

    return surface;
}
