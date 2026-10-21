using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Magic.ClusterRendering;

/// <summary>
/// What a <see cref="GpuInstance"/> carries besides its transform.
/// </summary>
[Flags]
internal enum GpuInstanceFlags : uint
{
    None = 0,

    /// <summary>
    /// Its scale turns it inside out, so its triangles wind the other way.
    /// </summary>
    Mirrored = 1,

    /// <summary>
    /// It dissolves into view from <see cref="GpuInstance.FadeStart"/>.
    /// </summary>
    FadeIn = 2,
}

/// <summary>
/// What a <see cref="GpuWorldLight"/> is.
/// </summary>
[Flags]
internal enum GpuWorldLightFlags : uint
{
    None = 0,

    /// <summary>
    /// There is a light in the slot.
    /// </summary>
    Live = 1,

    Spot = 2,

    /// <summary>
    /// It casts no shadow (Core's <c>PointLight.Shadowless</c>).
    /// </summary>
    Shadowless = 4,
}

/// <summary>
/// One piece of up to 32 triangles to draw: its instance's slot, its cluster's row times four plus which piece of it,
/// or a card's row with the top bit, and its slot among the view's pieces, which the visibility buffer names it by.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct GpuPiece(uint Instance, uint What, uint Slot);

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
    internal Matrix4x4 ViewProj;

    /// <summary>
    /// The view's rotation alone, world to view.
    /// </summary>
    internal Matrix4x4 View;

    /// <summary>
    /// Where the camera is, which every position a shader works with is relative to.
    /// </summary>
    internal Vector3 CameraPosition;
    internal float Time;

    /// <summary>
    /// The view's size in pixels as it is drawn, and one over it.
    /// </summary>
    internal Vector2 ViewSize;
    internal Vector2 ViewTexel;

    /// <summary>
    /// The projection's x and y scales, which turn a view position into the screen and back.
    /// </summary>
    internal Vector2 ProjScale;
    internal float Near;
    internal float Far;

    /// <summary>
    /// The directional light that casts the shadows (<see cref="KeyLightCopy"/>): the way its light travels, and its
    /// disc's half-angle in radians.
    /// </summary>
    internal Vector3 KeyLightDirection;
    internal float KeyLightAngularRadius;

    /// <summary>
    /// The key light's illuminance in lux, times its colour; zero with none.
    /// </summary>
    internal Vector3 KeyLightIlluminance;
    internal uint LightCount;

    internal uint FrameIndex;

    /// <summary>
    /// Light tiles across the view, and the 32-bit words of lights each tile's mask holds.
    /// </summary>
    internal uint TileCountX;
    internal uint TileWords;

    /// <summary>
    /// How the view's image is encoded as it is shown, a <see cref="Contexts.Gpu.TargetEncoding"/>: what the
    /// target's image expects, decided by its format.
    /// </summary>
    internal uint TargetEncoding;

    /// <summary>
    /// A view depth's z-bin is <c>log2(depth) * ZBinScale + ZBinBias</c>, of <see cref="ZBinCount"/>.
    /// </summary>
    internal float ZBinScale;
    internal float ZBinBias;
    internal uint ZBinCount;

    /// <summary>
    /// The sky's material's row, which a sky draw loads its params from.
    /// </summary>
    internal uint SkyMaterial;

    internal float DeltaSeconds;

    /// <summary>
    /// Pixels a cluster's error may cover before a finer one is drawn.
    /// </summary>
    internal float ClusterError;

    /// <summary>
    /// 1 when the key light is the sky's sun or moon, whose light reaches the world through the sky's air and clouds,
    /// the sky's shadow; 0 when it is a light alone.
    /// </summary>
    internal uint KeyLightThroughSky;

    /// <summary>
    /// How many of <see cref="Directional"/> light the world.
    /// </summary>
    internal uint DirectionalCount;

    /// <summary>
    /// The last frame's <see cref="ViewProj"/> without its jitter.
    /// </summary>
    internal Matrix4x4 PrevViewProj;

    /// <summary>
    /// The camera's position less the last frame's, in metres.
    /// </summary>
    internal Vector3 CameraMotion;

    /// <summary>
    /// Pixels a metre covers a metre in front of the camera, along the view's height.
    /// </summary>
    internal float PixelScale;

    /// <summary>
    /// The player's camera relative to this view's, which detail is kept nearest to when there is too much.
    /// </summary>
    internal Vector3 PriorityPosition;

    /// <summary>
    /// What uv gradients are scaled by: below 1 while a temporal upscale resolves the finer texels it reads.
    /// </summary>
    internal float TextureMipScale;

    /// <summary>
    /// This frame's sub-pixel offset of <see cref="ViewProj"/>, in clip space.
    /// </summary>
    internal Vector2 Jitter;

    /// <summary>
    /// The view's size in pixels as it is shown.
    /// </summary>
    internal Vector2 OutputSize;

    /// <summary>
    /// The world's weather, as its <see cref="Contexts.Components.Weather"/> says, or still and clear without one.
    /// </summary>
    internal WeatherConstants Weather;

    /// <summary>
    /// The sky's sun and moon, which the sky draws whether or not either lights the world now.
    /// </summary>
    internal SkyLightConstants SkyLights;

    /// <summary>
    /// The directional lights that light the world alone, without shadows, the first <see cref="DirectionalCount"/>.
    /// </summary>
    internal DirectionalLights Directional;

    /// <summary>
    /// The pass's params, packed by its <see cref="ParamLayout"/>, in the first seven rows; its iteration in the last.
    /// </summary>
    internal PassWords Pass;
}

/// <summary>
/// The sky's sun and moon in a <see cref="FrameConstants"/>, <c>SkyLightConstants</c> in Shaders/Include/Frame.hlsli
/// member for member: the way each one's light travels, its illuminance in lux times its colour, zero without one, and
/// its disc's half-angle in radians.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SkyLightConstants
{
    internal Vector3 SunDirection;
    internal float SunAngularRadius;
    internal Vector3 SunIlluminance;
    internal float MoonAngularRadius;
    internal Vector3 MoonDirection;
    internal float Padding0;
    internal Vector3 MoonIlluminance;
    internal float Padding1;
}

/// <summary>
/// A directional light that lights the world alone in a <see cref="FrameConstants"/>,
/// <c>DirectionalLightConstants</c> in Shaders/Include/Frame.hlsli member for member: the way its light travels, and
/// its illuminance in lux times its colour.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DirectionalLightConstants
{
    internal Vector3 Direction;
    internal float Padding0;
    internal Vector3 Illuminance;
    internal float Padding1;
}

/// <summary>
/// The directional lights a <see cref="FrameConstants"/> holds: as many as a world's are lit by besides its sun and
/// moon.
/// </summary>
[InlineArray(Capacity)]
internal struct DirectionalLights
{
    internal const int Capacity = 4;

    private DirectionalLightConstants _light;
}

/// <summary>
/// The world's weather in a <see cref="FrameConstants"/>, <c>WeatherConstants</c> in Shaders/Include/Frame.hlsli member
/// for member: the wind in metres per second, how much of the sky the clouds cover and how thick they are, 0 to 1 each,
/// and their linear colours where the sun lights them and in their own shade; how much fog there is a metre, its tint,
/// the metres before it begins, and its share by height, a curve as <see cref="ParamLayout.WriteCurve"/> packs one.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WeatherConstants
{
    internal Vector3 Wind;
    internal float Coverage;
    internal Vector3 CloudColor;
    internal float Density;
    internal Vector3 CloudShadow;
    internal float Fog;
    internal Vector3 FogColor;
    internal float FogStart;
    internal CurveRows FogFalloff;
}

/// <summary>
/// A curve's sixteen floats, Shaders/Include/Curve.hlsli's <c>Curve</c>.
/// </summary>
[InlineArray(16)]
internal struct CurveRows
{
    private float _value;
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
/// One instance in the instances' table, at its entity's slot (Shaders/Include/Structs.hlsli's GpuInstance): the rows
/// of its transpose(world), xyz its turn and scale and w its position; the last frame's the same way, which velocity is
/// worked out from; its largest scale, flags, model row, when its fade started, and its eight material slots' rows, two
/// to a word.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuInstance
{
    internal Vector4 Row0;
    internal Vector4 Row1;
    internal Vector4 Row2;
    internal Vector4 PrevRow0;
    internal Vector4 PrevRow1;
    internal Vector4 PrevRow2;
    internal float MaxScale;

    /// <summary>
    /// <see cref="GpuInstanceFlags"/>.
    /// </summary>
    internal uint Flags;

    internal uint Model;

    /// <summary>
    /// When its fade started, in the frame's <c>Time</c>, while <see cref="Flags"/> says it fades.
    /// </summary>
    internal float FadeStart;

    internal MaterialRows Materials;
}

/// <summary>
/// An instance's eight material slots' rows, two to a word.
/// </summary>
[InlineArray(4)]
internal struct MaterialRows
{
    private uint _word;
}

/// <summary>
/// A model on the GPU (<see cref="GpuResources"/>): its sphere about its origin, its clusters, levels and cards, the
/// unit-scale distance its cards are drawn from, 0 for none, its grid's step, its distance field's slot in
/// <c>MeshFields</c>, 0 for none, and how far from its sphere's middle its clusters' error spheres reach.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuModel
{
    internal Vector4 Bounds;
    internal uint FirstCluster;
    internal uint ClusterCount;
    internal uint FirstLevel;
    internal uint LevelCount;
    internal uint FirstCard;
    internal uint CardCount;
    internal float CardDistance;
    internal float PositionStep;
    internal uint Field;

    /// <summary>
    /// How far from <see cref="Bounds"/>' middle, in the model's space, any cluster's error sphere or its parent's
    /// reaches: past the bounds, as each group's sphere encloses its clusters'. An instance picks the levels of
    /// clusters it hands on by how near and far this reaches, so it never passes over the level a cluster's own test
    /// wants.
    /// </summary>
    internal float ClusterReach;
    internal uint Padding0;
    internal uint Padding1;
}

/// <summary>
/// One level of a model's clusters: where they are, the least error of any of them and the most of any of their
/// parents', which bound the distances it is drawn at.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuLevel
{
    internal uint FirstCluster;
    internal uint Count;
    internal float MinError;
    internal float MaxParentError;
}

/// <summary>
/// One cluster of a model (Core's <c>Cluster</c>): its sphere, its group's sphere and error and its parent's, where its
/// vertices and triangles start in the geometry's words, its counts, packed by <see cref="PackCounts"/>, and its
/// model's grid step, which its vertices' positions are in.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuCluster
{
    internal Vector4 Bounds;
    internal Vector4 ErrorBounds;
    internal Vector4 ParentErrorBounds;
    internal float Error;
    internal float ParentError;
    internal uint Vertices;
    internal uint Triangles;
    internal uint Counts;
    internal float Step;
    internal uint Padding0;
    internal uint Padding1;

    /// <summary>
    /// Triangles in the low 8 bits, vertices in the next 9, the material slot in the 3 after, the level in the 5 after.
    /// </summary>
    internal static uint PackCounts(int triangles, int vertices, int slot, int level)
        => (uint)triangles | ((uint)vertices << 8) | ((uint)slot << 17) | ((uint)level << 20);
}

/// <summary>
/// One card of a model (Core's <c>Card</c>): its centre, half width (less than 0 when it shows its mesh mirrored), half
/// height and turn about +Y, its material slot, and its atlases' texture refs, <see cref="GpuContract.NoTexture"/>
/// while they load.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuCard
{
    internal Vector3 Centre;
    internal float HalfWidth;
    internal float HalfHeight;
    internal float Turn;
    internal uint Slot;
    internal uint Surface;
    internal uint Normals;
    internal uint Padding0;
    internal uint Padding1;
    internal uint Padding2;
}

/// <summary>
/// Where a model's distance field is: the corner of its first texel and the size to its last, in the model's space,
/// and its brick's first texel in the atlas.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuMeshField
{
    internal Vector3 Min;
    internal float Padding0;
    internal Vector3 Size;
    internal float Padding1;
    internal Vector3 Corner;
    internal float Padding2;
}

/// <summary>
/// What a material gives global illumination, by its row: the colour it reflects and the light it gives off, in nits.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuMaterialGi
{
    internal Vector4 Color;
    internal Vector4 Emission;
}

/// <summary>
/// The first row of <c>GiLevels</c>: how many levels the world's field and probes have this frame, and how they are
/// updated. As big as a <see cref="GpuGiLevel"/>, which the rows after it are.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = GpuGiLevel.Bytes)]
internal struct GpuGi
{
    internal uint Levels;
    internal uint Rays;

    /// <summary>
    /// The frame's number: it turns the rays, so a probe's rays go a new way each time it is traced, and picks which of
    /// each level's probes are traced.
    /// </summary>
    internal uint Seed;

    /// <summary>
    /// 1 when a probe keeps more of what it held through a change no larger than its rays' noise, so the light does not
    /// waver; 0 while the world is readied, when each probe holds the plain mean of its tracings so it settles fast.
    /// </summary>
    internal uint Steadies;

    /// <summary>
    /// Where the camera the levels are placed about is, from the world's origin: a place is lit by each level by how
    /// far from it it is, so where one level gives way to the next moves with the camera, never in a level's steps.
    /// </summary>
    internal Vector3 Camera;
}

/// <summary>
/// One level of the world's field and its probes, each finer one inside the next: its origin its first voxel's place in
/// voxels of <see cref="Voxel"/> metres from the world's origin, a box of <see cref="GiVolume.Resolution"/> a
/// side stored wrapping around, as is its grid of probes; the voxels made again this frame, a bit per brick of
/// <see cref="GiVolume.Brick"/> voxels a side by where it is stored, and the box around them; how many frames a probe
/// of it waits to be traced again, <see cref="Period"/>; and how much of what a probe held it keeps as it takes in what
/// it was traced with, <see cref="Hysteresis"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = Bytes)]
internal struct GpuGiLevel
{
    internal const int Bytes = 112;

    internal int OriginX;
    internal int OriginY;
    internal int OriginZ;
    internal float Voxel;
    internal int DirtyMinX;
    internal int DirtyMinY;
    internal int DirtyMinZ;
    internal float Hysteresis;
    internal int DirtyMaxX;
    internal int DirtyMaxY;
    internal int DirtyMaxZ;
    internal uint Period;
    internal BrickMask Dirty;
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
    internal uint IndexCount;
    internal uint InstanceCount;
    internal uint FirstIndex;
    internal int VertexOffset;
    internal uint FirstInstance;
}

/// <summary>
/// The key light's clipmap this frame (Shaders/Include/VirtualShadows.hlsli's GpuClipmap), its levels after it: the two
/// axes across it and the way the light travels, how many levels it has, the frame's number (pages last read before it
/// are free to take), whether every page starts again, how far along the light the camera is from the plane depth is
/// measured from, how deep a page reaches either side of it, the finest level's width, the words of the changed lights'
/// bits, the pool's pages a side, the pages of it whole and the small pages the rest is split into, how many spheres
/// moved, where the main camera is, which the clipmap is placed about, and the rows of the tables where the levels'
/// entries, this frame's lights' table and the last frame's start, and how many entries a lights' table has.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuClipmap
{
    internal Vector3 Right;
    internal uint Levels;
    internal Vector3 Up;
    internal uint Frame;
    internal Vector3 Direction;
    internal uint AllDirty;
    internal float CameraDepth;
    internal float DepthRange;
    internal float Finest;
    internal uint ChangedLightWords;
    internal uint PoolAcross;
    internal uint WholePages;
    internal uint SmallPages;
    internal uint Moved;
    internal Vector3 Camera;
    internal uint LevelTable;
    internal uint LightTable;
    internal uint LightTableLast;
    internal uint LightEntries;
    internal uint Padding0;
    internal uint Padding1;
    internal uint Padding2;
    internal uint Padding3;
    internal uint Padding4;
}

/// <summary>
/// One level of the clipmap: its window's first page, counted from the clipmap's origin, metres a page and a texel, and
/// where the camera is across the light from the window's corner, in metres.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuClipmapLevel
{
    internal int OriginX;
    internal int OriginY;
    internal float PageSize;
    internal float Texel;
    internal float CameraX;
    internal float CameraY;
    internal float Padding0;
    internal float Padding1;
}

/// <summary>
/// One row of the materials buffer: a surface's <c>MaterialParams</c>, packed by its <see cref="ParamLayout"/>.
/// </summary>
[InlineArray(32)]
internal struct GpuMaterial
{
    private uint _word;
}

/// <summary>
/// A point or spot light in the world's table (Shaders/Include/Structs.hlsli's GpuWorldLight): where it is; how far it
/// reaches; its intensity in candela times its colour; the size of its bulb; for a spot its axis and the scale and
/// offset of its cone's falloff; and <see cref="GpuWorldLightFlags"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuWorldLight
{
    internal Vector3 Position;
    internal float Range;
    internal Vector3 Intensity;
    internal float Radius;
    internal Vector3 Forward;
    internal float SpotScale;
    internal float SpotOffset;
    internal uint Flags;
    internal uint Padding0;
    internal uint Padding1;
}
