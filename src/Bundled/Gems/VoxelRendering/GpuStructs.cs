using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Magic.VoxelRendering;

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
/// A view's constants for a frame, in the buffer its shaders read them from (<c>ViewConstants</c> in
/// Shaders/Rendering/Include/View.hlsli, member for member: change both together): its camera, its sizes, the world's
/// lights and weather, and the heap ids of the world's tables. Every group of members is 16 bytes, which packs the same
/// in every API's storage buffers. A matrix is its four rows, as System.Numerics keeps them, applied to a row vector,
/// <c>mul(v, M)</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ViewConstants
{
    /// <summary>
    /// Camera-relative: positions minus the camera's, through the view and a reverse-Z projection with no far plane.
    /// </summary>
    internal Matrix4x4 ViewProj;

    /// <summary>
    /// The view's rotation alone, world to view.
    /// </summary>
    internal Matrix4x4 Rotation;

    /// <summary>
    /// The last frame's <see cref="ViewProj"/>.
    /// </summary>
    internal Matrix4x4 PrevViewProj;

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
    /// The directional light that casts the shadows: the way its light travels, and its disc's half-angle in radians.
    /// </summary>
    internal Vector3 KeyLightDirection;
    internal float KeyLightAngularRadius;

    /// <summary>
    /// The key light's illuminance in lux, times its colour; zero with none.
    /// </summary>
    internal Vector3 KeyLightIlluminance;
    internal uint FrameIndex;

    /// <summary>
    /// The view's size in pixels as it is shown, and this frame's sub-pixel offset of <see cref="ViewProj"/>.
    /// </summary>
    internal Vector2 OutputSize;
    internal Vector2 Jitter;

    /// <summary>
    /// The camera's position less the last frame's, in metres; and pixels a metre covers a metre in front of the
    /// camera, along the view's height.
    /// </summary>
    internal Vector3 CameraMotion;
    internal float PixelScale;

    /// <summary>
    /// The player's camera relative to this view's, which detail is kept nearest to when there is too much; and what uv
    /// gradients are scaled by.
    /// </summary>
    internal Vector3 PriorityPosition;
    internal float TextureMipScale;

    internal float DeltaSeconds;

    /// <summary>
    /// Pixels a cluster's error may cover before a finer one is drawn.
    /// </summary>
    internal float ClusterError;

    /// <summary>
    /// 1 when the key light is the sky's sun or moon, whose light reaches the world through the sky's air and clouds.
    /// </summary>
    internal uint KeyLightThroughSky;

    /// <summary>
    /// How many of <see cref="Directional"/> light the world.
    /// </summary>
    internal uint DirectionalCount;

    /// <summary>
    /// How the view's image is encoded as it is shown, a <see cref="Contexts.Gpu.TargetEncoding"/>.
    /// </summary>
    internal uint TargetEncoding;

    /// <summary>
    /// The sky's material's row, which a sky's passes load its params from.
    /// </summary>
    internal uint SkyMaterial;

    /// <summary>
    /// The heap id of the sampler materials' textures are read with.
    /// </summary>
    internal uint MaterialSampler;

    /// <summary>
    /// The heap id of the frame's raised failure codes, a word each (Shaders/Include/Failure.hlsli).
    /// </summary>
    internal uint Failures;

    /// <summary>
    /// The heap ids of the world's tables (<see cref="GpuTables"/>).
    /// </summary>
    internal uint Instances;
    internal uint Models;
    internal uint Levels;
    internal uint Clusters;

    internal uint Cards;
    internal uint Materials;
    internal uint MaterialBins;
    internal uint Geometry;

    /// <summary>
    /// The heap ids of the view's exposure, which lighting and the sky are pre-exposed by, and of the sky's harmonics.
    /// </summary>
    internal uint Exposure;
    internal uint SkySh;
    internal uint Padding0;
    internal uint Padding1;

    /// <summary>
    /// The world's weather, as its <c>Weather</c> says, or still and clear without one.
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
}

/// <summary>
/// The sky's sun and moon in a <see cref="ViewConstants"/>: the way each one's light travels, its illuminance in lux
/// times its colour, zero without one, and its disc's half-angle in radians.
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
/// A directional light that lights the world alone in a <see cref="ViewConstants"/>: the way its light travels, and its
/// illuminance in lux times its colour.
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
/// The directional lights a <see cref="ViewConstants"/> holds: as many as a world's are lit by besides its sun and moon.
/// </summary>
[InlineArray(Capacity)]
internal struct DirectionalLights
{
    internal const int Capacity = 4;

    private DirectionalLightConstants _light;
}

/// <summary>
/// The world's weather in a <see cref="ViewConstants"/>: the wind in metres per second, how much of the sky the clouds
/// cover and how thick they are, 0 to 1 each, and their linear colours where the sun lights them and in their own
/// shade; how much fog there is a metre, its tint, the metres before it begins, and its share by height as a curve.
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
/// One instance in the instances' table, at its entity's slot: the rows of its transpose(world), xyz its turn and scale
/// and w its position; the last frame's the same way, which velocity is worked out from; its largest scale, flags,
/// model row, when its fade started, and its eight material slots' rows, two to a word.
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
/// A model on the GPU: its sphere about its origin, its clusters, levels and cards, the unit-scale distance its cards
/// are drawn from, 0 for none, its grid's step, and how far from its sphere's middle its clusters' error spheres reach.
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

    /// <summary>
    /// How far from <see cref="Bounds"/>' middle, in the model's space, any cluster's error sphere or its parent's
    /// reaches: past the bounds, as each group's sphere encloses its clusters'. An instance picks the levels of
    /// clusters it hands on by how near and far this reaches, so it never passes over the level a cluster's own test
    /// wants.
    /// </summary>
    internal float ClusterReach;
    internal uint Padding0;
    internal uint Padding1;
    internal uint Padding2;
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
/// height and turn about +Y, its material slot, and its atlases' texture refs, <see cref="GpuTextures.None"/> while
/// they load.
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
/// One row of the materials' table: a surface's <c>MaterialParams</c>, packed by its <see cref="ParamLayout"/>.
/// </summary>
[InlineArray(32)]
internal struct GpuMaterial
{
    private uint _word;
}
