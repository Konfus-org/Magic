// Every pixel of the visibility buffer to a surface: the id names the triangle, its three vertices give the pixel its
// barycentrics and their derivatives, the attributes interpolate from them, and the material decides the surface,
// written to the GBuffer, with how far the pixel's point rises off its flat triangle; and the nearest translucent
// surface of the pixel, from the glass's visibility buffer, to the glass targets. The entry is specialised to one
// material shader, numbered in the pass header's z, and resolves only the pixels whose rows that shader draws, loading
// its struct from each; every pass writes the sky's velocity, the same each time. The pixel's velocity is written
// beside it: where the surface was on last frame's screen, from where its instance was and where the camera was; a
// pixel of sky moves with the camera alone.
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
import Scene.Clusters;
import Scene.Models;

// How far a point must rise along a vertex's normal to reach the plane through the vertex square to it, when it lies
// below that plane.
float3 GetLift(float3 fromVertex, float3 normal) { return -min(0.0, dot(fromVertex, normal)) * normal; }

// How far a pixel's point rises off its flat triangle towards the curved surface the triangle's vertex normals
// describe, so that a ray from the lit side of a curve doesn't hit the next triangle and shadow the curve a facet at a
// time (Hanika, "Hacking the Shadow Terminator", Ray Tracing Gems II, 2021). A point in a hollow stays where it is.
float GetTriangleLift(PixelTriangle shown, float3 relative)
{
    float3 lambda = shown.bary.lambda;
    return length(
        lambda.x * GetLift(relative - shown.p0, ToWorldNormal(shown.instance.world, shown.v0.normal))
        + lambda.y * GetLift(relative - shown.p1, ToWorldNormal(shown.instance.world, shown.v1.normal))
        + lambda.z * GetLift(relative - shown.p2, ToWorldNormal(shown.instance.world, shown.v2.normal))
    );
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ResolveMaterial<M : IMaterial>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    ResolveOpaque<M>(frame, thread.xy);
    ResolveTranslucent<M>(frame, thread.xy);
}

// The pixel's opaque surface into the GBuffer and its velocity, or the sky's velocity where nothing was drawn.
void ResolveOpaque<M : IMaterial>(FrameConstants frame, uint2 pixel)
{
    Models models = GetModels();
    uint2 size = uint2(frame.width, frame.height);
    UintTextureRef visibility = { GetClusters().visibility };
    uint visibleIndex = 0u;
    uint triangle = 0u;
    // The pixel's centre is where the jittered view saw its surface, which is unjittered where the camera saw it.
    float2 center = GetTexelCenterUv(pixel, size);
    float2 unjittered = center - frame.jitter.xy / float2(size);
    StorageRef velocityTarget = { frame.velocity, 0u };
    uint id = visibility.Load(pixel, 0u);
    bool isImpostor = (id & ImpostorFlag) != 0u;
    uint shader = GetPassHeader().z;
    if (!isImpostor && !DecodeVisibilityId(id, visibleIndex, triangle))
    {
        float3 far = ReconstructRelative(frame, center, 1e-6);
        float4 previousClip = mul(float4(far, 1.0), frame.previousViewProjection);
        float2 previousUv = ToUv(previousClip.xy / max(previousClip.w, Epsilon));
        velocityTarget.Store(pixel, float4(previousUv - unjittered, 0.0, 0.0));
        return;
    }

    // The surface's inputs, the point in its model's frame, its instance and its material slot: from the triangle the
    // id names, or, for an impostor's pixel, from its atlas traced again along the pixel's ray, with the slot the
    // draw saw. An impostor has no triangle to rise off.
    SurfaceInputs inputs;
    float3 model;
    GpuInstance instance;
    uint slot;
    float lift = 0.0;
    if (isImpostor)
    {
        instance = LoadInstance(models.instances, id & ImpostorInstanceMask);
        slot = (id >> ImpostorSlotShift) & 7u;

        // Another pass's pixel goes before its atlas is traced.
        if (LoadMaterialShader(models.materials, GetMaterial(instance, slot)) != shader)
            return;

        ImpostorRay ray = GetModelRay(frame, instance, ReconstructRelative(frame, center, 0.5));
        GpuImpostor impostor = LoadImpostor(GetPassHeader().y, LoadClusterModel(instance.model).impostor);
        ImpostorHit hit = TraceImpostor(instance, impostor, GetImpostorBlend(frame, instance), ray, 0.0);
        model = hit.position;
        inputs.position = Relative(frame, ToWorld(instance.world, hit.position));
        inputs.normal = ToWorldNormal(instance.world, hit.normal);
        float3 side = abs(inputs.normal.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
        inputs.tangent = normalize(cross(side, inputs.normal));
        inputs.tangentSign = 1.0;

        // The texture coordinates the atlas holds at the texel, and their change over a screen pixel: a texel's change
        // times how many atlas texels the pixel spans, a pixel's size over a texel's at the instance's distance.
        ImpostorUv uv = LoadImpostorUv(impostor, hit.texel);
        float footprint = length(inputs.position) / frame.pixelsPerRadian;
        float atlasTexel = 2.0 * instance.boundsRadius / float(ImpostorTile);
        inputs.uv = uv.uv;
        inputs.uvDdx = uv.perTexelX * (footprint / atlasTexel);
        inputs.uvDdy = uv.perTexelY * (footprint / atlasTexel);
        inputs.viewDirection = NormalizeSafe(-inputs.position, float3(0.0, 0.0, -1.0));
    }
    else
    {
        PixelTriangle shown = LoadPixelTriangle(frame, visibleIndex, triangle, ToClip(center));
        instance = shown.instance;
        model = Interpolate(shown.v0.position, shown.v1.position, shown.v2.position, shown.bary.lambda);
        slot = shown.cluster.materialSlot;
        inputs = CreateSurfaceInputs(shown);
        lift = GetTriangleLift(shown, inputs.position);
    }

    uint row = GetMaterial(instance, slot);
    if (LoadMaterialShader(models.materials, row) != shader)
        return;

    M material = LoadMaterialParams<M>(models.materials, row);
    SurfaceOutputs surface = material.Evaluate(inputs);

    float3 previous = ToWorld(GetPreviousWorld(instance, frame.frameIndex), model) - frame.previousCameraPosition.xyz;
    float4 previousClip = mul(float4(previous, 1.0), frame.previousViewProjection);
    float2 previousUv = ToUv(previousClip.xy / max(previousClip.w, Epsilon));
    velocityTarget.Store(pixel, float4(previousUv - unjittered, previousClip.w, 0.0));

    StorageRef normalTarget = { frame.gbufferNormal, 0u };
    StorageRef baseTarget = { frame.gbufferBase, 0u };
    StorageRef surfaceTarget = { frame.gbufferSurface, 0u };
    normalTarget.Store(pixel, float4(EncodeOctahedral(surface.normal) * 0.5 + 0.5, 0.0, 0.0));
    baseTarget.Store(pixel, float4(surface.baseColor, surface.metalness));
    float encodedLift = EncodeLift(frame, inputs.position, lift);
    surfaceTarget.Store(pixel, float4(surface.roughness, 0.5, surface.occlusion, encodedLift));
}

// The pixel's nearest translucent surface into the glass targets, where one is in front of the opaque surface.
void ResolveTranslucent<M : IMaterial>(FrameConstants frame, uint2 pixel)
{
    UintTextureRef glass = { GetClusters().glassVisibility };
    uint visibleIndex;
    uint triangle;
    if (!DecodeVisibilityId(glass.Load(pixel, 0u), visibleIndex, triangle))
        return;

    Models models = GetModels();
    float2 center = GetTexelCenterUv(pixel, uint2(frame.width, frame.height));
    PixelTriangle shown = LoadPixelTriangle(frame, visibleIndex, triangle, ToClip(center));
    uint row = GetMaterial(shown.instance, shown.cluster.materialSlot);
    if (LoadMaterialShader(models.materials, row) != GetPassHeader().z)
        return;

    M material = LoadMaterialParams<M>(models.materials, row);
    SurfaceOutputs surface = material.Evaluate(CreateSurfaceInputs(shown));
    StorageRef baseTarget = { frame.glassBase, 0u };
    StorageRef normalTarget = { frame.glassNormal, 0u };
    StorageRef surfaceTarget = { frame.glassSurface, 0u };
    baseTarget.Store(pixel, float4(surface.baseColor, surface.alpha));
    normalTarget.Store(pixel, float4(EncodeOctahedral(surface.normal) * 0.5 + 0.5, 0.0, 0.0));
    surfaceTarget.Store(pixel, float4(surface.roughness, surface.emissive));
}
