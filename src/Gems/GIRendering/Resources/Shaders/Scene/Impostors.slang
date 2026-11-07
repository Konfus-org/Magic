// PROTO: octahedral impostors. An instance whose model baked an atlas (the mesh preprocessor's ImpostorBuilder) draws
// as a quad fitted to its box once it is under a handoff's pixel radius on screen, the three views nearest the
// camera's direction blended; in the handoff its clusters draw too, each pixel shown by one of the two by a dither on
// the fade, so nothing pops (Brucks, "Octahedral Impostors", 2018).
module "Scene/Impostors";

import Core.Heap;
import Core.Pack;
import Core.Frame;
import Scene.Instances;

public static const uint ImpostorViews = 12u;
public static const uint ImpostorTile = 64u;

// The handoff runs from its start, a pixel radius on screen that a pass header's w holds as float bits, down to
// three quarters of it; a start of 0 draws no impostors.
public static const float ImpostorBand = 0.25;

// The visibility id's top bit marks an impostor's pixel, the next three the material slot the draw saw, the rest its
// instance: the resolve takes the slot from the id, as its own trace of the atlas can land on another texel.
public static const uint ImpostorFlag = 0x80000000u;
public static const uint ImpostorSlotShift = 28u;
public static const uint ImpostorInstanceMask = (1u << ImpostorSlotShift) - 1u;

public uint EncodeImpostorId(uint instance, uint slot) { return ImpostorFlag | ((slot & 7u) << ImpostorSlotShift) | instance; }

// The counts buffer's words the cull adds impostor draws at: those the old depth pyramid shows, and those the retest
// finds the new one shows.
public static const uint CountImpostorDraws = 16u;
public static const uint CountRetestImpostorDraws = 20u;

// The counts buffer's words the cull adds the draws of clusters in the handoff at, which draw with the dither, and the
// draw arguments' regions they go in, after the first and retested draws of the plain clusters.
public static const uint CountFadingDraws = 24u;
public static const uint CountRetestFadingDraws = 28u;

// A retest list entry's cluster for an instance whose impostor the old pyramid hid.
public static const uint RetestImpostor = 0xFFFFFFFFu;

// One model's impostor, C#'s GpuImpostor: the box around the model in its frame, the heap id of its atlas and of the
// atlas of the texture coordinates each texel shows.
public struct GpuImpostor
{
    public float3 boundsMin;
    public uint atlas;
    public float3 boundsMax;
    public uint uvAtlas;
}

public GpuImpostor LoadImpostor(uint table, uint row)
{
    ArrayRef<GpuImpostor> impostors = { table };
    return impostors[row];
}

// How far into the handoff an instance is, 0 for clusters alone, 1 for the impostor alone.
public float GetImpostorFade(FrameConstants frame, GpuInstance instance, uint startBits)
{
    float start = asfloat(startBits);
    if (instance.impostor == 0u || start <= 0.0)
        return 0.0;

    float pixels = instance.boundsRadius / max(length(Relative(frame, instance.boundsCenter)), 1e-3) * frame.pixelsPerRadian;
    return saturate((start - pixels) / (start * ImpostorBand));
}

// A threshold in [0, 1) for the pixel, even over the screen (Jimenez, interleaved gradient noise).
public float GetDither(float2 pixel) { return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715)))); }

struct ImpostorView
{
    float3 forward;
    float3 right;
    float3 up;
}

ImpostorView GetView(int2 grid)
{
    float2 flat = float2(grid) / float(ImpostorViews - 1u) * 2.0 - 1.0;
    ImpostorView view;
    view.forward = DecodeOctahedral(flat);
    float3 up0 = abs(view.forward.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0);
    view.right = normalize(cross(up0, view.forward));
    view.up = cross(view.forward, view.right);
    return view;
}

// The quad an impostor draws as, camera-relative: the corner at uv in [0, 1]² of the box's outline as the camera sees
// it, on the plane through the box's nearest corner, so the depth it writes is never nearer than the quad.
public float3 GetImpostorCorner(FrameConstants frame, GpuInstance instance, GpuImpostor impostor, float2 uv)
{
    float3 center = Relative(frame, instance.boundsCenter);
    float centerAlong = length(center);
    float3 forward = center / max(centerAlong, 1e-3);
    float3 up0 = abs(forward.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0);
    float3 right = normalize(cross(up0, forward));
    float3 up = cross(forward, right);

    // The box's eight corners, seen from the camera: their spread across and up on the plane through the centre,
    // each along its line of sight, and the nearest along the view.
    float2 low = float2(1e30, 1e30);
    float2 high = float2(-1e30, -1e30);
    float nearest = 1e30;
    for (uint i = 0u; i < 8u; i++)
    {
        float3 local = float3(
            (i & 1u) != 0u ? impostor.boundsMax.x : impostor.boundsMin.x,
            (i & 2u) != 0u ? impostor.boundsMax.y : impostor.boundsMin.y,
            (i & 4u) != 0u ? impostor.boundsMax.z : impostor.boundsMin.z
        );
        float3 corner = Relative(frame, ToWorld(instance.world, local));
        float along = max(dot(corner, forward), 1e-3);
        float2 onPlane = float2(dot(corner, right), dot(corner, up)) * (centerAlong / along);
        low = min(low, onPlane);
        high = max(high, onPlane);
        nearest = min(nearest, along);
    }

    // The outline on the centre's plane, slid along the lines of sight onto the nearest corner's.
    float2 at = lerp(low, high, uv);
    float3 onCenter = right * at.x + up * at.y + forward * centerAlong;
    return onCenter * (max(nearest, 1e-3) / centerAlong);
}

// What the ray from origin along direction, in the model's frame, sees of the impostor: a model point and normal and
// the material slot, isHit false where less than minCoverage of the views' weight sees something. The draw asks for
// half, so a silhouette is where the views agree; the resolve, whose ray can differ from the draw's by a jitter, for any,
// so a pixel the draw kept always finds its surface.
public struct ImpostorHit
{
    public bool isHit;
    public float3 position;
    public float3 normal;
    public uint slot;
    // The atlas texel the strongest view saw the surface at.
    public uint2 texel;
}

public ImpostorHit TraceImpostor(GpuInstance instance, GpuImpostor impostor, ImpostorRay ray, float minCoverage)
{
    float3 origin = ray.origin;
    float3 direction = ray.direction;
    float3 center = ToWorld(instance.inverseWorld, instance.boundsCenter);
    float radius = instance.boundsRadius / GetMaxScale(instance.world);

    // The three grid views around the camera's direction from the model, and their weights.
    float2 grid = (EncodeOctahedral(normalize(origin - center)) * 0.5 + 0.5) * float(ImpostorViews - 1u);
    int2 cell = min(int2(floor(grid)), int2(int(ImpostorViews) - 2));
    float2 f = grid - float2(cell);
    int2 views[3];
    float weights[3];
    if (f.x + f.y < 1.0)
    {
        views[0] = cell;
        views[1] = cell + int2(1, 0);
        views[2] = cell + int2(0, 1);
        weights[0] = 1.0 - f.x - f.y;
        weights[1] = f.x;
        weights[2] = f.y;
    }
    else
    {
        views[0] = cell + int2(1, 1);
        views[1] = cell + int2(0, 1);
        views[2] = cell + int2(1, 0);
        weights[0] = f.x + f.y - 1.0;
        weights[1] = 1.0 - f.x;
        weights[2] = 1.0 - f.y;
    }

    TextureRef atlas = { impostor.atlas };
    float coverage = 0.0;
    float3 normal = 0.0;
    float best = -1.0;
    ImpostorHit hit;
    hit.isHit = false;
    hit.position = center;
    hit.normal = float3(0.0, 1.0, 0.0);
    hit.slot = 0u;
    hit.texel = uint2(0u, 0u);
    for (int i = 0; i < 3; i++)
    {
        // The ray where it crosses the plane through the centre facing the view, in the view's tile.
        ImpostorView view = GetView(views[i]);
        float facing = dot(direction, view.forward);
        if (abs(facing) < 1e-4)
            continue;

        float3 onPlane = origin + direction * (dot(center - origin, view.forward) / facing) - center;
        float2 st = float2(dot(onPlane, view.right), dot(onPlane, view.up)) / radius;
        if (any(abs(st) >= 1.0))
            continue;

        uint2 inTile = min(uint2(float2(st.x + 1.0, 1.0 - st.y) * 0.5 * float(ImpostorTile)), ImpostorTile - 1u);
        uint2 atlasTexel = uint2(views[i]) * ImpostorTile + inTile;
        float4 texel = atlas.Load(atlasTexel, 0u);
        if (texel.w < 0.5)
            continue;

        coverage += weights[i];
        normal += DecodeOctahedral(texel.xy) * weights[i];
        if (weights[i] > best)
        {
            best = weights[i];
            hit.slot = uint(texel.w + 0.5) - 1u;
            hit.texel = atlasTexel;

            // Where the pixel's own ray reaches the depth the view saw along its axis, so the point stays on the ray.
            float along = radius - texel.z * 2.0 * radius;
            hit.position = origin + direction * ((along + dot(center - origin, view.forward)) / facing);
        }
    }

    hit.isHit = coverage >= max(minCoverage, 1e-4);
    if (hit.isHit)
        hit.normal = normalize(normal);

    // The atlas places the surface to within a texel, so the point moves out by two, off the model's own geometry
    // that shadow rays trace from what the depth says.
    hit.position += hit.normal * (4.0 * radius / float(ImpostorTile));
    return hit;
}

// The texture coordinates of the surface an impostor's atlas texel shows, and how they change a texel across and a
// texel down its view.
public struct ImpostorUv
{
    public float2 uv;
    public float2 perTexelX;
    public float2 perTexelY;
}

public ImpostorUv LoadImpostorUv(GpuImpostor impostor, uint2 texel)
{
    TextureRef uvs = { impostor.uvAtlas };
    ImpostorUv result;
    result.uv = uvs.Load(texel, 0u).xy;
    result.perTexelX = GetUvStep(impostor, texel, int2(1, 0), result.uv);
    result.perTexelY = GetUvStep(impostor, texel, int2(0, 1), result.uv);
    return result;
}

// How the texture coordinates change a texel along step: towards the neighbour the view also saw a surface at, else
// from the one opposite; none at a texel alone.
float2 GetUvStep(GpuImpostor impostor, uint2 texel, int2 step, float2 uv)
{
    TextureRef atlas = { impostor.atlas };
    TextureRef uvs = { impostor.uvAtlas };
    int2 inTile = int2(texel % ImpostorTile);
    int2 next = inTile + step;
    if (all(next >= 0) && all(next < int(ImpostorTile)) && atlas.Load(uint2(int2(texel) + step), 0u).w >= 0.5)
        return uvs.Load(uint2(int2(texel) + step), 0u).xy - uv;

    int2 back = inTile - step;
    if (all(back >= 0) && all(back < int(ImpostorTile)) && atlas.Load(uint2(int2(texel) - step), 0u).w >= 0.5)
        return uv - uvs.Load(uint2(int2(texel) - step), 0u).xy;

    return float2(0.0, 0.0);
}

// A ray in an instance's model frame: where it starts and its unit direction.
public struct ImpostorRay
{
    public float3 origin;
    public float3 direction;
}

// The ray through a camera-relative point, from the camera, in the instance's model frame.
public ImpostorRay GetModelRay(FrameConstants frame, GpuInstance instance, float3 relative)
{
    ImpostorRay ray;
    ray.origin = ToWorld(instance.inverseWorld, frame.cameraPosition.xyz);
    float3 through = ToWorld(instance.inverseWorld, frame.cameraPosition.xyz + relative);
    ray.direction = normalize(through - ray.origin);
    return ray;
}
