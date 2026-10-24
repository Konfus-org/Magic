using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;

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
    /// It dissolves into view from <see cref="GpuInstance.FadeStartSec"/>.
    /// </summary>
    FadeIn = 2,

    /// <summary>
    /// It casts no shadow (<see cref="Contexts.Components.Renderer.IsShadowless"/>): the voxels leave it out.
    /// </summary>
    Shadowless = 4,
}

/// <summary>
/// The first of every pass's constants, Shaders/Core/Pass.slang's <c>PassHeader</c>: the buffer of the frame's
/// <see cref="WorldConstants"/>, and of the view's <see cref="ViewConstants"/> the pass is drawn for, none for a pass
/// drawn for no view.
/// </summary>
[ShaderStruct("Shaders/Core/Pass.slang", "PassHeader")]
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PassHeader(GpuBuffer Frame, GpuBuffer View);

/// <summary>
/// A pass's constants: the header, then the pass's own, as its entry point takes them, one uniform parameter after the
/// other.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PassConstants<T>(PassHeader Header, T Values)
    where T : unmanaged;

/// <summary>
/// The image a post effect is handed and the texture of its size and format it writes, Shaders/Core/Post.slang's
/// <c>PostImages</c>, which its constants hold after the header.
/// </summary>
[ShaderStruct("Shaders/Core/Post.slang", "PostImages")]
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PostImages(GpuTexture Image, GpuTexture Output);

/// <summary>
/// The world as a frame draws it, in the buffer every pass's header names, Shaders/Renderer/World.slang's
/// <c>WorldConstants</c>: the frame's clocks first, the world's lights, the heap ids of its tables and the rest its
/// passes read, its weather and its sky's sun and moon. A new frame global is a member here and there.
/// </summary>
[ShaderStruct("Shaders/Renderer/World.slang", "WorldConstants")]
[StructLayout(LayoutKind.Sequential)]
internal struct WorldConstants
{
    internal FrameConstants Clocks;
    internal WorldLights Lights;

    /// <summary>
    /// The world's tables (<see cref="GpuTables"/>) and the clusters' geometry.
    /// </summary>
    internal GpuBuffer Instances;
    internal GpuBuffer LocalLights;
    internal GpuBuffer Models;
    internal GpuBuffer Levels;
    internal GpuBuffer Clusters;
    internal GpuBuffer Cards;
    internal GpuBuffer Materials;
    internal GpuBuffer MaterialBins;
    internal GpuBuffer Geometry;

    /// <summary>
    /// The sky's material's row, which a sky's passes load it from.
    /// </summary>
    internal uint SkyMaterial;

    /// <summary>
    /// The sampler materials' textures are read with.
    /// </summary>
    internal GpuSampler MaterialSampler;

    /// <summary>
    /// The frame's raised failure codes, a word each (Shaders/Core/Failure.slang).
    /// </summary>
    internal GpuBuffer Failures;

    /// <summary>
    /// The sky's light from everywhere, nine harmonics.
    /// </summary>
    internal GpuBuffer SkySh;

    /// <summary>
    /// The world's weather, as its <c>Weather</c> says, or still and clear without one.
    /// </summary>
    internal WeatherConstants Weather;

    /// <summary>
    /// The sky's sun and moon, which the sky draws whether or not either lights the world now.
    /// </summary>
    internal CelestialConstants Celestial;
}

/// <summary>
/// A view's constants for a frame, in the buffer its passes' headers name, Shaders/Renderer/World.slang's
/// <c>ViewConstants</c>: its camera, its sizes and its exposure. A matrix is row-major, as System.Numerics keeps it,
/// applied to a row vector.
/// </summary>
[ShaderStruct("Shaders/Renderer/World.slang", "ViewConstants")]
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

    /// <summary>
    /// Pixels a cluster's error may cover before a finer one is drawn.
    /// </summary>
    internal float ClusterError;

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

    /// <summary>
    /// How the view's image is encoded as it is shown, a <see cref="Contexts.Gpu.TargetEncoding"/>.
    /// </summary>
    internal uint TargetEncoding;

    /// <summary>
    /// The view's exposure, which lighting and the sky are pre-exposed by (<see cref="View.Exposure"/>).
    /// </summary>
    internal GpuBuffer Exposure;
}

/// <summary>
/// The frame's clocks, Shaders/Core/Pass.slang's <c>FrameConstants</c>, which a <see cref="WorldConstants"/> starts
/// with: the world's time and the real time since the first frame, the real time since the last, and the frame's
/// number.
/// </summary>
[ShaderStruct("Shaders/Core/Pass.slang", "FrameConstants")]
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct FrameConstants(float WorldSec, float RealSec, float RealDeltaSec, uint Index)
{
    internal FrameConstants(in Frame frame)
        : this((float)frame.WorldSec, (float)frame.RealSec, frame.RealDeltaSec, (uint)(frame.Index % uint.MaxValue))
    {
    }
}

/// <summary>
/// The sky's sun and moon in a <see cref="WorldConstants"/>, Shaders/Core/Sky.slang's <c>CelestialConstants</c>: the
/// way each one's light travels, its illuminance in lux times its colour, zero without one, and its disc's half-angle
/// in radians.
/// </summary>
[ShaderStruct("Shaders/Core/Sky.slang", "CelestialConstants")]
[StructLayout(LayoutKind.Sequential)]
internal struct CelestialConstants
{
    internal Vector3 SunDirection;
    internal float SunAngularRadius;
    internal Vector3 SunIlluminance;
    internal float MoonAngularRadius;
    internal Vector3 MoonDirection;
    internal Vector3 MoonIlluminance;
}

/// <summary>
/// A directional light, Shaders/Lighting/Lights.slang's <c>DirectionalLight</c>: the way its light travels, its disc's
/// half-angle in radians, and its illuminance in lux times its colour.
/// </summary>
[ShaderStruct("Shaders/Lighting/Lights.slang", "DirectionalLight")]
[StructLayout(LayoutKind.Sequential)]
internal struct DirectionalLightConstants
{
    internal Vector3 Direction;
    internal float AngularRadius;
    internal Vector3 Illuminance;
}

/// <summary>
/// The lights the world is lit by, Shaders/Lighting/Lights.slang's <c>WorldLights</c>: the key light, which casts the
/// shadows, and the other directional lights, the first <see cref="DirectionalCount"/>.
/// </summary>
[ShaderStruct("Shaders/Lighting/Lights.slang", "WorldLights")]
[StructLayout(LayoutKind.Sequential)]
internal struct WorldLights
{
    internal DirectionalLightConstants Key;
    internal uint DirectionalCount;
    internal DirectionalLights Directional;
}

/// <summary>
/// The directional lights a <see cref="WorldLights"/> holds besides the key light.
/// </summary>
[InlineArray(Capacity)]
internal struct DirectionalLights
{
    internal const int Capacity = 4;

    private DirectionalLightConstants _light;
}

/// <summary>
/// The world's weather in a <see cref="WorldConstants"/>, Shaders/Core/Weather.slang's <c>WeatherConstants</c>: the
/// wind in metres per second, how much of the sky the clouds cover and how thick they are, 0 to 1 each, and their
/// linear colours where the sun lights them and in their own shade.
/// </summary>
[ShaderStruct("Shaders/Core/Weather.slang", "WeatherConstants")]
[StructLayout(LayoutKind.Sequential)]
internal struct WeatherConstants
{
    internal Vector3 Wind;
    internal float Coverage;
    internal Vector3 CloudColor;
    internal float Density;
    internal Vector3 CloudShadow;
}

/// <summary>
/// A point or spot light in the lights' table, at its entity's slot, Shaders/Lighting/Lights.slang's <c>LocalLight</c>:
/// its centre in the world, which the shaders take camera-relative, the metres it reaches, its intensity in candela
/// times its colour, its radius, a spot's axis and the scale and offset of its cone's fade (a point light's 0 and 1),
/// and its <see cref="GpuLocalLightFlags"/>. A slot with no light reaches nowhere.
/// </summary>
[ShaderStruct("Shaders/Lighting/Lights.slang", "LocalLight")]
[StructLayout(LayoutKind.Sequential)]
internal struct GpuLocalLight
{
    internal Vector3 Position;
    internal float Range;
    internal Vector3 Intensity;
    internal float Radius;
    internal Vector3 Forward;
    internal float SpotScale;
    internal float SpotOffset;
    internal uint Flags;
}

/// <summary>
/// One level of the voxels' clipmap, Shaders/Renderer/Voxels.slang's <c>GpuVoxelLevel</c>: its window's lowest corner
/// in voxels of the world, a whole number of bricks, the metres a voxel is, where the corner was the frame before, and
/// its flags.
/// </summary>
[ShaderStruct("Shaders/Renderer/Voxels.slang", "GpuVoxelLevel")]
[StructLayout(LayoutKind.Sequential)]
internal struct GpuVoxelLevel
{
    /// <summary>
    /// The flag of a level put in place anew, every brick of which is voxelized.
    /// </summary>
    internal const uint Reset = 1;

    internal Vector3 Origin;
    internal float VoxelSize;
    internal Vector3 LastOrigin;
    internal uint Flags;
}

/// <summary>
/// What a <see cref="GpuLocalLight"/> carries besides its light.
/// </summary>
[Flags]
internal enum GpuLocalLightFlags : uint
{
    None = 0,

    /// <summary>
    /// Its light reaches through everything.
    /// </summary>
    Shadowless = 1,
}

/// <summary>
/// One instance in the instances' table, at its entity's slot: the rows of its transpose(world), xyz its turn and scale
/// and w its position; the last frame's the same way, which velocity is worked out from; its largest scale, flags,
/// model row, when its fade started, and its eight material slots' rows, two to a word.
/// </summary>
[ShaderStruct("Shaders/Renderer/World.slang", "GpuInstance")]
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
    /// When its fade started, in the frame's real time (<see cref="FrameConstants.RealSec"/>), while
    /// <see cref="Flags"/> says it fades.
    /// </summary>
    internal float FadeStartSec;

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
[ShaderStruct("Shaders/Renderer/World.slang", "GpuModel")]
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
[ShaderStruct("Shaders/Renderer/World.slang", "GpuLevel")]
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
[ShaderStruct("Shaders/Renderer/World.slang", "GpuCluster")]
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
[ShaderStruct("Shaders/Renderer/World.slang", "GpuCard")]
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
/// One row of the materials' table: a surface's or a sky's struct, laid out as its reflection says
/// (<see cref="ShaderStruct"/>).
/// </summary>
[InlineArray(32)]
internal struct GpuMaterial
{
    private uint _word;
}
