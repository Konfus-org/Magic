// The samples' terrain: grass, forest floor, rock and snow, laid by how high the ground is, how steep and which way it
// faces, as a mountainside lays them. A terrain has no uvs, so the layers are mapped in world space by the inputs'
// position and its derivatives: from above where the ground is flat, and rock from the sides too where it is steep, so
// a cliff is not smeared down its face. Each layer is a colour, a normal and an occlusion-roughness-metal map; their
// normals are blended over the mesh's (Golus's whiteout blend, "Normal Mapping for a Triplanar Shader", 2017). Each
// reading of a layer is laid as splats, its maps laid again at the corners of a lattice, each corner's moved and
// turned its own way, so no tile repeats. Where two layers meet, the one whose texture stands higher there wins, so
// grass gives way to snow around its stones rather than in a smooth fade, and a low noise breaks every boundary up so
// no line runs straight across the mountains. The heights and the slope are params, so a script can move the snow line
// as the seasons turn. Far off, each layer is read again at farScale times the size and blended over as the ground
// recedes, so what a hillside a kilometre away shows is the texture's broad shapes, not a blur of its fine ones. The
// grass answers the weather: gusts of the world's wind run over it, laying it over as they pass.

#include "Include/Noise.hlsli"
#include "Include/Surface.hlsli"

// Metres from the camera over which the far reading of each layer takes over.
static const float FarFrom = 40.0;
static const float FarTo = 450.0;

// How many of the splats' lattice corners a repeat has along each way, how sharply the nearest corner's splat takes
// over from the others, and how much more one counts where its texture stands higher.
static const float SplatsPerRepeat = 2.0;
static const float SplatSharpness = 7.0;
static const float SplatContrast = 0.6;

// The most a cliff's splats turn either way, in whole turns, so the rock's grain stays about upright.
static const float SideTurn = 0.05;

struct MaterialParams
{
    Color tint : GiColor = Color(0.3, 0.33, 0.22, 1.0); // the terrain's colour as global illumination takes it
    Color grassTint = Color(1.0, 1.0, 1.0, 1.0);         // the grass texture's colour times this
    float farScale = 7.0;       // how many times larger each layer repeats far off
    float grassScale = 3.0;     // metres each layer's textures repeat over
    float forestScale = 4.0;
    float rockScale = 9.0;
    float snowScale = 5.0;
    float treeLine = 1900.0;    // metres above the world's zero: the forest floor thins out toward it
    float snowLine = 2700.0;    // where snow starts to lie, lower on slopes facing north
    float snowSoftness = 250.0; // over how many metres snow goes from none to all
    float rockSlope = 0.22;     // one less the up of the normal: where rock shows through, 0.13 is 30 degrees
    float rockSoftness = 0.07;
    TextureRef grassColor;
    TextureRef grassNormal;
    TextureRef grassOrm;
    TextureRef forestColor;
    TextureRef forestNormal;
    TextureRef forestOrm;
    TextureRef rockColor;
    TextureRef rockNormal;
    TextureRef rockOrm;
    TextureRef snowColor;
    TextureRef snowNormal;
    TextureRef snowOrm;
};

// One layer as read at a pixel: its colour, its normal in world space and its occlusion, roughness and metal.
struct Layer
{
    float3 color;
    float3 normal;
    float3 orm;
};

// A layer's maps at uv, in repeats, as they are.
Layer ReadLayer(TextureRef color, TextureRef normal, TextureRef orm, float2 uv, float2 uvDdx, float2 uvDdy)
{
    Layer layer;
    layer.color = SampleTextureGrad(color, uv, uvDdx, uvDdy).rgb;
    float2 slope = SampleTextureGrad(normal, uv, uvDdx, uvDdy).xy * 2.0 - 1.0;
    layer.normal = float3(slope, sqrt(saturate(1.0 - dot(slope, slope))));
    layer.orm = SampleTextureGrad(orm, uv, uvDdx, uvDdy).rgb;
    return layer;
}

// A layer's maps at uv, in repeats, laid as splats so their repeat never shows (Mikkelsen 2022, "Practical Real-Time
// Hex-Tiling"). uv falls in a triangle of a lattice laid over the plane, and at each of its corners the maps are laid
// again, moved anywhere in a repeat and turned either way by up to turn, in whole turns, each by a hash of the corner.
// Each is turned about its corner, so what is read stays near the texture's origin however far the world goes.
// The three are blended by how near uv is to each, sharpened, and by how high each stands there, as the layers are, so
// where they meet one wins rather than all three showing through washed out; a splat too far off to count is not read.
Layer SplatLayer(TextureRef color, TextureRef normal, TextureRef orm, float2 uv, float2 uvDdx, float2 uvDdy, float turn)
{
    // The plane skewed so the lattice's triangles are the halves of its cells.
    float2 skewed = float2(uv.x - uv.y * 0.57735027, uv.y * 1.15470054) * SplatsPerRepeat;
    float2 cell = floor(skewed);
    float2 inside = skewed - cell;
    float rest = 1.0 - inside.x - inside.y;
    bool upper = rest < 0.0;
    int s = upper ? 1 : 0;
    int2 corners[3] = { int2(cell) + int2(s, s), int2(cell) + int2(s, 1 - s), int2(cell) + int2(1 - s, s) };
    float3 nearness = upper ? float3(-rest, 1.0 - inside.y, 1.0 - inside.x) : float3(rest, inside.y, inside.x);
    float3 sharp = pow(nearness, SplatSharpness);
    float strongest = max(sharp.x, max(sharp.y, sharp.z));

    Layer splats[3];
    float3 weights = float3(0.0, 0.0, 0.0);
    [unroll] for (uint i = 0u; i < 3u; i++)
    {
        splats[i] = (Layer)0;
        [branch] if (sharp[i] > strongest * 0.01)
        {
            int2 corner = corners[i];
            uint hash = LatticeHash(int3(corner, 0));
            float angle = (HashToUnit(hash) * 2.0 - 1.0) * turn * TwoPi;
            float2 shift = float2(HashToUnit(PcgHash(hash)), HashToUnit(PcgHash(hash + 1u)));
            float sine = sin(angle);
            float cosine = cos(angle);
            float2x2 spin = float2x2(cosine, -sine, sine, cosine);
            float2 at = float2(corner.x + corner.y * 0.5, corner.y * 0.8660254) * (1.0 / SplatsPerRepeat);
            Layer splat = ReadLayer(color, normal, orm, mul(spin, uv - at) + shift, mul(spin, uvDdx), mul(spin, uvDdy));

            // The map's slope turned back with the splat, as the transpose undoes the turn.
            splat.normal.xy = mul(splat.normal.xy, spin);
            splats[i] = splat;
            weights[i] = sharp[i] * lerp(1.0, splat.orm.r, SplatContrast);
        }
    }

    weights /= max(dot(weights, 1.0), 1e-6);
    Layer layer;
    layer.color = splats[0].color * weights.x + splats[1].color * weights.y + splats[2].color * weights.z;
    layer.normal = splats[0].normal * weights.x + splats[1].normal * weights.y + splats[2].normal * weights.z;
    layer.orm = splats[0].orm * weights.x + splats[1].orm * weights.y + splats[2].orm * weights.z;
    return layer;
}

// A layer's maps along one of the world's axes: uv is the place projected on the plane across the axis, in repeats,
// laid as splats turned either way by up to turn, and as far as far says, the same at farScale times the size; only the
// readings that count are taken.
Layer SampleLayer(TextureRef color, TextureRef normal, TextureRef orm, float2 uv, float2 uvDdx, float2 uvDdy,
    float turn, float far, float farScale)
{
    Layer near = (Layer)0;
    Layer wide = (Layer)0;
    [branch] if (far < 0.999)
    {
        near = SplatLayer(color, normal, orm, uv, uvDdx, uvDdy, turn);
    }

    [branch] if (far > 0.001)
    {
        float shrink = 1.0 / farScale;
        wide = SplatLayer(color, normal, orm, uv * shrink, uvDdx * shrink, uvDdy * shrink, turn);
    }

    Layer layer;
    layer.color = lerp(near.color, wide.color, far);
    layer.normal = lerp(near.normal, wide.normal, far);
    layer.orm = lerp(near.orm, wide.orm, far);
    return layer;
}

// A layer projected from above: the world's x and z, the image's v running down toward -z as uvs start at the top
// left, so its green, up in the image, is toward -z. Seen from above, its splats turn any way.
Layer TopLayer(SurfaceInputs input, TextureRef color, TextureRef normal, TextureRef orm, float scale, float far,
    float farScale)
{
    float inverse = 1.0 / scale;
    Layer layer = SampleLayer(color, normal, orm, input.worldPosition.xz * inverse, input.positionDdx.xz * inverse,
        input.positionDdy.xz * inverse, 0.5, far, farScale);
    float3 n = input.normal;
    layer.normal = NormalizeOrZero(float3(layer.normal.x + n.x, n.y * layer.normal.z, -layer.normal.y + n.z));
    return layer;
}

// A layer projected from the three axes and blended by how squarely the ground faces each, for rock on cliffs; a
// projection it hardly faces is not read. The sides' v runs down the world, so the image stands upright on a cliff
// and its green is up.
Layer TriplanarLayer(SurfaceInputs input, TextureRef color, TextureRef normal, TextureRef orm, float scale,
    float3 blend, float far, float farScale)
{
    float inverse = 1.0 / scale;
    float3 p = input.worldPosition * inverse;
    float3 dx = input.positionDdx * inverse;
    float3 dy = input.positionDdy * inverse;
    float3 n = input.normal;
    Layer side = (Layer)0;
    Layer front = (Layer)0;
    Layer top = (Layer)0;
    [branch] if (blend.x > 0.001)
    {
        side = SampleLayer(color, normal, orm, float2(p.z, -p.y), float2(dx.z, -dx.y), float2(dy.z, -dy.y), SideTurn,
            far, farScale);
        side.normal = float3(n.x * side.normal.z, side.normal.y + n.y, side.normal.x + n.z);
    }

    [branch] if (blend.y > 0.001)
    {
        top = TopLayer(input, color, normal, orm, scale, far, farScale);
    }

    [branch] if (blend.z > 0.001)
    {
        front = SampleLayer(color, normal, orm, float2(p.x, -p.y), float2(dx.x, -dx.y), float2(dy.x, -dy.y), SideTurn,
            far, farScale);
        front.normal = float3(front.normal.x + n.x, front.normal.y + n.y, n.z * front.normal.z);
    }

    Layer layer;
    layer.color = side.color * blend.x + top.color * blend.y + front.color * blend.z;
    layer.orm = side.orm * blend.x + top.orm * blend.y + front.orm * blend.z;
    layer.normal = NormalizeOrZero(side.normal * blend.x + top.normal * blend.y + front.normal * blend.z);
    return layer;
}

// Weights from how much of each layer the ground has, sharpened by how high each texture stands there, its
// occlusion standing in for its height, so the higher one wins where they meet. A layer the ground has none of gets
// none, as it was not read.
float4 HeightBlend(float4 share, float4 heights)
{
    float4 raised = share * (heights + 0.25);
    float top = max(max(raised.x, raised.y), max(raised.z, raised.w));
    float4 kept = max(raised - (top - 0.3), 0.0) * step(0.001, share);
    return kept / max(dot(kept, 1.0), 1e-4);
}

// The grass in the world's wind: waves, short along the wind and long across it, run downwind at half its speed, as
// the air among the blades is slower than above them, forming and dying as they go, and strongest where the broader
// gusts they ride on blow. Where one passes, the blades are laid over, so the grass shows their paler, glossier sides
// and leans downwind. A breeze stirs it and a gale sends waves across every meadow. Where a pixel spans too much of a
// wave for it to show, the waves give way to how far they lay the grass over on average, so far meadows show the gusts
// without shimmering. Its height is left as it is, so the wind never moves where the layers meet.
void Sway(inout Layer grass, SurfaceInputs input)
{
    float2 wind = input.weather.wind.xz;
    float speed = length(wind);
    if (speed < 0.1)
    {
        return;
    }

    float2 along = wind / speed;
    float2 place = input.worldPosition.xz;
    float downwind = dot(place, along) - speed * 0.5 * input.frame.worldSec;
    float across = dot(place, float2(-along.y, along.x));
    float gust = GradientNoise(float3(downwind * (1.0 / 60.0), across * (1.0 / 150.0), input.frame.worldSec * 0.05));
    float crest = GradientNoise(float3(downwind * (1.0 / 8.0), across * (1.0 / 24.0), input.frame.worldSec * 0.3));
    float gusts = lerp(0.25, 1.0, saturate(gust * 1.5 + 0.5));
    float crests = saturate(crest * 2.0 + 0.4);
    float footprint = max(length(input.positionDdx), length(input.positionDdy));
    float waves = lerp(crests, 0.4, smoothstep(0.5, 2.0, footprint));
    float laid = gusts * waves * saturate(speed / 10.0);
    grass.color *= 1.0 + laid * 0.6;
    grass.orm.g *= 1.0 - laid * 0.35;
    grass.normal = NormalizeOrZero(grass.normal + float3(along.x, 0.0, along.y) * (laid * 0.6));
}

Surface EvaluateSurface(SurfaceInputs input, MaterialParams material)
{
    Surface surface = DefaultSurface(input);
    float3 n = input.normal;
    float height = input.worldPosition.y;
    float steep = 1.0 - n.y;

    // Low noise, kilometres and tens of metres across, breaks every boundary up.
    float broad = GradientNoise(input.worldPosition * (1.0 / 900.0));
    float fine = GradientNoise(input.worldPosition * (1.0 / 37.0));
    float north = -n.z;

    float rock = smoothstep(material.rockSlope - material.rockSoftness, material.rockSlope + material.rockSoftness,
        steep + fine * 0.05 + saturate((height - material.treeLine) / 1500.0) * 0.08);
    float snowAt = material.snowLine - north * 300.0 + broad * 220.0 + fine * 40.0;
    float snow = smoothstep(snowAt - material.snowSoftness, snowAt + material.snowSoftness, height)
        * (1.0 - smoothstep(0.45, 0.65, steep));
    float forest = saturate(broad * 2.0 - 0.1)
        * (1.0 - smoothstep(material.treeLine - 300.0, material.treeLine, height));
    float4 share = float4(1.0 - forest, forest, 0.0, 0.0) * (1.0 - rock);
    share.z = rock;
    share = share * (1.0 - snow) + float4(0.0, 0.0, 0.0, snow);

    float far = smoothstep(FarFrom, FarTo, length(input.worldPosition - input.cameraPosition));
    float farScale = material.farScale;
    Layer grass = (Layer)0;
    Layer forestFloor = (Layer)0;
    Layer stone = (Layer)0;
    Layer drift = (Layer)0;
    [branch] if (share.x > 0.001)
    {
        grass = TopLayer(input, material.grassColor, material.grassNormal, material.grassOrm, material.grassScale, far,
            farScale);
        grass.color *= material.grassTint.rgb;
        Sway(grass, input);
    }

    [branch] if (share.y > 0.001)
    {
        forestFloor = TopLayer(input, material.forestColor, material.forestNormal, material.forestOrm,
            material.forestScale, far, farScale);
    }

    [branch] if (share.z > 0.001)
    {
        float3 facing = pow(abs(n), 4.0);
        facing /= dot(facing, 1.0);
        stone = TriplanarLayer(input, material.rockColor, material.rockNormal, material.rockOrm, material.rockScale,
            facing, far, farScale);
    }

    [branch] if (share.w > 0.001)
    {
        drift = TopLayer(input, material.snowColor, material.snowNormal, material.snowOrm, material.snowScale, far,
            farScale);
    }

    float4 weights = HeightBlend(share, float4(grass.orm.r, forestFloor.orm.r, stone.orm.r, drift.orm.r));
    float3 color = grass.color * weights.x + forestFloor.color * weights.y + stone.color * weights.z
        + drift.color * weights.w;
    float3 orm = grass.orm * weights.x + forestFloor.orm * weights.y + stone.orm * weights.z + drift.orm * weights.w;
    float3 normal = grass.normal * weights.x + forestFloor.normal * weights.y + stone.normal * weights.z
        + drift.normal * weights.w;

    // The broad noise shades whole hillsides a little lighter or darker, so far ground does not read as one colour.
    surface.baseColor = color * (0.88 + 0.24 * saturate(broad * 0.5 + 0.5));
    surface.normal = NormalizeOrZero(normal);
    surface.occlusion = orm.r;
    surface.roughness = orm.g;
    surface.metallic = 0.0;
    return surface;
}
