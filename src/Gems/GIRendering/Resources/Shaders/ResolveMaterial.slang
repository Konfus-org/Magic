// Every pixel of the visibility buffer to a surface: the id names the triangle, its three vertices give the pixel its
// barycentrics and their derivatives, the attributes interpolate from them, and the material decides the surface,
// written to the GBuffer: the one the entry is specialised to, or the failure material for a row of that kind. The
// pixel's velocity is written beside it: where the surface was on last frame's screen, from where its instance was
// and where the camera was; a pixel of sky moves with the camera alone.
module "ResolveMaterial";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Instances;
import Scene.Materials;
import Scene.Visibility;
import Surface.Surface;
import Material.IMaterial;
import Material.Failure;

[shader("compute")]
[numthreads(8, 8, 1)]
void ResolveMaterial<M : IMaterial>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    UintTextureRef visibility = { frame.visibility };
    uint visibleIndex;
    uint triangle;
    float2 unjittered = GetTexelCenterUv(thread.xy, size) - frame.jitter.xy / float2(size);
    StorageRef velocityTarget = { frame.velocity, 0u };
    if (!DecodeVisibilityId(visibility.Load(thread.xy, 0u), visibleIndex, triangle))
    {
        float3 far = ReconstructRelative(frame, unjittered, 1e-6);
        float4 previousClip = mul(float4(far, 1.0), frame.previousViewProjection);
        float2 previousUv = ToUv(previousClip.xy / max(previousClip.w, Epsilon));
        velocityTarget.Store(thread.xy, float4(previousUv - unjittered, 0.0, 0.0));
        return;
    }

    PixelTriangle shown = LoadPixelTriangle(frame, visibleIndex, triangle, ToClip(GetTexelCenterUv(thread.xy, size)));
    float3 lambda = shown.bary.lambda;

    SurfaceInputs inputs;
    inputs.position = Interpolate(shown.p0, shown.p1, shown.p2, lambda);
    float3 normal = Interpolate(shown.v0.normal, shown.v1.normal, shown.v2.normal, lambda);
    float3 tangent = Interpolate(shown.v0.tangent, shown.v1.tangent, shown.v2.tangent, lambda);
    inputs.normal = ToWorldNormal(shown.instance.world, normal);
    inputs.tangent = ToWorldDirection(shown.instance.world, tangent);
    inputs.tangentSign = shown.v0.tangentSign;
    inputs.uv = Interpolate(shown.v0.uv, shown.v1.uv, shown.v2.uv, lambda);
    inputs.uvDdx = Interpolate(shown.v0.uv, shown.v1.uv, shown.v2.uv, shown.bary.ddx);
    inputs.uvDdy = Interpolate(shown.v0.uv, shown.v1.uv, shown.v2.uv, shown.bary.ddy);
    inputs.viewDirection = NormalizeSafe(-inputs.position, float3(0.0, 0.0, -1.0));
    inputs.material = LoadMaterial(frame.materials, GetMaterial(shown.instance, shown.cluster.materialSlot));
    inputs.sampler = inputs.material.baseColorSampler != 0u ? inputs.material.baseColorSampler : frame.defaultSampler;
    SurfaceOutputs surface = inputs.material.kind == MaterialKindFailure ? Failure.Evaluate(inputs) : M.Evaluate(inputs);

    float3 model = Interpolate(shown.v0.position, shown.v1.position, shown.v2.position, lambda);
    float3 previous = ToWorld(GetPreviousWorld(shown.instance, frame.frameIndex), model) - frame.previousCameraPosition.xyz;
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
