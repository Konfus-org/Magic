// The cluster pool: every resident model's hierarchy in one buffer, C#'s GpuCluster, 80 bytes each. A cluster is up
// to MaxClusterTriangles triangles, a run of the cluster index pool into the vertex pool it names, with the spheres
// and errors the cut through the hierarchy is decided from and the cone its back faces are culled by.
module "Scene/Clusters";

import Core.Heap;
import Core.Pack;
import Core.Pass;
import Core.Frame;
import Light.Air;

public static const uint MaxClusterTriangles = 128u;

// Where the cluster constants are in the buffer every pass header's x names: after the frame, the models, the world as
// rays see it, the lights, the shadows, the indirect light and the air.
public static const uint ClusterConstantsOffset = AirOffset + AirBytes;

// What the cluster passes read beyond the frame, C#'s ClusterConstants, 84 bytes, written once a frame after the air.
// The cluster budget sets the handoff and the error threshold at the start of the frame.
public struct ClusterConstants
{
    public uint depthPyramidLevels;
    // Whether the depth pyramid holds last frame, so a cluster may be tested against it.
    public uint hasDepthPyramid;
    // Pixels of projected error a drawn cluster may have, as the cluster budget raises it.
    public float errorThreshold;
    // The pixel radius on screen under which an instance hands off to its impostor, as the cluster budget raises it.
    // 0 hands off by distance alone.
    public float impostorPixels;
    // The distance in metres to an instance's sphere past which it hands off to its impostor. 0 hands off by size
    // alone.
    public float impostorDistance;
    public uint maxVisibleClusters;
    // The heap ids of the cluster pool, its index pool and its vertex copies, of each model's row of clusters and
    // impostor, by the instances' model index, of what the cull leaves for the draw and the resolve, of the impostor
    // table, of each instance's count of visible rows, and of the buffer that holds the cluster budget's level.
    public uint clusters;
    public uint indices;
    public uint vertices;
    public uint models;
    public uint visibleClusters;
    public uint drawArguments;
    public uint counts;
    public uint retestList;
    public uint retestArguments;
    public uint depthPyramid;
    public uint visibility;
    // The nearest translucent surface in front of the opaque ones: its visibility id, 0 where there is none.
    public uint glassVisibility;
    public uint impostors;
    public uint instanceRows;
    public uint budget;
}

public ClusterConstants GetClusters() { return LoadFrameRow<ClusterConstants>(ClusterConstantsOffset); }

// A model's clusters in the cluster pool and its row of the impostor table, 0 for none: C#'s GpuClusterModel, the row
// of the model table an instance's model index names.
public struct GpuClusterModel
{
    public uint clusterOffset;
    public uint clusterCount;
    public uint impostor;
}

public GpuClusterModel LoadClusterModel(uint model)
{
    ArrayRef<GpuClusterModel> models = { GetClusters().models };
    return models[model];
}

public struct GpuCluster
{
    // The sphere around the cluster's triangles, in the model's frame.
    public float3 center;
    public float radius;
    // The sphere around the group whose simplification replaces this cluster and its siblings, which holds the
    // detail sphere.
    public float3 parentCenter;
    public float parentRadius;
    // The sphere around the group this cluster was simplified from, the cluster itself for a leaf: what error is
    // projected over.
    public float3 detailCenter;
    public float detailRadius;
    // How far, in metres, the cluster's surface may be from the original's: 0 for a leaf.
    public float error;
    // The error of the group that replaces this cluster and its siblings. The model's diameter for a root.
    public float parentError;
    public uint indexOffset;
    public uint triangleCount;
    // The normal cone's axis, octahedral, and the cosine of its half angle as a signed 16-bit fraction.
    public uint coneAxis;
    public uint coneCutoff;
    public uint materialSlot;
    // 1 when its indices point into the cluster vertex copies, a level above the leaves. 0 for a leaf, whose indices
    // point into the models' vertex pool.
    public uint isCopy;
}

// The heap id of the vertex pool the cluster's indices point into: the cluster copies' above the leaves, the
// models' for a leaf.
public uint GetVertexPool(GpuCluster cluster, ClusterConstants clusters, uint modelVertices)
{
    return cluster.isCopy != 0u ? clusters.vertices : modelVertices;
}

public GpuCluster LoadCluster(uint pool, uint index)
{
    ArrayRef<GpuCluster> clusters = { pool };
    return clusters[index];
}

public float3 GetConeAxis(GpuCluster cluster) { return DecodeUnitVector(cluster.coneAxis); }

// The cosine of the cone's half angle. 1 for a cluster facing every way, which no view is behind.
public float GetConeCutoff(GpuCluster cluster) { return UnpackSnorm16(cluster.coneCutoff); }
