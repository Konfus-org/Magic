// The cluster pool: every resident model's hierarchy in one buffer, C#'s GpuCluster, 80 bytes each. A cluster is up
// to MaxClusterTriangles triangles, a run of the index pool, with the spheres and errors the cut through the
// hierarchy is decided from and the cone its back faces are culled by.
module "Scene/Clusters";

import Core.Heap;
import Core.Pack;

public static const uint MaxClusterTriangles = 128u;

// The material slot of the imposter cluster, the one above a model's roots: a quad with no vertices of its own.
public static const uint ImposterSlot = 0xFFFFFFFFu;

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
    // The error of the group that replaces this cluster and its siblings; infinite for a root.
    public float parentError;
    public uint indexOffset;
    public uint triangleCount;
    // The normal cone's axis, octahedral, and the cosine of its half angle as a signed 16-bit fraction.
    public uint coneAxis;
    public uint coneCutoff;
    public uint materialSlot;
    public uint padding;
}

public GpuCluster LoadCluster(uint pool, uint index)
{
    ArrayRef<GpuCluster> clusters = { pool };
    return clusters[index];
}

public bool IsImposter(GpuCluster cluster) { return cluster.materialSlot == ImposterSlot; }

public float3 GetConeAxis(GpuCluster cluster) { return DecodeUnitVector(cluster.coneAxis); }

// The cosine of the cone's half angle; 1 for a cluster facing every way, which no view is behind.
public float GetConeCutoff(GpuCluster cluster) { return UnpackSnorm16(cluster.coneCutoff); }
