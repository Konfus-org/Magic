// The impostors the cull left, one indirect draw each of a quad fitted to the instance's box, its instance in
// the draw's first instance. The vertices pick the atlas views the instance blends; a pixel traces them, writes the
// impostor's id and the depth of what it sees, and shows the impostor's share of the handoff's dither, or all of it
// when the first instance carries ImpostorWhole.
module "DrawImpostors";

import Core.Heap;
import Core.Frame;
import Scene.Instances;
import Scene.Impostors;
import Scene.Clusters;
import Scene.Models;

struct Varyings
{
    float4 position : SV_Position;
    float3 relative : RELATIVE;
    nointerpolation uint instance : INSTANCE;
    nointerpolation float fade : FADE;
    nointerpolation uint3 viewCells : VIEW_CELLS;
    nointerpolation float3 viewWeights : VIEW_WEIGHTS;
};

struct Targets
{
    uint id : SV_Target0;
    // Never nearer than the quad, which is at the box's nearest corner, so the depth test still runs before the shader.
    float depth : SV_DepthLessEqual;
};

[shader("vertex")]
Varyings DrawImpostorsVertex(uint vertexIndex : SV_VertexID, uint start : SV_StartInstanceLocation)
{
    uint instanceIndex = start & ~ImpostorWhole;
    FrameConstants frame = GetFrame();
    GpuInstance instance = LoadInstance(GetModels().instances, instanceIndex);
    GpuImpostor impostor = LoadImpostor(LoadClusterModel(instance.model).impostor);
    static const float2 corners[6] = { float2(0, 0), float2(1, 0), float2(1, 1), float2(0, 0), float2(1, 1), float2(0, 1) };

    Varyings output;
    output.relative = GetImpostorCorner(frame, instance, impostor, corners[vertexIndex]);
    output.position = mul(float4(output.relative, 1.0), frame.viewProjection);
    output.instance = instanceIndex;
    output.fade = (start & ImpostorWhole) != 0u ? 1.0 : GetImpostorFade(frame, instance);
    ImpostorBlend blend = GetImpostorBlend(frame, instance);
    output.viewCells = blend.cells;
    output.viewWeights = blend.weights;
    return output;
}

[shader("pixel")]
Targets DrawImpostorsPixel(Varyings input)
{
    if (GetDither(input.position.xy) >= input.fade)
        discard;

    FrameConstants frame = GetFrame();
    GpuInstance instance = LoadInstance(GetModels().instances, input.instance);
    GpuImpostor impostor = LoadImpostor(LoadClusterModel(instance.model).impostor);
    ImpostorBlend blend = { input.viewCells, input.viewWeights };
    ImpostorHit hit = TraceImpostor(instance, impostor, blend, GetModelRay(frame, instance, input.relative), 0.5);
    if (!hit.isHit)
        discard;

    float3 relative = ToCameraRelative(frame, ToWorld(instance.world, hit.position));
    float4 clip = mul(float4(relative, 1.0), frame.viewProjection);
    Targets output;
    output.id = EncodeImpostorId(input.instance, hit.slot);
    output.depth = min(clip.z / clip.w, input.position.z);
    return output;
}
