// The impostors the cull left, one indirect draw each of a quad fitted to the instance's box, its instance in
// the draw's first instance. A pixel traces the atlas, writes the impostor's id and the depth of what it sees, and
// shows the impostor's share of the handoff's dither. The pass header's z is the impostor table.
module "DrawImpostors";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Instances;
import Scene.Impostors;

struct Varyings
{
    float4 position : SV_Position;
    float3 relative : RELATIVE;
    nointerpolation uint instance : INSTANCE;
    nointerpolation float fade : FADE;
};

struct Targets
{
    uint id : SV_Target0;
    // Never nearer than the quad, which is at the box's nearest corner, so the depth test still runs before the shader.
    float depth : SV_DepthLessEqual;
};

[shader("vertex")]
Varyings DrawImpostorsVertex(uint vertexIndex : SV_VertexID, uint instanceIndex : SV_StartInstanceLocation)
{
    FrameConstants frame = GetFrame();
    PassHeader header = GetPassHeader();
    GpuInstance instance = LoadInstance(frame.instances, instanceIndex);
    GpuImpostor impostor = LoadImpostor(header.z, instance.impostor);
    static const float2 corners[6] = { float2(0, 0), float2(1, 0), float2(1, 1), float2(0, 0), float2(1, 1), float2(0, 1) };

    Varyings output;
    output.relative = GetImpostorCorner(frame, instance, impostor, corners[vertexIndex]);
    output.position = mul(float4(output.relative, 1.0), frame.viewProjection);
    output.instance = instanceIndex;
    output.fade = GetImpostorFade(frame, instance);
    return output;
}

[shader("pixel")]
Targets DrawImpostorsPixel(Varyings input)
{
    if (GetDither(input.position.xy) >= input.fade)
        discard;

    FrameConstants frame = GetFrame();
    GpuInstance instance = LoadInstance(frame.instances, input.instance);
    GpuImpostor impostor = LoadImpostor(GetPassHeader().z, instance.impostor);
    ImpostorHit hit = TraceImpostor(instance, impostor, GetModelRay(frame, instance, input.relative), 0.5);
    if (!hit.isHit)
        discard;

    float4 clip = mul(float4(Relative(frame, ToWorld(instance.world, hit.position)), 1.0), frame.viewProjection);
    Targets output;
    output.id = EncodeImpostorId(input.instance, hit.slot);
    output.depth = min(clip.z / clip.w, input.position.z);
    return output;
}
