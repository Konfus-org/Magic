using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Shaders;

namespace WorldRenderer;

/// <summary>
/// What every pass of a frame reads, the twin of <c>Core/Frame.slang</c>'s: the view, the sun, the output's size and
/// the heap ids of the frame's tables and textures, written once a frame into the constants buffer.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct FrameConstants
{
    public const int Bytes = 672;

    /// <summary>
    /// The cone cull is off: the view is orthographic, as a capture's is, so no one point is the camera.
    /// </summary>
    public const uint FlagNoConeCull = 1;

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

    public uint Flags;

    public uint Cards;

    public uint CardSampler;

    public Vector2 Jitter;

    public uint OutputWidth;

    public uint OutputHeight;

    public uint Acceleration;

    public uint LeafClusters;

    public uint CardAlbedo;

    public uint CardNormal;

    public uint CardDepth;

    public uint Velocity;

    public uint Padding2;

    public uint Padding3;
}
