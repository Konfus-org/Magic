using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The sun's cascades of one view: a row each in <see cref="Views"/>, and the size of the square atlas they are drawn
/// into, in texels.
/// </summary>
/// <remarks>
/// The shadow gem makes it for each view. The geometry gem draws each cascade's depth into its rectangle of the atlas
/// and gives the atlas back as <see cref="CascadeDepth"/>.
/// </remarks>
public readonly record struct CascadeViews(GpuBuffer Views, int Count, int AtlasSize);

/// <summary>
/// The depth of each of a view's <see cref="CascadeViews"/>, drawn into one atlas: reversed, 1 at the light's near plane
/// and 0 where nothing is.
/// </summary>
public readonly record struct CascadeDepth(GpuTexture Atlas);

/// <summary>
/// The shadow tiles of the frame's local lights, which every view shares: the views that draw into the cache of still
/// depth this frame and those that draw into the local atlas, both atlases, and the tables that shading reads.
/// </summary>
/// <remarks>
/// <para>
/// The shadow gem makes it once a frame, and has already copied each tile whose cache holds its still depth from the
/// cache into the atlas, and cleared the rectangle of each tile that the cache draws again. The geometry gem then draws
/// <see cref="CacheViews"/> into <see cref="Cache"/> and <see cref="AtlasViews"/> into <see cref="Atlas"/> over what
/// they hold, each with its <see cref="GpuShadowView.Casters"/>, and gives the atlas back as
/// <see cref="LocalShadowDepth"/>.
/// </para>
/// <para>
/// Positions are relative to <see cref="Anchor"/>, the frame's main camera, which the world's buffer holds.
/// <see cref="Lights"/> has a 16-byte row for each light of the light table: its first tile, 0xFFFFFFFF for none, its
/// number of faces, and its width. <see cref="Tiles"/> has an 80-byte row for each tile: its view-projection and its
/// rectangle of the atlas.
/// </para>
/// </remarks>
public readonly record struct LocalShadowViews
{
    public required GpuBuffer CacheViews { get; init; }

    public required int CacheCount { get; init; }

    public required GpuBuffer AtlasViews { get; init; }

    public required int AtlasCount { get; init; }

    public required GpuTexture Cache { get; init; }

    public required GpuTexture Atlas { get; init; }

    public required int AtlasSize { get; init; }

    public required GpuBuffer Lights { get; init; }

    public required GpuBuffer Tiles { get; init; }

    public required Vector3 Anchor { get; init; }
}

/// <summary>
/// The local atlas of <see cref="LocalShadowViews"/> with each view's depth drawn into it.
/// </summary>
public readonly record struct LocalShadowDepth(GpuTexture Atlas);

/// <summary>
/// What shading reads to shadow the frame's local lights: the tables and the atlas of <see cref="LocalShadowViews"/>,
/// the atlas's min and max mips for the blocker search, and how the filter reads them.
/// </summary>
/// <remarks>
/// The shading step writes its ids into each view's buffer as <see cref="View.GpuLocalShadows"/>.
/// </remarks>
public readonly record struct LocalShadows
{
    public required GpuBuffer Lights { get; init; }

    public required GpuBuffer Tiles { get; init; }

    public required GpuTexture Atlas { get; init; }

    public required GpuTexture MinMips { get; init; }

    public required GpuTexture MaxMips { get; init; }

    public required int MipLevels { get; init; }

    public required GpuSampler Sampler { get; init; }

    public required Vector3 Anchor { get; init; }

    public required int BlockerTaps { get; init; }

    public required int FilterTaps { get; init; }

    public required float MaxPenumbraTexels { get; init; }

    public required float NormalBias { get; init; }
}

/// <summary>
/// Which casters a shadow view draws, the twin of <c>Scene/ShadowViews.slang</c>'s.
/// </summary>
public enum ShadowCasters : uint
{
    All,

    /// <summary>
    /// Only those that are not dynamic bodies: what a cache of still depth holds.
    /// </summary>
    Still,

    /// <summary>
    /// Only the dynamic bodies, drawn over a copy of the cache.
    /// </summary>
    Moving,
}

/// <summary>
/// A local light's row of <see cref="LocalShadowViews.Lights"/>, the twin of <c>Scene/ShadowViews.slang</c>'s, 16
/// bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuLightShadow
{
    public const int Bytes = 16;

    /// <summary>
    /// The light's no shadow.
    /// </summary>
    public const uint NoTile = 0xFFFFFFFF;

    public uint FirstTile;

    /// <summary>
    /// 1 for a spot light, 5 for a rect light and 6 for a point light.
    /// </summary>
    public uint FaceCount;

    /// <summary>
    /// The width of the light's source in metres, which sets how soft its shadows are.
    /// </summary>
    public float LightWidth;

    public float Padding;
}

/// <summary>
/// A tile's row of <see cref="LocalShadowViews.Tiles"/>, the twin of <c>Scene/ShadowViews.slang</c>'s, 80 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuShadowTile
{
    public const int Bytes = 80;

    /// <summary>
    /// The anchor-relative world to the tile's clip space, with reversed depth.
    /// </summary>
    public Matrix4x4 ViewProjection;

    /// <summary>
    /// The tile's rectangle of the atlas, in texels: x, y, width and height. A width of 0 is a face that is not drawn.
    /// </summary>
    public uint RectX;

    public uint RectY;

    public uint RectWidth;

    public uint RectHeight;
}

/// <summary>
/// A view that a shadow map is drawn from, as the GPU reads it: the twin of <c>Scene/ShadowViews.slang</c>'s, 224
/// bytes.
/// </summary>
/// <remarks>
/// Its positions are relative to the camera of the frame, as every position on the GPU is.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuShadowView
{
    public const int Bytes = 224;

    /// <summary>
    /// The camera-relative world to the view's clip space, with reversed depth.
    /// </summary>
    public Matrix4x4 ViewProjection;

    /// <summary>
    /// The sides, the near plane and the far plane of the view, each a unit normal that points in and an offset, as
    /// the frame's frustum planes are.
    /// </summary>
    public Vector4 Plane0;

    public Vector4 Plane1;

    public Vector4 Plane2;

    public Vector4 Plane3;

    public Vector4 Plane4;

    public Vector4 Plane5;

    /// <summary>
    /// The view's rectangle of its atlas, in texels from the top left: x, y, width and height.
    /// </summary>
    public uint RectX;

    public uint RectY;

    public uint RectWidth;

    public uint RectHeight;

    /// <summary>
    /// What takes the view's clip x and y into the atlas's: the scale in x and y, then the offset.
    /// </summary>
    public Vector4 AtlasTransform;

    /// <summary>
    /// The unit direction towards the light.
    /// </summary>
    public Vector3 Towards;

    /// <summary>
    /// The metres that one texel covers: at any depth in an orthographic view, and a metre from the light in a
    /// perspective one, whose texels grow with the distance.
    /// </summary>
    public float TexelSize;

    /// <summary>
    /// 1 for an orthographic view, whose casters nearer the light than its near plane are drawn at the near plane.
    /// </summary>
    public uint IsOrthographic;

    /// <summary>
    /// For an orthographic view, the metres from the near plane to the far plane, which a difference of reversed depth
    /// is a share of. For a perspective view, the metres to its near plane, which a reversed depth is over the view
    /// depth.
    /// </summary>
    public float DepthRange;

    public ShadowCasters Casters;

    public uint Padding;
}

/// <summary>
/// The sky's direction maps, which every view shares: for each of a fixed set of directions on the upper hemisphere,
/// an orthographic depth map of the still world around the main camera as the sky sees it from there, near and far.
/// </summary>
/// <remarks>
/// <para>
/// The light gem makes it once a frame, and has already cleared each rectangle that this frame draws again. A geometry
/// gem then draws <see cref="NearDraws"/> into <see cref="NearAtlas"/> and <see cref="FarDraws"/> into
/// <see cref="FarAtlas"/> over what they hold, and gives both back as <see cref="SkyDepth"/>. A draw is a whole map, or
/// the part of one that a change to the world touched, as a view of its own.
/// </para>
/// <para>
/// <see cref="NearMaps"/> and <see cref="FarMaps"/> hold each whole map's view, by which a point is tested. A map
/// whose depth is not yet drawn has a rectangle of no width. Positions are relative to <see cref="Anchor"/>. Both
/// atlases are <c>D16Unorm</c>, the one exception to the engine's 32-bit depth.
/// </para>
/// </remarks>
public readonly record struct SkyViews
{
    public required GpuBuffer NearDraws { get; init; }

    public required int NearDrawCount { get; init; }

    public required GpuBuffer FarDraws { get; init; }

    public required int FarDrawCount { get; init; }

    public required GpuTexture NearAtlas { get; init; }

    public required GpuTexture FarAtlas { get; init; }

    public required GpuBuffer NearMaps { get; init; }

    public required int NearCount { get; init; }

    public required GpuBuffer FarMaps { get; init; }

    public required int FarCount { get; init; }

    public required Vector3 Anchor { get; init; }
}

/// <summary>
/// The atlases of <see cref="SkyViews"/> with this frame's draws drawn into them.
/// </summary>
public readonly record struct SkyDepth(GpuTexture NearAtlas, GpuTexture FarAtlas);

/// <summary>
/// The sun's reflective shadow map of one view: an orthographic view from the sun around the camera in
/// <see cref="Views"/>, one row, and its targets, which hold for each texel the first surface that the sun lights there.
/// </summary>
/// <remarks>
/// The shadow gem makes it for each view. A geometry gem draws the depth of the surfaces into <see cref="Depth"/>,
/// reversed, their diffuse colour into <see cref="Albedo"/> and their normal, as 0.5 + 0.5 times the unit normal, into
/// <see cref="Normal"/>, and gives them back as <see cref="BounceMaps"/> (Dachsbacher and Stamminger, "Reflective
/// Shadow Maps", 2005). Positions are relative to the view's camera.
/// </remarks>
public readonly record struct BounceViews(
    GpuBuffer Views,
    GpuTexture Depth,
    GpuTexture Albedo,
    GpuTexture Normal,
    Matrix4x4 ClipToRelative
);

/// <summary>
/// The targets of <see cref="BounceViews"/> with the surfaces that the sun lights drawn into them.
/// </summary>
public readonly record struct BounceMaps(GpuTexture Depth, GpuTexture Albedo, GpuTexture Normal);
