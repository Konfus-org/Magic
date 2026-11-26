// Which clusters cast into each shadow view: of each instance that casts a shadow, is in the view and is of the casters
// the view draws, the clusters in the view at the level whose error is under a texel of the view where they are, and
// the impostor of an instance in the impostor handoff. The handoff is the camera's, so a caster's shadow has the shape
// that the camera sees. A perspective view, a local light's, draws an instance in the handoff as its clusters: its
// impostor is drawn for a far, parallel light. One group an instance goes over every view. Each cluster that passes
// gets a row of the shadow list and a draw in the region of its pipeline.
module "CullShadowClusters";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Instances;
import Scene.Clusters;
import Scene.Materials;
import Scene.Impostors;
import Scene.Models;
import Scene.ShadowViews;
import Scene.ShadowDraws;

// The instance cull's groups go along x up to this many and then down y, as a GPU takes no more along one axis.
static const uint CullGroupsPerRow = 65535u;

// The texels of projected error that a drawn cluster may have.
static const float ShadowErrorTexels = 1.0;

// One draw's arguments, as the GPU reads them: a cluster's triangles three vertices each, or an impostor's quad, its
// row of the shadow list the first instance.
struct DrawArguments
{
    uint vertexCount;
    uint instanceCount;
    uint firstVertex;
    uint firstInstance;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CullShadowClusters(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    Models models = GetModels();
    uint instanceIndex = (group.y * CullGroupsPerRow) + group.x;
    if (instanceIndex >= models.count)
        return;

    GpuInstance instance = LoadInstance(models.instances, instanceIndex);
    if (instance.model == 0u || IsShadowless(instance))
        return;

    FrameConstants frame = GetFrame();
    ShadowCullConstants constants = GetShadowCull();
    float3 center = ToCameraRelative(frame, instance.boundsCenter);
    float fade = GetImpostorFade(frame, instance);
    GpuClusterModel model = LoadClusterModel(instance.model);
    bool isDynamic = IsDynamic(instance);
    for (uint v = 0u; v < constants.viewCount; v++)
    {
        GpuShadowView view = LoadShadowView(constants.views, v);
        ShadowCasters casters = (ShadowCasters)view.casters;
        if ((casters == ShadowCasters.Still && isDynamic) || (casters == ShadowCasters.Moving && !isDynamic))
            continue;

        // An instance smaller than a texel of the view where it is casts nothing that it can hold.
        float texel = GetTexelSizeAt(view, GetViewDepth(view, center, instance.boundsRadius));
        if (!IsSphereInShadowView(view, center, instance.boundsRadius) || instance.boundsRadius * 2.0 < texel)
            continue;

        float viewFade = view.isOrthographic != 0u ? fade : 0.0;
        if (local.x == 0u && viewFade > 0.0)
            AddShadowDraw(ShadowDrawRegion.Impostors, instanceIndex, ShadowImpostor, v, 6u);

        if (viewFade >= 1.0)
            continue;

        bool isFading = viewFade > 0.0;
        for (uint i = local.x; i < model.clusterCount; i += 64u)
        {
            uint clusterIndex = model.clusterOffset + i;
            GpuCluster cluster = LoadCluster(GetClusters().clusters, clusterIndex);
            uint material = GetMaterial(instance, cluster.materialSlot);
            if (!IsCast(view, frame, instance, cluster, material))
                continue;

            bool isDoubleSided = IsDoubleSided(models.materials, material);
            ShadowDrawRegion region = isFading
                ? (isDoubleSided ? ShadowDrawRegion.FadingDoubleSided : ShadowDrawRegion.Fading)
                : (isDoubleSided ? ShadowDrawRegion.DoubleSided : ShadowDrawRegion.SingleSided);
            AddShadowDraw(region, instanceIndex, clusterIndex, v, cluster.triangleCount * 3u);
        }
    }
}

// Whether a cluster casts into the view: its slot holds an opaque material, it is in the view, and it is the level of
// the hierarchy whose error, in texels of the view, is under the threshold while its parent group's is over it.
bool IsCast(GpuShadowView view, FrameConstants frame, GpuInstance instance, GpuCluster cluster, uint material)
{
    if (material == EmptySlot || IsTranslucent(GetModels().materials, material))
        return false;

    float scale = GetMaxScale(instance.world);
    float3 center = ToCameraRelative(frame, ToWorld(instance.world, cluster.center));
    if (!IsSphereInShadowView(view, center, cluster.radius * scale))
        return false;

    float texel = GetTexelSizeAt(view, GetViewDepth(view, center, cluster.radius * scale));
    float parentError = cluster.parentError * scale / texel;
    float error = cluster.error * scale / texel;
    return parentError > ShadowErrorTexels && error <= ShadowErrorTexels;
}

// The view depth of the nearest point of a sphere in a perspective view, no nearer than its near plane; 1 in an
// orthographic view, whose texels are the same size at every depth.
float GetViewDepth(GpuShadowView view, float3 center, float radius)
{
    if (view.isOrthographic != 0u)
        return 1.0;

    return max(mul(float4(center, 1.0), view.viewProjection).w - radius, view.depthRange);
}

// Puts a cluster, or ShadowImpostor for the instance's impostor, on the shadow list for view, and its draw of
// vertexCount vertices in region. Past the end of the list or of the region, it draws nothing.
void AddShadowDraw(ShadowDrawRegion region, uint instance, uint cluster, uint view, uint vertexCount)
{
    ShadowCullConstants constants = GetShadowCull();
    BufferRef counts = { constants.counts };
    uint row = counts.InterlockedAdd(CountShadowClusters, 1u);
    if (row >= constants.maxShadowClusters)
        return;

    uint drawIndex = counts.InterlockedAdd(uint(region) * 4u, 1u);
    if (drawIndex >= constants.maxShadowDraws)
        return;

    ArrayRef<ShadowCluster> list = { constants.shadowClusters };
    ShadowCluster entry = { instance, cluster, view };
    list[row] = entry;
    ArrayRef<DrawArguments> draws = { constants.drawArguments };
    DrawArguments arguments = { vertexCount, 1u, 0u, row };
    draws[uint(region) * constants.maxShadowDraws + drawIndex] = arguments;
}
