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
import Scene.Clusters;
import Scene.Vertices;
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

    ArrayRef<VisibleCluster> visible = { frame.visibleClusters };
    VisibleCluster entry = visible[visibleIndex];
    GpuInstance instance = LoadInstance(frame.instances, entry.instance);
    GpuCluster cluster = LoadCluster(frame.clusters, entry.cluster);
    float2 ndc = ToClip(GetTexelCenterUv(thread.xy, size));

    uint first = cluster.indexOffset + triangle * 3u;
    Vertex v0 = LoadVertex(frame.vertices, LoadIndex(frame.indices, first));
    Vertex v1 = LoadVertex(frame.vertices, LoadIndex(frame.indices, first + 1u));
    Vertex v2 = LoadVertex(frame.vertices, LoadIndex(frame.indices, first + 2u));
    float3 p0 = Relative(frame, ToWorld(instance.world, v0.position));
    float3 p1 = Relative(frame, ToWorld(instance.world, v1.position));
    float3 p2 = Relative(frame, ToWorld(instance.world, v2.position));
    Barycentrics bary = ComputeBarycentrics(
        mul(float4(p0, 1.0), frame.viewProjection),
        mul(float4(p1, 1.0), frame.viewProjection),
        mul(float4(p2, 1.0), frame.viewProjection),
        ndc,
        float2(size)
    );

    SurfaceInputs inputs;
    inputs.position = Interpolate(p0, p1, p2, bary.lambda);
    inputs.normal = ToWorldNormal(instance.world, Interpolate(v0.normal, v1.normal, v2.normal, bary.lambda));
    inputs.tangent = ToWorldDirection(instance.world, Interpolate(v0.tangent, v1.tangent, v2.tangent, bary.lambda));
    inputs.tangentSign = v0.tangentSign;
    inputs.uv = Interpolate(v0.uv, v1.uv, v2.uv, bary.lambda);
    inputs.uvDdx = Interpolate(v0.uv, v1.uv, v2.uv, bary.ddx);
    inputs.uvDdy = Interpolate(v0.uv, v1.uv, v2.uv, bary.ddy);
    inputs.viewDirection = NormalizeSafe(-inputs.position, float3(0.0, 0.0, -1.0));
    inputs.material = LoadMaterial(frame.materials, GetMaterial(instance, cluster.materialSlot));
    inputs.sampler = inputs.material.baseColorSampler != 0u ? inputs.material.baseColorSampler : frame.defaultSampler;
    SurfaceOutputs surface = inputs.material.kind == MaterialKindFailure ? Failure.Evaluate(inputs) : M.Evaluate(inputs);

    float3 model = Interpolate(v0.position, v1.position, v2.position, bary.lambda);
    float3 previous = ToWorld(instance.previousWorld, model) - frame.previousCameraPosition.xyz;
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
