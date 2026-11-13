// Every pixel of the visibility buffer to a surface: the id names the triangle, its three vertices give the pixel its
// barycentrics and their derivatives, the attributes interpolate from them, and the material decides the surface,
// written to the GBuffer, with how far the pixel's point rises off its flat triangle, and its emissive light to the
// picture for the shading to add to, and the nearest translucent surface of the pixel, from the glass's visibility
// buffer, to the glass targets. The entry is specialised to one material shader, numbered in the pass header's y, and
// resolves only the pixels whose rows that shader draws, loading its struct from each. Every pass writes the sky's
// velocity, the same each time. The pixel's velocity is written beside it: where the surface was on last frame's
// screen, from where its instance was and where the camera was. A pixel of sky moves with the camera alone. A row
// whose asset failed names its failure, which is drawn over the surface, and its pixels move with the failure's text.
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
import Scene.Failures;

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

// The pixel's opaque surface into the GBuffer, its emissive light into the picture and its velocity, or the sky's
// velocity where nothing was drawn.
void ResolveOpaque<M : IMaterial>(FrameConstants frame, uint2 pixel)
{
    Models models = GetModels();
    uint2 size = uint2(frame.width, frame.height);
    UintTextureRef visibility = { GetClusters().visibility };
    // The pixel's centre is where the jittered view saw its surface, which is unjittered where the camera saw it.
    float2 center = GetTexelCenterUv(pixel, size);
    float2 unjittered = center - frame.jitter.xy / float2(size);
    StorageRef velocityTarget = { frame.velocity, 0u };
    uint id = visibility.Load(pixel, 0u);
    if (id == 0u)
    {
        float3 sky = GetPreviousScreen(frame, ReconstructRelative(frame, center, FarDepth));
        velocityTarget.Store(pixel, float4(sky.xy - unjittered, 0.0, 0.0));
        return;
    }

    // The surface's inputs, the point in its model's frame, its instance and its material slot: from the triangle the
    // id names, or, for an impostor's pixel, from its atlas traced again along the pixel's ray, with the slot the
    // draw saw. An impostor has no triangle to rise off. Another pass's pixel goes before its surface is loaded.
    uint shader = GetPassHeader().y;
    SurfaceInputs inputs;
    float3 model;
    GpuInstance instance;
    uint slot;
    float lift = 0.0;
    if ((id & ImpostorFlag) != 0u)
    {
        instance = LoadInstance(models.instances, id & ImpostorInstanceMask);
        slot = (id >> ImpostorSlotShift) & 7u;
        if (LoadMaterialShader(models.materials, GetMaterial(instance, slot)) != shader)
            return;

        ImpostorRay ray = GetModelRay(frame, instance, ReconstructRelative(frame, center, 0.5));
        GpuImpostor impostor = LoadImpostor(LoadClusterModel(instance.model).impostor);
        ImpostorHit hit = TraceImpostor(instance, impostor, GetImpostorBlend(frame, instance), ray, 0.0);
        model = hit.position;
        inputs.position = ToCameraRelative(frame, ToWorld(instance.world, hit.position));
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
    }
    else
    {
        VisibleTriangle visible = LoadVisibleTriangle(id);
        instance = visible.instance;
        slot = visible.cluster.materialSlot;
        if (LoadMaterialShader(models.materials, GetMaterial(instance, slot)) != shader)
            return;

        PixelTriangle shown = LoadPixelTriangle(frame, visible, ToClip(center));
        model = Interpolate(shown.v0.position, shown.v1.position, shown.v2.position, shown.bary.lambda);
        inputs = CreateSurfaceInputs(shown);
        lift = GetTriangleLift(shown, inputs.position);
    }

    uint row = GetMaterial(instance, slot);
    M material = LoadMaterialParams<M>(models.materials, row);
    SurfaceOutputs surface = material.Evaluate(inputs);

    float3 previous = ToWorld(GetPreviousWorld(instance, frame.frameIndex), model) - frame.previousCameraPosition.xyz;
    float3 screen = GetPreviousScreen(frame, previous);
    float2 velocity = screen.xy - unjittered;
    uint failure = LoadFailureIndex(models.materials, row);
    if (failure != 0u)
        velocity = ApplyFailure(frame, pixel, instance, LoadFailure(models.failures, failure), surface);

    velocityTarget.Store(pixel, float4(velocity, screen.z, 0.0));

    StorageRef normalTarget = { frame.gbufferNormal, 0u };
    StorageRef baseTarget = { frame.gbufferBase, 0u };
    StorageRef surfaceTarget = { frame.gbufferSurface, 0u };
    normalTarget.Store(pixel, float4(EncodeUnitVectorUnorm(surface.normal), 0.0, 0.0));
    baseTarget.Store(pixel, float4(surface.baseColor, surface.metalness));
    float encodedLift = EncodeLift(frame, inputs.position, lift);
    surfaceTarget.Store(pixel, float4(surface.roughness, 0.5, surface.occlusion, encodedLift));
    StorageRef picture = { frame.hdr, 0u };
    picture.Store(pixel, float4(surface.emissive, 1.0));
}

// The pixel's nearest translucent surface into the glass targets, where one is in front of the opaque surface. None
// where the view has no glass, whose buffer's id is 0.
void ResolveTranslucent<M : IMaterial>(FrameConstants frame, uint2 pixel)
{
    uint glassId = GetClusters().glassVisibility;
    if (glassId == 0u)
        return;

    UintTextureRef glass = { glassId };
    uint id = glass.Load(pixel, 0u);
    if (id == 0u)
        return;

    Models models = GetModels();
    VisibleTriangle visible = LoadVisibleTriangle(id);
    uint row = GetMaterial(visible.instance, visible.cluster.materialSlot);
    if (LoadMaterialShader(models.materials, row) != GetPassHeader().y)
        return;

    float2 center = GetTexelCenterUv(pixel, uint2(frame.width, frame.height));
    PixelTriangle shown = LoadPixelTriangle(frame, visible, ToClip(center));
    M material = LoadMaterialParams<M>(models.materials, row);
    SurfaceOutputs surface = material.Evaluate(CreateSurfaceInputs(shown));
    uint failure = LoadFailureIndex(models.materials, row);
    if (failure != 0u)
        ApplyFailure(frame, pixel, visible.instance, LoadFailure(models.failures, failure), surface);

    StorageRef baseTarget = { frame.glassBase, 0u };
    StorageRef normalTarget = { frame.glassNormal, 0u };
    StorageRef surfaceTarget = { frame.glassSurface, 0u };
    baseTarget.Store(pixel, float4(surface.baseColor, surface.alpha));
    normalTarget.Store(pixel, float4(EncodeUnitVectorUnorm(surface.normal), 0.0, 0.0));
    surfaceTarget.Store(pixel, float4(surface.roughness, surface.emissive));
}
