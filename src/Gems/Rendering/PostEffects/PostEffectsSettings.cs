using System.Collections.Immutable;
using System.ComponentModel;
using Magic.Configuration;

namespace Magic.PostEffects;

/// <summary>
/// The brightness of the picture and the post effects left out of it.
/// </summary>
[Category("Rendering")]
public sealed class PostEffectsSettings : ISettings
{
    /// <summary>
    /// How many stops brighter the picture is exposed, or darker below 0, when the world has no post processing of its
    /// own: the brightness.
    /// </summary>
    public float ExposureCompensation { get; set; }

    /// <summary>
    /// The post effects left out of every frame, by their material's file name, as Bloom Vignette.
    /// </summary>
    public ImmutableArray<string> DisabledEffects { get; set; } = [];
}
