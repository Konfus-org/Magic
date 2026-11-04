// What a ray reads where it lands: the surface cache. A point on an instance is looked up in the instance's cards,
// the side its normal faces most, where the card's depth agrees with the point, and read as the card's albedo under
// the lit card's irradiance there; a point the field found is first given to the nearest of the instances listed
// for its brick; and a ray marched along the screen's depth lands on the instance drawn there.
module "Scene/SurfaceCache";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Instances;
import Scene.Visibility;
import Scene.Cards;
import Scene.Field;
import Scene.Probes;
import Trace.IWorldTracer;

// The card of a capture that saw a point from its surface's side: the side the normal faces most, or the one after
// it where that side's depth disagrees with the point by more than the point's own slack; false when neither saw
// it, with the first side's card and place all the same.
bool FindCard(FrameConstants frame, uint capture, float3 modelPoint, float3 modelNormal, float slack, out uint cardIndex, out float2 uv)
{
    uint best = 0u;
    uint second = 0u;
    float bestDot = -2.0;
    float secondDot = -2.0;
    for (uint axis = 0u; axis < CardsPerCapture; axis++)
    {
        float facing = dot(modelNormal, GetCardDirection(axis));
        if (facing > bestDot)
        {
            second = best;
            secondDot = bestDot;
            best = axis;
            bestDot = facing;
        }
        else if (facing > secondDot)
        {
            second = axis;
            secondDot = facing;
        }
    }

    cardIndex = (capture * CardsPerCapture) + best;
    uv = float2(0.5);
    TextureRef depthAtlas = { frame.cardDepth };
    for (uint i = 0u; i < 2u; i++)
    {
        uint index = (capture * CardsPerCapture) + (i == 0u ? best : second);
        GpuCard card = LoadCard(frame.cards, index);
        float depth;
        float2 at = ProjectIntoCard(card, modelPoint, depth);
        if (i == 0u)
            uv = saturate(at);

        if (any(at < 0.0) || any(at >= 1.0))
            continue;

        uint2 texel = card.origin + min(uint2(at * float2(card.size)), card.size - 1u);
        float captured = depthAtlas.Load(texel, 0u).x;
        // Agreement within two texels across the card, five centimetres or the slack, whichever is most.
        float tolerance = max(max(2.0 * GetCardTexelWorld(card), 0.05), slack) / GetCardDepthRange(card);
        if (captured <= 0.0 || abs(captured - depth) > tolerance)
            continue;

        cardIndex = index;
        uv = at;
        return true;
    }

    return false;
}

// What leaves a point of an instance's surface, known to within slack metres: the card's albedo under its lit
// irradiance where the instance has a lit slot, under the fallback light where it has none, and nothing where it
// has no capture yet.
public float3 ShadeInstancePoint(FrameConstants frame, GiConstants gi, uint instanceIndex, GpuInstance instance, float3 world, float3 normal, float slack)
{
    if (instance.capture == 0u)
        return float3(0.0);

    float3x3 rotation = float3x3(instance.world[0].xyz, instance.world[1].xyz, instance.world[2].xyz);
    float3 modelPoint = mul(float4(world, 1.0), instance.inverseWorld).xyz;
    float3 modelNormal = NormalizeSafe(mul(rotation, normal), float3(0.0, 1.0, 0.0));
    uint cardIndex;
    float2 uv;
    float3 modelSlack = mul(float4(slack, 0.0, 0.0, 0.0), instance.inverseWorld).xyz;
    bool isSeen = FindCard(frame, instance.capture, modelPoint, modelNormal, length(modelSlack), cardIndex, uv);
    GpuCard card = LoadCard(frame.cards, cardIndex);
    SamplerRef sampler = { frame.cardSampler };
    TextureRef albedoAtlas = { frame.cardAlbedo };
    float3 albedo = albedoAtlas.SampleLevel(sampler, GetCardAtlasUv(card, uv), 0.0).rgb;
    uint slotPlusOne = GetLitSlot(gi, instanceIndex);
    if (!isSeen || slotPlusOne == 0u)
        return albedo * gi.fallbackIrradiance.rgb * InvPi;

    TextureRef pool = { gi.litPool };
    return albedo * InvPi * pool.SampleLevel(sampler, GetLitAtlasUv(slotPlusOne - 1u, card, uv), 0.0).rgb;
}

// The instance a point the field found lies on: of those listed for its brick, the one whose field reads nearest
// zero there; false when none is listed.
public bool FindFieldInstance(FrameConstants frame, FieldConstants field, float3 world, out uint instanceIndex)
{
    instanceIndex = 0u;
    for (uint level = 0u; level < FieldLevels; level++)
    {
        int3 voxel = GetFieldVoxel(field, level, world);
        if (!IsInFieldWindow(field, level, voxel))
            continue;

        uint cell = GetFieldBrickCell(level, GetFieldBrick(voxel));
        ArrayRef<uint> lists = { field.lists };
        uint count = min(lists[cell * (FieldInstancesPerBrick + 1u)], FieldInstancesPerBrick);
        if (count == 0u)
            return false;

        ArrayRef<GpuModelField> modelFields = { field.modelFields };
        VolumeRef atlas = { field.atlas };
        SamplerRef sampler = { field.sampler };
        float best = 1e30;
        for (uint i = 0u; i < count; i++)
        {
            uint index = lists[(cell * (FieldInstancesPerBrick + 1u)) + 1u + i];
            GpuInstance instance = LoadInstance(frame.instances, index);
            if (instance.flags == 0u)
                continue;

            GpuModelField modelField = modelFields[instance.flags - 1u];
            float3 model = mul(float4(world, 1.0), instance.inverseWorld).xyz;
            float3 extent = float(FieldModelVoxels - 1u) * modelField.voxel;
            float3 inField = clamp(model - modelField.origin, float3(0.0), extent);
            float3 outside = (model - modelField.origin) - inField;
            float3 texel = float3(GetModelFieldOrigin(instance.flags - 1u)) + inField / modelField.voxel + 0.5;
            float sampled = atlas.SampleLevel(sampler, texel / float(FieldAtlasVoxels), 0.0).x * modelField.band;
            float distance = (abs(sampled) + length(outside)) * GetMinScale(instance.world);
            if (distance < best)
            {
                best = distance;
                instanceIndex = index;
            }
        }

        return best < 1e30;
    }

    return false;
}

static const uint ScreenSteps = 12u;
static const float ScreenReach = 3.0;

// A ray marched along the screen's depth: true where it passes behind a drawn surface within its thickness, with
// what that surface sends back along the ray and how far along the ray it was.
public bool TraceScreenToSurface(FrameConstants frame, GiConstants gi, float3 origin, float3 direction, out float3 radiance, out float hitDistance)
{
    radiance = float3(0.0);
    hitDistance = 0.0;
    uint2 size = uint2(frame.width, frame.height);
    TextureRef hzb = { frame.hzb };
    float t = 0.05;
    for (uint step = 0u; step < ScreenSteps; step++)
    {
        t *= 1.45;
        if (t > ScreenReach)
            return false;

        float3 point = origin + direction * t;
        float4 clip = mul(float4(Relative(frame, point), 1.0), frame.viewProjection);
        if (clip.w <= frame.near)
            return false;

        float2 uv = float2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5);
        if (any(uv < 0.0) || any(uv >= 1.0))
            return false;

        uint2 pixel = min(uint2(uv * float2(size)), size - 1u);
        float sceneDepth = hzb.Load(pixel, 0u).x;
        if (sceneDepth <= 0.0)
            continue;

        float rayDepth = clip.z / clip.w;
        if (rayDepth >= sceneDepth)
            continue;

        float behind = ToViewDepth(rayDepth, frame.near) - ToViewDepth(sceneDepth, frame.near);
        if (behind > 0.2 + 0.1 * t)
            continue;

        UintTextureRef visibility = { frame.visibility };
        uint visibleIndex;
        uint triangle;
        if (!DecodeVisibilityId(visibility.Load(pixel, 0u), visibleIndex, triangle))
            return false;

        ArrayRef<VisibleCluster> visible = { frame.visibleClusters };
        uint instanceIndex = visible[visibleIndex].instance;
        GpuInstance instance = LoadInstance(frame.instances, instanceIndex);
        TextureRef normalTexture = { frame.gbufferNormal };
        float3 normal = DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0);
        float3 world = ReconstructWorld(frame, GetTexelCenterUv(pixel, size), sceneDepth);
        radiance = ShadeInstancePoint(frame, gi, instanceIndex, instance, world, normal, 0.0);
        hitDistance = t;
        return true;
    }

    return false;
}

// What leaves a point a trace found: on the instance the tracer named, else on the nearest of the instances listed
// for the point's brick. A point the field found is within a voxel of the surface, so that is its slack.
public float3 ShadeFieldHit(FrameConstants frame, FieldConstants field, GiConstants gi, float3 world, float3 normal, uint instanceHint)
{
    uint instanceIndex = instanceHint;
    if (instanceHint == TraceNoInstance && !FindFieldInstance(frame, field, world, instanceIndex))
        return float3(0.0);

    uint level;
    SampleField(field, world, level);
    float slack = instanceHint == TraceNoInstance ? field.voxelSizes[level] : 0.0;
    GpuInstance instance = LoadInstance(frame.instances, instanceIndex);
    return ShadeInstancePoint(frame, gi, instanceIndex, instance, world, normal, slack);
}
