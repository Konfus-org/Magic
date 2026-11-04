// What a ray reads where it lands: the surface cache. A point on an instance is looked up in the instance's lit
// card, the capture cell that saw the surface from its normal's side, where the cell's depth agrees with the
// point; a point the field found is first given to the nearest of the instances listed for its brick.
module "Scene/SurfaceCache";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Instances;
import Scene.Captures;
import Scene.Field;
import Scene.Probes;

// The axes of a capture cell's view, as the renderer sets them up: towards the viewer along direction, the view
// looking back along it.
public void GetCaptureBasis(float3 direction, out float3 right, out float3 up, out float3 forward)
{
    float3 worldUp = abs(direction.y) > 0.9 ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
    forward = -direction;
    right = normalize(cross(worldUp, forward));
    up = cross(forward, right);
}

// Where a point of the model lands in a capture cell seen from a direction: the cell's texture coordinates from its
// top left, and the reversed depth the capture would hold there.
public float2 ProjectIntoCapture(GpuCapture capture, float3 direction, float3 modelPoint, out float depth)
{
    float3 right;
    float3 up;
    float3 forward;
    GetCaptureBasis(direction, right, up, forward);
    float radius = max(capture.boundsRadius, 1e-3);
    float3 eye = capture.boundsCenter + direction * 2.0 * radius;
    float3 offset = modelPoint - eye;
    float3 view = float3(dot(offset, right), dot(offset, up), dot(offset, forward));
    depth = 1.5 - view.z / (2.0 * radius);
    return float2(view.x / radius * 0.5 + 0.5, 0.5 - view.y / radius * 0.5);
}

// The texel of the lit pool a point of an instance is read at, from the capture cell that saw the surface it lies
// on; false when no cell did.
bool FindLitTexel(GpuCapture capture, uint slot, float3 modelPoint, float3 modelNormal, float3 modelRayDirection, out float2 texel)
{
    texel = float2(0.0);
    TextureRef depthAtlas = { capture.depth };
    float3 candidates[2] = { modelNormal, -modelRayDirection };
    for (uint i = 0u; i < 2u; i++)
    {
        float3 direction = NormalizeSafe(candidates[i], float3(0.0, 1.0, 0.0));
        float2 flat = EncodeOctahedral(direction) * 0.5 + 0.5;
        uint2 cell = min(uint2(flat * float(CaptureCells)), CaptureCells - 1u);
        float3 cellDirection = GetCaptureDirection(cell);
        float depth;
        float2 cellUv = ProjectIntoCapture(capture, cellDirection, modelPoint, depth);
        if (any(cellUv < 0.0) || any(cellUv >= 1.0))
            continue;

        uint2 captureTexel = (cell * CaptureCellSize) + uint2(cellUv * float(CaptureCellSize));
        float captured = depthAtlas.Load(captureTexel, 0u).x;
        // Depth runs 1 at the near face of the capture to 0 at the far: a difference of 0.05 is a tenth of the radius.
        if (captured <= 0.0 || abs(captured - depth) > 0.05)
            continue;

        texel = float2(GetLitSlotOrigin(slot)) + (float2(cell) + cellUv) * float(LitCellTexels);
        return true;
    }

    return false;
}

// What leaves a point of an instance's surface towards a ray that arrived along a direction: its lit card where it
// has one, else its capture's albedo under the fallback light, else nothing.
public float3 ShadeInstancePoint(FrameConstants frame, GiConstants gi, uint instanceIndex, GpuInstance instance, float3 world, float3 normal, float3 rayDirection)
{
    if (instance.capture == 0u)
        return float3(0.0);

    GpuCapture capture = LoadCapture(frame.captures, instance.capture);
    float3x3 rotation = float3x3(instance.world[0].xyz, instance.world[1].xyz, instance.world[2].xyz);
    float3 modelPoint = mul(float4(world, 1.0), instance.inverseWorld).xyz;
    float3 modelNormal = NormalizeSafe(mul(rotation, normal), float3(0.0, 1.0, 0.0));
    float3 modelRay = NormalizeSafe(mul(rotation, rayDirection), -modelNormal);
    uint slotPlusOne = GetLitSlot(gi, instanceIndex);
    SamplerRef sampler = { frame.captureSampler };
    if (slotPlusOne != 0u)
    {
        float2 texel;
        if (FindLitTexel(capture, slotPlusOne - 1u, modelPoint, modelNormal, modelRay, texel))
        {
            TextureRef pool = { gi.litPool };
            return pool.SampleLevel(sampler, texel / float(LitPoolTexels), 0.0).rgb;
        }
    }

    // No lit card: the albedo the capture saw from the normal's side under the fallback light.
    float2 flat = EncodeOctahedral(modelNormal) * 0.5 + 0.5;
    uint2 cell = min(uint2(flat * float(CaptureCells)), CaptureCells - 1u);
    float depth;
    float2 cellUv = saturate(ProjectIntoCapture(capture, GetCaptureDirection(cell), modelPoint, depth));
    TextureRef albedoAtlas = { capture.albedo };
    float4 albedo = albedoAtlas.SampleLevel(sampler, (float2(cell) + cellUv) / float(CaptureCells), 0.0);
    return albedo.rgb * gi.fallbackIrradiance.rgb * InvPi;
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

// What leaves a point the field found, towards a ray that arrived along a direction.
public float3 ShadeFieldHit(FrameConstants frame, FieldConstants field, GiConstants gi, float3 world, float3 normal, float3 rayDirection)
{
    uint instanceIndex;
    if (!FindFieldInstance(frame, field, world, instanceIndex))
        return float3(0.0);

    GpuInstance instance = LoadInstance(frame.instances, instanceIndex);
    return ShadeInstancePoint(frame, gi, instanceIndex, instance, world, normal, rayDirection);
}
