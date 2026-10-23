using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Magic.Interfaces;

namespace Magic.Internal;

/// <summary>
/// The engine's own settings, under <c>Core</c>: set in the project's <c>Presets/Default.preset</c>, by another preset,
/// or with <c>--set Core.fixedRate=120</c>.
/// </summary>
[Category("Core")]
internal sealed class CoreSettings : ISettings
{
    /// <summary>
    /// Fixed steps a second: each FixedUpdate moves the world on by one over this, its frame's DeltaSeconds.
    /// </summary>
    [Range(1, 1000)]
    public int FixedRate { get; set; } = 60;

    /// <summary>
    /// The most fixed steps one frame runs to catch up after a slow one: a frame further behind drops the rest, so the
    /// world loses that time rather than the frame growing to make it up and the next falling further behind. More
    /// keeps the world on time through longer hitches and fast time scales, at more cost in a slow frame; at 60 steps a
    /// second, 4 is 67 ms of catch-up.
    /// </summary>
    [Range(1, 64)]
    public int MaxFixedSteps { get; set; } = 4;
}
