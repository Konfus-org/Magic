// Every pixel of the visibility buffer to a surface: the id names the triangle, its three vertices give the pixel its
// barycentrics and their derivatives, the attributes interpolate from them, and the material decides the surface,
// written to the GBuffer. The entry is specialised to one material shader, numbered in the pass header's z, and
// resolves only the pixels whose rows that shader draws, loading its struct from each; every pass writes the sky's
// velocity, the same each time. The pixel's velocity is written beside it: where the surface was on last frame's
// screen, from where its instance was and where the camera was; a pixel of sky moves with the camera alone.
module "ResolveMaterial";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Instances;
import Scene.Materials;
import Scene.Visibility;
import Scene.Impostors;
import Surface.Surface;
import Material.IMaterial;

[shader("compute")]
[numthreads(8, 8, 1)]
void ResolveMaterial<M : IMaterial>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    UintTextureRef visibility = { frame.visibility };
    uint visibleIndex = 0u;
    uint triangle = 0u;
    // The pixel's centre is where the jittered view saw its surface, which is unjittered where the camera saw it.
    float2 center = GetTexelCenterUv(thread.xy, size);
    float2 unjittered = center - frame.jitter.xy / float2(size);
    StorageRef velocityTarget = { frame.velocity, 0u };
    uint id = visibility.Load(thread.xy, 0u);
    bool isImpostor = (id & ImpostorFlag) != 0u;
    uint shader = GetPassHeader().z;
    if (!isImpostor && !DecodeVisibilityId(id, visibleIndex, triangle))
    {
        float3 far = ReconstructRelative(frame, center, 1e-6);
        float4 previousClip = mul(float4(far, 1.0), frame.previousViewProjection);
        float2 previousUv = ToUv(previousClip.xy / max(previousClip.w, Epsilon));
        velocityTarget.Store(thread.xy, float4(previousUv - unjittered, 0.0, 0.0));
        return;
    }

    // The surface's inputs, the point in its model's frame, its instance and its material slot: from the triangle the
    // id names, or, for an impostor's pixel, from its atlas traced again along the pixel's ray, with the slot the
    // draw saw.
    SurfaceInputs inputs;
    float3 model;
    GpuInstance instance;
    uint slot;
    if (isImpostor)
    {
        instance = LoadInstance(frame.instances, id & ImpostorInstanceMask);
        slot = (id >> ImpostorSlotShift) & 7u;

        // Another pass's pixel goes before its atlas is traced.
        if (LoadMaterialShader(frame.materials, GetMaterial(instance, slot)) != shader)
            return;

        ImpostorRay ray = GetModelRay(frame, instance, ReconstructRelative(frame, center, 0.5));
        GpuImpostor impostor = LoadImpostor(GetPassHeader().y, instance.impostor);
        ImpostorHit hit = TraceImpostor(instance, impostor, ray, 0.0);
        model = hit.position;
        inputs.position = Relative(frame, ToWorld(instance.world, hit.position));
        inputs.normal = ToWorldNormal(instance.world, hit.normal);
        float3 side = abs(inputs.normal.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
        inputs.tangent = normalize(cross(side, inputs.normal));
        inputs.tangentSign = 1.0;

        // The texture coordinates the atlas holds at the texel, and their change over a screen pixel: a texel's change
        // times how many atlas texels the pixel spans, a pixel's size over a texel's at the instance's distance.
        ImpostorUv uv = LoadImpostorUv(impostor, hit.texel);
        float pixel = length(inputs.position) / frame.pixelsPerRadian;
        float atlasTexel = 2.0 * instance.boundsRadius / float(ImpostorTile);
        inputs.uv = uv.uv;
        inputs.uvDdx = uv.perTexelX * (pixel / atlasTexel);
        inputs.uvDdy = uv.perTexelY * (pixel / atlasTexel);
        inputs.viewDirection = NormalizeSafe(-inputs.position, float3(0.0, 0.0, -1.0));
    }
    else
    {
        PixelTriangle shown = LoadPixelTriangle(frame, visibleIndex, triangle, ToClip(center));
        instance = shown.instance;
        model = Interpolate(shown.v0.position, shown.v1.position, shown.v2.position, shown.bary.lambda);
        slot = shown.cluster.materialSlot;
        inputs = CreateSurfaceInputs(shown);
    }

    uint row = GetMaterial(instance, slot);
    if (LoadMaterialShader(frame.materials, row) != shader)
        return;

    M material = LoadMaterialParams<M>(frame.materials, row);
    SurfaceOutputs surface = material.Evaluate(inputs);

    float3 previous = ToWorld(GetPreviousWorld(instance, frame.frameIndex), model) - frame.previousCameraPosition.xyz;
    float4 previousClip = mul(float4(previous, 1.0), frame.previousViewProjection);
    float2 previousUv = ToUv(previousClip.xy / max(previousClip.w, Epsilon));
    velocityTarget.Store(thread.xy, float4(previousUv - unjittered, previousClip.w, 0.0));

    StorageRef normalTarget = { frame.gbufferNormal, 0u };
    StorageRef baseTarget = { frame.gbufferBase, 0u };
    StorageRef surfaceTarget = { frame.gbufferSurface, 0u };
    normalTarget.Store(thread.xy, float4(EncodeOctahedral(surface.normal) * 0.5 + 0.5, 0.0, 0.0));
    baseTarget.Store(thread.xy, float4(surface.baseColor, surface.metalness));
    surfaceTarget.Store(thread.xy, float4(surface.roughness, 0.5, surface.occlusion, 0.0));
}
