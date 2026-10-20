using System.Collections.ObjectModel;
using System.ComponentModel;
using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.PipelineLoader;

/// <summary>
/// What the machine overrides of every pipeline: set in the project's <c>Presets/Default.preset</c>, by another
/// preset, or with <c>--set PipelineLoader.off=["AutoExposure"]</c>. Changed while running, every pipeline is made
/// again.
/// </summary>
[Category("Render/Pipeline")]
internal sealed class PipelineSettings : ISettings
{
    /// <summary>
    /// Passes and pipelines this machine leaves out wherever they are listed, by their file name without its extension:
    /// <c>["AutoExposure"]</c>.
    /// </summary>
    public IReadOnlyList<string> Off { get; set; } = [];

    /// <summary>
    /// Pass params that win over every pipeline's and the pass's own, by the pass's file name, then the param's:
    /// <c>{ "Tonemap": { "exposureBias": { "x": -0.5 } } }</c>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, MaterialParam>> Params { get; set; } = ReadOnlyDictionary<string, IReadOnlyDictionary<string, MaterialParam>>.Empty;
}
