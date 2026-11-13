using System.Collections.Immutable;
using System.Drawing;
using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.DeferredRendering.World;

/// <summary>
/// The world as the GPU holds it this frame, whatever view it is seen through: what the world's step makes once a frame
/// for the view's step and for the gems that keep something for the whole world. Its buffer is laid out as a view's
/// frame buffer is, and holds the models' row alone.
/// </summary>
public readonly record struct GpuWorld
{
    public required GpuBuffer Frame { get; init; }

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
    /// The failure table: a row for each failure drawn, which the material rows name.
    /// </summary>
    public required GpuBuffer Failures { get; init; }

    /// <summary>
    /// The pools the models are in: vertices, indices and the mesh table rays read.
    /// </summary>
    public required GpuBuffer Vertices { get; init; }

    public required GpuBuffer Indices { get; init; }

    public required GpuBuffer Meshes { get; init; }

    /// <summary>
    /// Each model on the GPU by its model index, as <see cref="GpuView.Models"/> has them.
    /// </summary>
    public required ReadOnlyMemory<ModelPlace> Models { get; init; }

    /// <summary>
    /// Each material shader a surface is drawn with, by the number its material rows name it by, and every texture in
    /// the heap, which a pass that evaluates materials reads.
    /// </summary>
    public required IReadOnlyList<ShaderTypeArgument> MaterialShaders { get; init; }

    public required ImmutableArray<GpuResource> HeapTextures { get; init; }

    /// <summary>
    /// How many domains that loaded have had what they draw put on the GPU, as <see cref="GpuView.PreparedDomains"/>
    /// counts them.
    /// </summary>
    public required int PreparedDomains { get; init; }

    /// <summary>
    /// The models' row of a frame buffer: the pools and tables the instances are drawn from.
    /// </summary>
    internal GpuModels ModelsRow =>
        new()
        {
            Instances = Instances.Id,
            Count = (uint)InstanceCount,
            Vertices = Vertices.Id,
            Indices = Indices.Id,
            Materials = Materials.Id,
            Meshes = Meshes.Id,
            Failures = Failures.Id,
        };

    /// <summary>
    /// What the frame draws, as the game thread published it: each view's camera, the sun and the sky.
    /// </summary>
    internal RenderInputs? Inputs { get; init; }

    /// <summary>
    /// The size of the whole finished picture, which the views of the window share by their sections.
    /// </summary>
    internal Size Picture { get; init; }

    internal LightBuffers Lights { get; init; }

    /// <summary>
    /// The effects run over each view's lit picture, and their material rows.
    /// </summary>
    internal ReadOnlyMemory<GpuEffect> Effects { get; init; }

    internal GpuBuffer EffectRows { get; init; }

    /// <summary>
    /// The screenshot asked for in this frame; none in most.
    /// </summary>
    internal Screenshot? Screenshot { get; init; }
}

/// <summary>
/// A screenshot to take: where it is written, the whole picture each view of the window is copied into, and the buffer
/// it is read back into.
/// </summary>
internal readonly record struct Screenshot(string Path, GpuTexture Copy, GpuBuffer Readback);
