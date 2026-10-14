using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Magic.ModelLodGenerator;

/// <summary>
/// Where a model's cards start: set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set ModelLodGenerator.cardHeight=0.05</c>, which draws cards nearer.
/// </summary>
internal sealed class ModelLodGeneratorSettings
{
    /// <summary>
    /// The model's height on screen, as a share of the view's, under which its cards are drawn in place of its meshes,
    /// never before its last level. Read as each model's levels are made, and part of what they are kept in the cache
    /// by, so changing it makes them again.
    /// </summary>
    [Browsable(false)]
    [Range(0.001, 0.5)]
    public float CardHeight { get; set; } = 0.03f;
}
