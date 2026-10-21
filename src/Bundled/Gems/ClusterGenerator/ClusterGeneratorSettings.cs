using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Magic.Interfaces;

namespace Magic.ClusterGenerator;

/// <summary>
/// Where a model's cards start: set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set ClusterGenerator.cardHeight=0.05</c>, which draws cards nearer.
/// </summary>
[Category("Render/Clusters")]
internal sealed class ClusterGeneratorSettings : ISettings
{
    /// <summary>
    /// The model's height on screen, as a share of the view's, under which its cards are drawn in place of its meshes,
    /// never before its last level. Read as each model's clusters and cards are made, and part of what they are kept in
    /// the cache by. Changed while running, every model is made again.
    /// </summary>
    [Range(0.001, 0.5)]
    public float CardHeight { get; set; } = 0.03f;
}
