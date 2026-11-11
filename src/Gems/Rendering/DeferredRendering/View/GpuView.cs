using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.DeferredRendering.World;
using Magic.Graphics;

namespace Magic.DeferredRendering.View;

/// <summary>
/// The world as the GPU holds it this frame, and the view it is seen through: what the view's step makes each frame for
/// the gems that draw it. The frame's buffer holds the library's <c>Core/Frame</c>, then the
/// rows of <c>FrameRows.cs</c>; every target is at the picture's size.
/// </summary>
public readonly record struct GpuView
{
    public required GpuBuffer Frame { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>
    /// The size the finished picture is shown at, which the output is upscaled or stretched to: the window's, or one
    /// set.
    /// </summary>
    public required int ShownWidth { get; init; }

    public required int ShownHeight { get; init; }

    /// <summary>
    /// Whether last frame drew this view at this size, in this world: what its targets hold is last frame's.
    /// </summary>
    public required bool HasHistory { get; init; }

    public required GpuTexture Depth { get; init; }

    public required GpuTexture GBufferNormal { get; init; }

    public required GpuTexture GBufferBase { get; init; }

    public required GpuTexture GBufferSurface { get; init; }

    public required GpuTexture Velocity { get; init; }

    /// <summary>
    /// The nearest glass's depth and surface, in front of the opaque surfaces; none while no material draws
    /// translucent.
    /// </summary>
    public required GpuTexture GlassDepth { get; init; }

    public required GpuTexture GlassBase { get; init; }

    public required GpuTexture GlassNormal { get; init; }

    public required GpuTexture GlassSurface { get; init; }

    /// <summary>
    /// The lit picture, and the output the window and screenshots take, or a gem upscales for them.
    /// </summary>
    public required GpuTexture Hdr { get; init; }

    public required GpuTexture Output { get; init; }

    /// <summary>
    /// The instance rows drawn this frame and the material rows they name.
    /// </summary>
    public required GpuBuffer Instances { get; init; }

    public required int InstanceCount { get; init; }

    /// <summary>
    /// How many instances the table holds this frame; it grows as the world does, so what is sized by it is asked for
    /// again each frame.
    /// </summary>
    public required int InstanceCapacity { get; init; }

    public required GpuBuffer Materials { get; init; }

    /// <summary>
    /// The pools the models are in: vertices, indices and the mesh table rays read.
    /// </summary>
    public required GpuBuffer Vertices { get; init; }

    public required GpuBuffer Indices { get; init; }

    public required GpuBuffer Meshes { get; init; }

    /// <summary>
    /// Each model on the GPU by its model index, the one its instances name, up to the last row in use, none where no
    /// model is: what a gem that keeps a row for each model compares its own with each frame, dropping its rows past
    /// the end.
    /// </summary>
    public required ReadOnlyMemory<ModelPlace> Models { get; init; }

    /// <summary>
    /// Each material shader a surface is drawn with, by the number its material rows name it by, and every texture in
    /// the heap, which a pass that evaluates materials reads.
    /// </summary>
    public required IReadOnlyList<ShaderTypeArgument> MaterialShaders { get; init; }

    public required ImmutableArray<GpuResource> HeapTextures { get; init; }

    /// <summary>
    /// The local lights, each tile's lights and the sky's tables.
    /// </summary>
    public required GpuBuffer LightTable { get; init; }

    public required GpuBuffer LightTiles { get; init; }

    public required GpuTexture SkyTransmittance { get; init; }

    public required GpuTexture SkyMultiScattering { get; init; }

    public required GpuTexture SkyView { get; init; }

    /// <summary>
    /// The effects run over the lit picture this frame, by their place in the post processing's list.
    /// </summary>
    public required ReadOnlyMemory<GpuEffect> Effects { get; init; }

    /// <summary>
    /// Each effect's material row, by its place, packed as a surface material's is.
    /// </summary>
    public required GpuBuffer EffectRows { get; init; }

    /// <summary>
    /// How many domains that loaded have had what they draw put on the GPU: a gem that waits for its own work before a
    /// domain shows waits until this counts the domain it was told of.
    /// </summary>
    public required int PreparedDomains { get; init; }

    /// <summary>
    /// The atmosphere, for an entry generic over the sky it shades with.
    /// </summary>
    public static ShaderTypeArgument GetAtmosphere(AssetManager assets) =>
        new(assets.Find<Shader>("Shaders/Sky/Atmosphere.slang"), "Atmosphere");

    /// <summary>
    /// The view's size times <paramref name="scale"/>, each side rounded and kept between one pixel and the view's:
    /// the size of a pass drawn at a setting's fraction of the picture.
    /// </summary>
    public (int Width, int Height) Scaled(float scale) =>
        (
            Math.Clamp((int)MathF.Round(Width * scale), 1, Width),
            Math.Clamp((int)MathF.Round(Height * scale), 1, Height)
        );
}

/// <summary>
/// A model on the GPU: its asset, where its vertices are in the vertex pool, which its indices add, the box around it in
/// its own frame, and where its meshes start in the mesh table with each one rays see: where its indices start in the
/// index pool, its material slot and its triangles. It has no meshes on a GPU that traces no rays, or when the mesh
/// table had no room for them.
/// </summary>
public readonly record struct ModelPlace(
    Handle<Model> Model,
    uint VertexOffset,
    uint VertexCount,
    Vector3 BoundsMin,
    Vector3 BoundsMax,
    uint MeshOffset,
    ImmutableArray<(GpuMesh Mesh, uint Triangles)> Meshes
);
