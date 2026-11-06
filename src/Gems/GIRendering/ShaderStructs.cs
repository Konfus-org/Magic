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
    public const int Bytes = 640;

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

    public float Padding0;

    public uint InstanceCount;

    public uint MaxVisibleClusters;

    public uint FrameIndex;

    public uint DefaultSampler;

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

    public uint Padding1;

    public uint Padding2;
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

    public uint Padding1;

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
