using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Ecs.Components;

namespace Magic.GIRendering;

/// <summary>
/// What every pass of a frame reads, the twin of <c>Core/Frame.slang</c>'s: the view, the sun, the output's size and
/// the heap ids of the frame's tables and textures, written once a frame into the constants buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct FrameConstants
{
    public const int Bytes = 656;

    public Matrix4x4 View;

    public Matrix4x4 Projection;

    public Matrix4x4 ViewProjection;

    public Matrix4x4 InverseViewProjection;

    public Vector4 FrustumPlane0;

    public Vector4 FrustumPlane1;

    public Vector4 FrustumPlane2;

    public Vector4 FrustumPlane3;

    public Vector4 FrustumPlane4;

    public Vector4 FrustumPlane5;

    public Vector4 SunDirection;

    public Vector4 SunRadiance;

    public Vector4 SkyRadiance;

    public Vector4 CameraPosition;

    public Matrix4x4 PreviousViewProjection;

    public Vector4 PreviousCameraPosition;

    public uint Width;

    public uint Height;

    public uint HzbLevels;

    public uint HasHzb;

    public float Near;

    public float PixelsPerRadian;

    public float ErrorThreshold;

    /// <summary>
    /// The pixel radius on screen under which an instance hands off to its impostor; 0 hands off by distance alone.
    /// </summary>
    public float ImpostorPixels;

    public uint InstanceCount;

    public uint MaxVisibleClusters;

    public uint FrameIndex;

    public uint Instances;

    public uint Clusters;

    public uint Vertices;

    public uint Indices;

    public uint Materials;

    public uint VisibleClusters;

    public uint DrawArguments;

    public uint Counts;

    public uint RetestList;

    public uint RetestArguments;

    public uint Hzb;

    public uint Visibility;

    public uint Depth;

    public uint GBufferNormal;

    public uint GBufferBase;

    public uint GBufferSurface;

    public uint Output;

    public Vector2 Jitter;

    public uint Acceleration;

    public uint LeafClusters;

    public uint Velocity;

    /// <summary>
    /// The distance in metres to an instance's sphere past which it hands off to its impostor; 0 hands off by size
    /// alone.
    /// </summary>
    public float ImpostorDistance;

    public uint Hdr;

    public uint ExposureMeter;

    public float TimeSec;

    public uint HitMaterials;

    public uint Padding3;
}

/// <summary>
/// An instance as the GPU reads it from the instance table, 256 bytes: the twin of <c>Scene/Instances.slang</c>'s,
/// in the world; the GPU takes the camera's position off a position once transformed.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuInstance
{
    public const int Bytes = 256;

    /// <summary>
    /// The model's frame to the world, row-major, as <see cref="Matrix4x4"/> is.
    /// </summary>
    public Matrix4x4 World;

    /// <summary>
    /// The world to the model's frame.
    /// </summary>
    public Matrix4x4 InverseWorld;

    /// <summary>
    /// <see cref="World"/> before it last changed, for motion in the frame <see cref="MovedFrame"/>.
    /// </summary>
    public Matrix4x4 PreviousWorld;

    /// <summary>
    /// The sphere around the instance in the world.
    /// </summary>
    public Vector3 BoundsCenter;

    public float BoundsRadius;

    /// <summary>
    /// The instance's clusters in the cluster pool.
    /// </summary>
    public uint ClusterOffset;

    public uint ClusterCount;

    /// <summary>
    /// The frame <see cref="World"/> last changed in: in any other, the instance stood still.
    /// </summary>
    public uint MovedFrame;

    /// <summary>
    /// The model's row of the impostor table, 0 for none.
    /// </summary>
    public uint Impostor;

    /// <summary>
    /// The material table's row for each of the instance's slots.
    /// </summary>
    public GpuMaterialRows Materials;
}

/// <summary>
/// A material table row for each of a renderer's <see cref="MaterialSlots"/>, inline so the instance stays one block of
/// bytes.
/// </summary>
[InlineArray(MaterialSlots.Capacity)]
internal struct GpuMaterialRows
{
    private uint _first;
}

/// <summary>
/// A material slot as the GPU reads it from the material table, 64 bytes, the twin of <c>Scene/Materials.slang</c>'s:
/// which of <see cref="GpuResources.MaterialShaders"/> draws it, then that shader's struct, its params as the shader's
/// reflection lays them out.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuMaterial
{
    public const int Bytes = 64;

    public uint Shader;

    public GpuMaterialParams Params;
}

/// <summary>
/// A <see cref="GpuMaterial"/>'s params, inline: the bytes the material shader's struct reads.
/// </summary>
[InlineArray((GpuMaterial.Bytes / 4) - 1)]
internal struct GpuMaterialParams
{
    private uint _first;
}

/// <summary>
/// A light as the GPU reads it from the light table, 64 bytes: the twin of <c>Scene/Lights.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuLight
{
    public const int Bytes = 64;

    public const uint Point = 0;

    public const uint Spot = 1;

    public const uint Rect = 2;

    public Vector3 Position;

    public float Range;

    public Vector3 Direction;

    public float Radius;

    public Vector3 Radiance;

    public uint Type;

    public float CosOuter;

    public float CosInner;

    public float Width;

    public float Height;
}

/// <summary>
/// What the lighting passes read beyond the frame, the twin of <c>Scene/Lighting.slang</c>'s: the sun above the air, the
/// viewer's height, the sky's tables, the HDR picture they light, the light table and its tiles, and the shadow masks.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct LightingConstants
{
    public const int Bytes = 80;

    public Vector4 SunIlluminance;

    public Vector4 CameraAltitude;

    public uint TransmittanceLut;

    public uint MultiScatteringLut;

    public uint SkyViewLut;

    public uint LutSampler;

    public uint HasSky;

    public uint Hdr;

    public uint Lights;

    public uint LightCount;

    public uint LightTiles;

    /// <summary>
    /// The denoised mask shading reads; 0 on a GPU that traces no rays: nothing is shadowed.
    /// </summary>
    public uint ShadowMask;

    public uint TracedShadows;

    public uint SunTiles;
}

/// <summary>
/// The heap ids of the shadow denoiser's targets, the twin of <c>DenoiseShadows.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ShadowDenoiseConstants
{
    public const int Bytes = 32;

    public uint TileStates;

    public uint SunBlend;

    public uint SunHistory;

    public uint SunDenoised;

    public uint Moments;

    public uint PreviousMoments;

    public uint PreviousMask;

    /// <summary>
    /// 0 on the first frame at a size, when last frame's targets hold nothing to blend with.
    /// </summary>
    public uint HasHistory;
}

/// <summary>
/// The probe grid's place and the heap ids of its buffers, the twin of <c>Scene/Probes.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ProbeGridConstants
{
    public const int Bytes = 80;

    /// <summary>
    /// Probes along x; with the two after, the twin of <c>dimensions</c>.
    /// </summary>
    public uint DimensionX;

    public uint DimensionY;

    public uint DimensionZ;

    /// <summary>
    /// How far a probe's depth reaches: one and a half cells.
    /// </summary>
    public float MaxDistance;

    /// <summary>
    /// Where the first probe rests in the world.
    /// </summary>
    public Vector3 Origin;

    public uint Probes;

    public Vector3 CellSize;

    public uint Depth;

    public uint DepthSampler;

    public uint Rays;

    public uint RayList;

    public uint RayArguments;

    public uint RayCounts;

    public uint Variance;

    /// <summary>
    /// 0 while the probes hold nothing: this update starts them afresh.
    /// </summary>
    public uint HasHistory;

    public uint Padding0;
}

/// <summary>
/// A vertex as the GPU reads it from the vertex pool, 32 bytes: the twin of <c>Scene/Vertices.slang</c>'s. Normals and
/// tangents are octahedral, packed as <c>Core/Pack.slang</c> packs them.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuVertex
{
    public const int Bytes = 32;

    public Vector3 Position;

    /// <summary>
    /// The unit normal, octahedral at 16 bits an axis.
    /// </summary>
    public uint Normal;

    /// <summary>
    /// The unit tangent, octahedral at 16 bits an axis.
    /// </summary>
    public uint Tangent;

    /// <summary>
    /// 1 or -1: what turns the cross of the normal and the tangent into the bitangent.
    /// </summary>
    public float TangentSign;

    public Vector2 Uv;
}

/// <summary>
/// One cluster of a model's hierarchy as the GPU reads it from the cluster pool, 80 bytes: the twin of
/// <c>Scene/Clusters.slang</c>'s. Its triangles are a run of the index pool; its vertices are wherever those indices
/// point in the vertex pool. The cut through the hierarchy is decided per cluster, with no walk: a cluster is drawn
/// when its parent's error projects above the threshold and its own at or below it. A leaf's error is 0 and a
/// root's parent error is the model's diameter, so some level draws until the whole model is under the threshold.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuCluster
{
    public const int Bytes = 80;

    /// <summary>
    /// The most triangles a cluster holds, and the most vertices.
    /// </summary>
    public const int MaxTriangles = 128;

    /// <summary>
    /// The sphere around the cluster's triangles, in the model's frame.
    /// </summary>
    public Vector3 Center;

    public float Radius;

    /// <summary>
    /// The sphere around the group whose simplification replaces the cluster and its siblings, which holds
    /// <see cref="DetailCenter"/>'s sphere, so the parent's error projects no smaller than the cluster's.
    /// </summary>
    public Vector3 ParentCenter;

    public float ParentRadius;

    /// <summary>
    /// The sphere around the group the cluster was simplified from, the cluster's own for a leaf: what
    /// <see cref="Error"/> is projected over, the same sphere the group's children project it over as their parent's.
    /// </summary>
    public Vector3 DetailCenter;

    public float DetailRadius;

    /// <summary>
    /// How far, in metres, the cluster's surface may be from the original's: 0 for a leaf.
    /// </summary>
    public float Error;

    /// <summary>
    /// The error of the group that replaces the cluster and its siblings; the model's diameter for a root.
    /// </summary>
    public float ParentError;

    /// <summary>
    /// The first of the cluster's indices in the index pool.
    /// </summary>
    public uint IndexOffset;

    public uint TriangleCount;

    /// <summary>
    /// The normal cone's axis, octahedral at 16 bits an axis, in the model's frame.
    /// </summary>
    public uint ConeAxis;

    /// <summary>
    /// The cosine of the cone's half angle, as a signed 16-bit fraction in the low half; a cluster facing every way
    /// has a cutoff of 1, which the test never rejects.
    /// </summary>
    public uint ConeCutoff;

    /// <summary>
    /// Which of the instance's material slots the cluster is drawn with.
    /// </summary>
    public uint MaterialSlot;

    public uint Padding;
}

/// <summary>
/// One model's impostor, 32 bytes: the twin of <c>Scene/Impostors.slang</c>'s: the box around the model in its
/// frame, and the heap id of its atlas.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuImpostor
{
    public const int Bytes = 32;

    public Vector3 BoundsMin;

    public uint Atlas;

    public Vector3 BoundsMax;

    /// <summary>
    /// The heap id of the atlas of the texture coordinates its surface texels show.
    /// </summary>
    public uint UvAtlas;
}
