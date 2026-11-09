using System.Collections.Immutable;

namespace Magic.Graphics;

/// <summary>
/// The world as the GPU holds it this frame, and the view it is seen through: what the gem that keeps the world on the
/// GPU makes each frame for the gems that draw it. The frame's buffer holds the library's <c>Core/Frame</c>, then its
/// models', lights' and shadows' rows; every target is at the picture's size.
/// </summary>
public readonly record struct GpuView
{
    public required GpuBuffer Frame { get; init; }

    /// <summary>
    /// The library's <c>Light/Indirect</c>, which whatever traces the indirect light writes for a frame.
    /// </summary>
    public required GpuBuffer IndirectLight { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

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
    /// The nearest glass's depth and surface, in front of the opaque surfaces.
    /// </summary>
    public required GpuTexture GlassDepth { get; init; }

    public required GpuTexture GlassBase { get; init; }

    public required GpuTexture GlassNormal { get; init; }

    public required GpuTexture GlassSurface { get; init; }

    /// <summary>
    /// The lit picture, and the output the window and screenshots take.
    /// </summary>
    public required GpuTexture Hdr { get; init; }

    public required GpuTexture Output { get; init; }

    /// <summary>
    /// The instance rows drawn this frame and the material rows they name.
    /// </summary>
    public required GpuBuffer Instances { get; init; }

    public required int InstanceCount { get; init; }

    /// <summary>
    /// The most instances the table holds.
    /// </summary>
    public required int InstanceCapacity { get; init; }

    public required GpuBuffer Materials { get; init; }

    /// <summary>
    /// The pools the models are in: vertices, indices, the mesh table rays read, and each model's clusters and impostor.
    /// </summary>
    public required GpuBuffer Vertices { get; init; }

    public required GpuBuffer Indices { get; init; }

    public required GpuBuffer Meshes { get; init; }

    public required GpuBuffer ClusterPool { get; init; }

    public required GpuBuffer ClusterModels { get; init; }

    public required GpuBuffer ImpostorTable { get; init; }

    /// <summary>
    /// Each material shader a surface is drawn with, by the number its material rows name it by, and every texture in
    /// the heap, which a pass that evaluates materials reads.
    /// </summary>
    public required IReadOnlyList<ShaderTypeArgument> MaterialShaders { get; init; }

    public required ImmutableArray<GpuResource> HeapTextures { get; init; }

    /// <summary>
    /// Each material row as a ray hit reads it; none on a GPU that doesn't trace rays.
    /// </summary>
    public required GpuBuffer HitMaterials { get; init; }

    /// <summary>
    /// The local lights, each tile's lights and the sky's tables.
    /// </summary>
    public required GpuBuffer LightTable { get; init; }

    public required GpuBuffer LightTiles { get; init; }

    public required int LightCount { get; init; }

    public required GpuTexture SkyTransmittance { get; init; }

    public required GpuTexture SkyMultiScattering { get; init; }

    public required GpuTexture SkyView { get; init; }

    /// <summary>
    /// The effects run over the lit picture this frame, by their place in the air's list and then the picture's.
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
}
