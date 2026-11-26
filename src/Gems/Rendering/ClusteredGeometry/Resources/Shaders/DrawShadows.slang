// The shadow list drawn into a shadow atlas's depth: one indirect draw a cluster or an impostor, its row of the list in
// the draw's first instance. Each vertex goes through the view-projection of its row's shadow view, into that view's
// rectangle of the atlas, and four clip distances keep its triangles inside the rectangle. A cluster in the impostor
// handoff keeps the clusters' share of the camera's dither. An impostor faces the light and writes the depth of what
// its atlas sees.
module "DrawShadows";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Instances;
import Scene.Clusters;
import Scene.Vertices;
import Scene.Impostors;
import Scene.Models;
import Scene.ShadowViews;
import Scene.ShadowDraws;

// How far back towards the light, in the instance's radii, the point is that an impostor is seen from: far enough that
// its rays are as good as parallel. Never under a kilometre.
static const float ImpostorEyeRadii = 1000.0;
static const float ImpostorEyeMin = 1000.0;

struct Varyings
{
    float4 position : SV_Position;
    float clipDistances[4] : SV_ClipDistance;
    // How far into the impostor handoff the instance is, as the camera sees it.
    nointerpolation float fade : FADE;
};

struct ImpostorVaryings
{
    float4 position : SV_Position;
    float clipDistances[4] : SV_ClipDistance;
    // The point relative to the eye the impostor is seen from.
    float3 relative : RELATIVE;
    nointerpolation uint row : ROW;
    nointerpolation float fade : FADE;
    nointerpolation uint3 viewCells : VIEW_CELLS;
    nointerpolation float3 viewWeights : VIEW_WEIGHTS;
};

struct ImpostorTargets
{
    // Never nearer the light than the quad, which is at the box's nearest corner.
    float depth : SV_DepthLessEqual;
};

[shader("vertex")]
Varyings DrawShadowVertex(uint vertexIndex : SV_VertexID, uint row : SV_StartInstanceLocation)
{
    Models models = GetModels();
    ClusterConstants clusters = GetClusters();
    FrameConstants frame = GetFrame();
    ShadowCluster entry = LoadShadowCluster(row);
    GpuShadowView view = LoadShadowView(GetShadowCull().views, entry.view);
    GpuInstance instance = LoadInstance(models.instances, entry.instance);
    GpuCluster cluster = LoadCluster(clusters.clusters, entry.cluster);

    uint vertices = GetVertexPool(cluster, clusters, models.vertices);
    Vertex vertex = LoadVertex(vertices, LoadIndex(clusters.indices, cluster.indexOffset + vertexIndex));
    float4 clip = ToShadowClip(view, ToCameraRelative(frame, ToWorld(instance.world, vertex.position)));
    Varyings output;
    output.position = ToAtlasClip(view, clip);
    float4 sides = GetShadowClipDistances(clip);
    output.clipDistances[0] = sides.x;
    output.clipDistances[1] = sides.y;
    output.clipDistances[2] = sides.z;
    output.clipDistances[3] = sides.w;
    output.fade = GetImpostorFade(frame, instance);
    return output;
}

[shader("pixel")]
void DrawShadowPixel(Varyings input) {}

// A cluster of an instance in the impostor handoff: the dither keeps the clusters' share of the texels, and the
// impostor draws the rest.
[shader("pixel")]
void DrawShadowFadingPixel(Varyings input)
{
    if (GetDither(input.position.xy) < input.fade)
        discard;
}

[shader("vertex")]
ImpostorVaryings DrawShadowImpostorVertex(uint vertexIndex : SV_VertexID, uint row : SV_StartInstanceLocation)
{
    FrameConstants frame = GetFrame();
    ShadowCluster entry = LoadShadowCluster(row);
    GpuShadowView view = LoadShadowView(GetShadowCull().views, entry.view);
    GpuInstance instance = LoadInstance(GetModels().instances, entry.instance);
    uint impostorRow = LoadClusterModel(instance.model).impostor;
    GpuImpostor impostor = LoadImpostor(impostorRow);
    static const float2 corners[6] = { float2(0, 0), float2(1, 0), float2(1, 1), float2(0, 0), float2(1, 1), float2(0, 1) };

    FrameConstants eye = GetImpostorEye(frame, view, instance);
    ImpostorBlend blend = GetImpostorBlend(eye, instance);
    uint3 viewBounds = LoadImpostorViewBounds(impostorRow, blend);
    ImpostorVaryings output;
    output.relative = GetImpostorCorner(eye, instance, impostor, blend, viewBounds, corners[vertexIndex]);
    float4 clip = ToShadowClip(view, ToCameraRelative(frame, eye.cameraPosition.xyz + output.relative));
    output.position = ToAtlasClip(view, clip);
    float4 sides = GetShadowClipDistances(clip);
    output.clipDistances[0] = sides.x;
    output.clipDistances[1] = sides.y;
    output.clipDistances[2] = sides.z;
    output.clipDistances[3] = sides.w;
    output.row = row;
    output.fade = GetImpostorFade(frame, instance);
    output.viewCells = blend.cells;
    output.viewWeights = blend.weights;
    return output;
}

[shader("pixel")]
ImpostorTargets DrawShadowImpostorPixel(ImpostorVaryings input)
{
    if (GetDither(input.position.xy) >= input.fade)
        discard;

    FrameConstants frame = GetFrame();
    ShadowCluster entry = LoadShadowCluster(input.row);
    GpuShadowView view = LoadShadowView(GetShadowCull().views, entry.view);
    GpuInstance instance = LoadInstance(GetModels().instances, entry.instance);
    GpuImpostor impostor = LoadImpostor(LoadClusterModel(instance.model).impostor);
    FrameConstants eye = GetImpostorEye(frame, view, instance);
    ImpostorBlend blend = { input.viewCells, input.viewWeights };
    ImpostorHit hit = TraceImpostor(instance, impostor, blend, GetModelRay(eye, instance, input.relative), 0.5);
    if (!hit.isHit || GetMaterial(instance, hit.slot) == EmptySlot)
        discard;

    float4 clip = ToShadowClip(view, ToCameraRelative(frame, ToWorld(instance.world, hit.position)));
    ImpostorTargets output;
    output.depth = min(clip.z / clip.w, input.position.z);
    return output;
}

// The camera's frame moved to the eye an impostor is seen from in a shadow view: far back from the instance towards
// the light, so it faces the light and its rays are as good as the view's parallel ones.
FrameConstants GetImpostorEye(FrameConstants frame, GpuShadowView view, GpuInstance instance)
{
    FrameConstants eye = frame;
    float distance = max(instance.boundsRadius * ImpostorEyeRadii, ImpostorEyeMin);
    eye.cameraPosition = float4(instance.boundsCenter + view.towards * distance, 0.0);
    return eye;
}
