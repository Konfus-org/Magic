// An impostor's card: the last LOD of a model, a quad that always faces the viewer about the model's up axis and shows
// the model as baked from eight directions (twin of Gems/MeshLods/ImpostorBaker.cs, which says how). A card's vertex
// says it is one (Magic/Contexts/Assets/ImpostorCard.cs): its tangent's w is ImpostorCardTag plus the mesh it shows (a
// mesh vertex's is the bitangent sign, +1 or -1), its normal holds the corner (x, y, each -1 or 1) and half the square's
// side (z), its position is that corner of the square facing +Z, its tangent's z the card's turn about the up axis
// (radians: a stand-in's cards each turned as their object was), and its tangent's x and y its two atlases' packed
// texture references (TextureRef, as floats), which the renderer writes over the model id the asset has there.
//
// Frame k looks from (sin k45, 0, cos k45) towards the model, with (-cos k45, 0, sin k45) as its right and +Y as its up,
// in cell (k % 3, k / 3) of a 3 x 3 grid of 85-texel cells in a 256-texel atlas. The surface atlas holds the surface's
// uv (r, g) and coverage (b), the normal atlas the model-space normal (r, g, b, from -1..1). A card shows the two
// frames either side of the direction it is seen from, dithered between them.

#ifndef MAGIC_IMPOSTOR_HLSLI
#define MAGIC_IMPOSTOR_HLSLI

#include "Include/Structs.hlsli"

static const float ImpostorCardTag = 4.0;
static const uint ImpostorFrames = 8u;
static const uint ImpostorGrid = 3u;
static const float ImpostorAtlasSize = 256.0;
static const float ImpostorFrameSize = 85.0;

bool IsImpostorCard(float4 tangent)
{
    return tangent.w > ImpostorCardTag * 0.5;
}

// A card's corner for an instance: where it goes in the world, the baked model's x axis in the world (its y is the
// instance's), where it is in its frame (0..1, y down), the two frames it shows (a | b << 8) and how much of b.
struct ImpostorCorner
{
    float3 position;
    float3 axisX;
    float2 frameUv;
    uint frames;
    float blend;
};

float3 ImpostorPlace(GpuInstanceXform world, float3 position)
{
    float4 point4 = float4(position, 1.0);
    return float3(dot(world.r0, point4), dot(world.r1, point4), dot(world.r2, point4));
}

// The corner of a card drawn with the instance's transform, facing a viewer about the model's up axis: the viewer is a
// place in the world (w 1) or, for an orthographic view, the direction towards it (w 0). push moves the card away from
// the viewer, in units of half its side: a shadow's card is pushed back by one, behind every point of the camera's
// card, which would otherwise fall in its own shadow.
ImpostorCorner ImpostorCornerOf(GpuInstanceXform world, float3 cardPosition, float3 cardNormal, float4 cardTangent, float4 viewer, float push)
{
    float2 corner = cardNormal.xy;
    float half = cardNormal.z;
    float3 center = cardPosition - float3(corner * half, 0.0);

    // The baked model's x and z axes in the instance's space, turned by the card's yaw about y.
    float2 turn;
    sincos(cardTangent.z, turn.y, turn.x);
    float3 axisX = float3(turn.x, 0.0, -turn.y);
    float3 axisZ = float3(turn.y, 0.0, turn.x);

    // The viewer's direction in the instance's space (x, z), through its axes in the world, then in the baked model's.
    float3 toViewer = viewer.w > 0.5 ? viewer.xyz - ImpostorPlace(world, center) : viewer.xyz;
    float2 along = float2(dot(float3(world.r0.x, world.r1.x, world.r2.x), toViewer), dot(float3(world.r0.z, world.r1.z, world.r2.z), toViewer));
    along = dot(along, along) > 1e-12 ? normalize(along) : float2(0.0, 1.0);
    float2 flat = float2(along.x * axisX.x + along.y * axisX.z, along.x * axisZ.x + along.y * axisZ.z);
    float3 right = axisX * -flat.y + axisZ * flat.x;

    float place = frac(atan2(flat.x, flat.y) / 6.28318530718) * (float)ImpostorFrames;
    uint a = (uint)place % ImpostorFrames;

    ImpostorCorner result;
    result.position = ImpostorPlace(world, center + right * (corner.x * half) + float3(0.0, corner.y * half, 0.0) - float3(along.x, 0.0, along.y) * (push * half));
    result.axisX = float3(dot(world.r0.xyz, axisX), dot(world.r1.xyz, axisX), dot(world.r2.xyz, axisX));
    result.frameUv = float2(corner.x * 0.5 + 0.5, 0.5 - corner.y * 0.5);
    result.frames = a | (((a + 1u) % ImpostorFrames) << 8u);
    result.blend = frac(place);
    return result;
}

// The two atlases' references, as a card's vertex carries them.
uint2 ImpostorAtlases(float4 tangent)
{
    return uint2((uint)(tangent.x + 0.5), (uint)(tangent.y + 0.5));
}

// The frame a pixel shows: b where the dither value is under the blend, else a.
uint ImpostorFrame(uint frames, float blend, float dither)
{
    return dither < blend ? (frames >> 8u) & 0xFFu : frames & 0xFFu;
}

// Where in the atlas a point of a frame is, kept half a texel inside its cell so filtering never reads the next one.
float2 ImpostorAtlasUv(uint frame, float2 frameUv)
{
    float2 cell = float2((float)(frame % ImpostorGrid), (float)(frame / ImpostorGrid));
    float2 texel = clamp(frameUv * ImpostorFrameSize, 0.5, ImpostorFrameSize - 0.5);
    return (cell * ImpostorFrameSize + texel) / ImpostorAtlasSize;
}

#ifdef MAGIC_SURFACE_HLSLI
// What a card shows at a point of a frame: the surface's uv, its coverage and its model-space normal. Level 0 only:
// the atlases' mips would average the baked numbers with the empty texels around them.
struct ImpostorTexel
{
    float2 uv;
    float coverage;
    float3 normal;
};

ImpostorTexel ImpostorSample(uint2 atlases, uint frame, float2 frameUv)
{
    float2 at = ImpostorAtlasUv(frame, frameUv);
    float4 surface = SampleTextureGrad(atlases.x, at, float2(0.0, 0.0), float2(0.0, 0.0));
    float4 normal = SampleTextureGrad(atlases.y, at, float2(0.0, 0.0), float2(0.0, 0.0));

    ImpostorTexel texel;
    texel.uv = surface.rg;
    texel.coverage = surface.b;
    float3 decoded = normal.rgb * 2.0 - 1.0;
    texel.normal = dot(decoded, decoded) > 1e-6 ? normalize(decoded) : float3(0.0, 1.0, 0.0);
    return texel;
}
#endif

#endif
