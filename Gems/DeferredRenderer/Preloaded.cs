using Magic.Contexts.Assets;

namespace DeferredRendererGem;

/// <summary>
/// What one load ahead of the render thread brought: an asset with everything of it the render tables will ask
/// for, by id; null for one that could not be loaded. Filled by the load, on workers, and read by the render
/// thread once it is done.
/// </summary>
internal sealed class Preloaded
{
    public Dictionary<ulong, Asset?> Assets { get; } = [];

    /// <summary>
    /// The lesser versions of the models in it, by the model's id, the highest threshold first.
    /// </summary>
    public Dictionary<ulong, Lods> Lods { get; } = [];

    /// <summary>
    /// The frame it arrived in.
    /// </summary>
    public long Frame { get; set; }
}
