using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Magic.ClusterRendering;

/// <summary>
/// The one constant block every stage of every pass is pushed, <c>Frame</c> in Shaders/Include/Frame.hlsli, which
/// mirrors it member for member: a view's constants, then the pass's own rows. Matrices go up untransposed, as the
/// shaders apply them with <c>mul(M, v)</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    /// <summary>
    /// Camera-relative: positions minus the camera's, through the view and a reverse-Z projection with no far plane,
    /// moved by <see cref="Jitter"/>.
    /// </summary>
    public Matrix4x4 ViewProj;

    /// <summary>
    /// The view's rotation alone, world to view.
    /// </summary>
    public Matrix4x4 View;

    /// <summary>
    /// The camera's position as a float; <see cref="CameraLow"/> is what it is past that.
    /// </summary>
    public Vector3 CameraPosition;
    public float Time;

    /// <summary>
    /// The view's size in pixels as it is drawn, and one over it.
    /// </summary>
    public Vector2 ViewSize;
    public Vector2 ViewTexel;

    /// <summary>
    /// The projection's x and y scales, which turn a view position into the screen and back.
    /// </summary>
    public Vector2 ProjScale;
    public float Near;
    public float Far;

    /// <summary>
    /// The way the sun's light travels, and its disc's half-angle in radians.
    /// </summary>
    public Vector3 SunDirection;
    public float SunAngularRadius;

    /// <summary>
    /// The sun's illuminance in lux, times its colour; zero with no sun.
    /// </summary>
    public Vector3 SunIlluminance;
    public uint LightCount;

    public uint FrameIndex;

    /// <summary>
    /// Light tiles across the view, and the 32-bit words of lights each tile's mask holds.
    /// </summary>
    public uint TileCountX;
    public uint TileWords;

    /// <summary>
    /// How the view's image is encoded as it is shown, a <see cref="Magic.Contexts.Gpu.TargetEncoding"/>: what the
    /// target's image expects, decided by its format.
    /// </summary>
    public uint TargetEncoding;

    /// <summary>
    /// A view depth's z-bin is <c>log2(depth) * ZBinScale + ZBinBias</c>, of <see cref="ZBinCount"/>.
    /// </summary>
    public float ZBinScale;
    public float ZBinBias;
    public uint ZBinCount;

    /// <summary>
    /// The sky's material's row, which a sky draw of a surface loads its params from.
    /// </summary>
    public uint SkyMaterial;

    public float DeltaSeconds;
    public Vector3 Padding;

    /// <summary>
    /// The last frame's <see cref="ViewProj"/> without its jitter.
    /// </summary>
    public Matrix4x4 PrevViewProj;

    /// <summary>
    /// What the camera's position is past <see cref="CameraPosition"/>.
    /// </summary>
    public Vector3 CameraLow;

    /// <summary>
    /// Pixels a cluster's error may cover before a finer one is drawn.
    /// </summary>
    public float ClusterError;

    /// <summary>
    /// The camera's position less the last frame's, in metres.
    /// </summary>
    public Vector3 CameraMotion;

    /// <summary>
    /// Pixels a metre covers a metre in front of the camera, along the view's height.
    /// </summary>
    public float PixelScale;

    /// <summary>
    /// The player's camera relative to this view's, which detail is kept nearest to when there is too much.
    /// </summary>
    public Vector3 PriorityPosition;

    /// <summary>
    /// What uv gradients are scaled by: below 1 while a temporal upscale resolves the finer texels it reads.
    /// </summary>
    public float TextureMipScale;

    /// <summary>
    /// This frame's sub-pixel offset of <see cref="ViewProj"/>, in clip space.
    /// </summary>
    public Vector2 Jitter;

    /// <summary>
    /// The view's size in pixels as it is shown.
    /// </summary>
    public Vector2 OutputSize;

    /// <summary>
    /// The pass's params, packed by its <see cref="ParamLayout"/>, in the first seven rows; its iteration in the last.
    /// </summary>
    public PassWords Pass;
}

/// <summary>
/// The pass's eight rows of a <see cref="FrameConstants"/>.
/// </summary>
[InlineArray(32)]
internal struct PassWords
{
    private uint _word;
}

/// <summary>
/// One instance in the scene's table, at its entity's slot (Shaders/Include/Structs.hlsli's GpuSceneInstance): the rows
/// of its transpose(world), xyz its turn and scale and w its position's high float, which <see cref="Low"/> adds to, so
/// a position far from the origin is still exact relative to the camera; the last frame's the same way, which velocity
/// is worked out from; its largest scale, flags, model row, when its fade started, and its eight material slots' rows,
/// two to a word, <see cref="NoMaterial"/> for one not in yet.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuSceneInstance
{
    public const uint NoMaterial = 0xFFFF;

    public Vector4 Row0;
    public Vector4 Row1;
    public Vector4 Row2;
    public Vector3 Low;
    public float MaxScale;
    public Vector4 PrevRow0;
    public Vector4 PrevRow1;
    public Vector4 PrevRow2;
    public Vector3 PrevLow;

    /// <summary>
    /// <see cref="GpuSceneFlags"/>.
    /// </summary>
    public uint Flags;

    public uint Model;

    /// <summary>
    /// When its fade started, in the frame's <c>Time</c>, while <see cref="Flags"/> says it fades.
    /// </summary>
    public float FadeStart;

    public Vector2 Padding;
    public MaterialRows Materials;
}

/// <summary>
/// A scene instance's eight material slots' rows, two to a word.
/// </summary>
[InlineArray(4)]
internal struct MaterialRows
{
    private uint _word;
}

/// <summary>
/// What a <see cref="GpuSceneInstance"/> carries besides its transform.
/// </summary>
[Flags]
internal enum GpuSceneFlags : uint
{
    None = 0,

    /// <summary>
    /// Its scale turns it inside out, so its triangles wind the other way.
    /// </summary>
    Mirrored = 1,

    /// <summary>
    /// It dissolves into view from <see cref="GpuSceneInstance.FadeStart"/>.
    /// </summary>
    FadeIn = 2,
}

/// <summary>
/// A model in the <see cref="ClusterPool"/>: its sphere about its origin, its clusters, levels and cards, the unit-scale
/// distance its cards are drawn from, 0 for none, its grid's step, its distance field's slot in <see cref="MeshFields"/>,
/// 0 for none, and how far from its sphere's middle its clusters' error spheres reach.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuModel
{
    public Vector4 Bounds;
    public uint FirstCluster;
    public uint ClusterCount;
    public uint FirstLevel;
    public uint LevelCount;
    public uint FirstCard;
    public uint CardCount;
    public float CardDistance;
    public float PositionStep;
    public uint Field;

    /// <summary>
    /// How far from <see cref="Bounds"/>' middle, in the model's space, any cluster's error sphere or its parent's
    /// reaches: past the bounds, as each group's sphere encloses its clusters'. An instance picks the levels of clusters
    /// it hands on by how near and far this reaches, so it never passes over the level a cluster's own test wants.
    /// </summary>
    public float ClusterReach;
    public uint Padding0;
    public uint Padding1;
}

/// <summary>
/// One level of a model's clusters: where they are, the least error of any of them and the most of any of their
/// parents', which bound the distances it is drawn at.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuLevel
{
    public uint FirstCluster;
    public uint Count;
    public float MinError;
    public float MaxParentError;
}

/// <summary>
/// One cluster of a model (Core's <c>Cluster</c>): its sphere, its group's sphere and error and its parent's, where its
/// vertices and triangles start in the geometry's words, its counts, packed by <see cref="CountsOf"/>, and its model's
/// grid step, which its vertices' positions are in.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuCluster
{
    public Vector4 Bounds;
    public Vector4 ErrorBounds;
    public Vector4 ParentErrorBounds;
    public float Error;
    public float ParentError;
    public uint Vertices;
    public uint Triangles;
    public uint Counts;
    public float Step;
    public uint Padding0;
    public uint Padding1;

    /// <summary>
    /// Triangles in the low 8 bits, vertices in the next 9, the material slot in the 3 after, the level in the 5 after.
    /// </summary>
    public static uint CountsOf(int triangles, int vertices, int slot, int level) => (uint)triangles | ((uint)vertices << 8) | ((uint)slot << 17) | ((uint)level << 20);
}

/// <summary>
/// One card of a model (Core's <c>Card</c>): its centre, half width (less than 0 when it shows its mesh mirrored), half
/// height and turn about +Y, its material slot, and its atlases' texture refs, <see cref="TexturePools.None"/> while they
/// load.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuCard
{
    public Vector3 Centre;
    public float HalfWidth;
    public float HalfHeight;
    public float Turn;
    public uint Slot;
    public uint Surface;
    public uint Normals;
    public uint Padding0;
    public uint Padding1;
    public uint Padding2;
}

/// <summary>
/// Where a model's distance field is: the corner of its first texel and the size to its last, in the model's space,
/// and its brick's first texel in the atlas.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuMeshField
{
    public Vector3 Min;
    public float Padding0;
    public Vector3 Size;
    public float Padding1;
    public Vector3 Corner;
    public float Padding2;
}

/// <summary>
/// What a material gives global illumination, by its row: the colour it reflects and the light it gives off, in nits.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuMaterialGi
{
    public Vector4 Color;
    public Vector4 Emission;
}

/// <summary>
/// The first row of <c>GiLevels</c>: how many levels the scene's field and probes have this frame, and how they are
/// updated. As big as a <see cref="GpuGiLevel"/>, which the rows after it are.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = GpuGiLevel.Bytes)]
internal struct GpuGi
{
    public uint Levels;
    public uint Rays;

    /// <summary>
    /// The frame's number: it turns the rays, so a probe's rays go a new way each time it is traced, and picks which of
    /// each level's probes are traced.
    /// </summary>
    public uint Seed;

    public uint Padding0;

    /// <summary>
    /// Where the camera the levels are placed about is, from the world's origin: a place is lit by each level by how far
    /// from it it is, so where one level gives way to the next moves with the camera, never in a level's steps.
    /// </summary>
    public Vector3 Camera;
}

/// <summary>
/// One level of the scene's field and its probes, each finer one inside the next: its origin its first voxel's place in
/// voxels of <see cref="Voxel"/> metres from the world's origin, a box of <see cref="GiVolume.Resolution"/> a
/// side stored wrapping around, as is its grid of probes; the voxels made again this frame, a bit per brick of
/// <see cref="GiVolume.Brick"/> voxels a side by where it is stored, and the box around them; how many frames a probe
/// of it waits to be traced again, <see cref="Period"/>; and how much of what a probe held it keeps as it takes in what
/// it was traced with, <see cref="Hysteresis"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = Bytes)]
internal struct GpuGiLevel
{
    public const int Bytes = 112;

    public int OriginX;
    public int OriginY;
    public int OriginZ;
    public float Voxel;
    public int DirtyMinX;
    public int DirtyMinY;
    public int DirtyMinZ;
    public float Hysteresis;
    public int DirtyMaxX;
    public int DirtyMaxY;
    public int DirtyMaxZ;
    public uint Period;
    public BrickMask Dirty;
}

/// <summary>
/// A bit for each of a level's 512 bricks.
/// </summary>
[InlineArray(16)]
internal struct BrickMask
{
    private uint _word;
}

/// <summary>
/// The arguments of one indexed indirect draw, as the GPU reads them.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuDrawArgs
{
    public uint IndexCount;
    public uint InstanceCount;
    public uint FirstIndex;
    public int VertexOffset;
    public uint FirstInstance;
}
