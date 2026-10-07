using Magic.Contexts.Assets;

namespace Magic.Interfaces;

/// <summary>
/// The render pipeline's passes with their parameters as they run: exported by the renderer gem, listed by the
/// settings window. A parameter is changed as any setting is, by its <c>"PassName.param"</c> key through
/// <see cref="Services.Settings.Set"/>. Main thread only.
/// </summary>
public interface IPipelineTuning
{
    /// <summary>
    /// Every pass listed this frame, in the order they run, the post-processing last.
    /// </summary>
    IReadOnlyList<TunablePass> Passes { get; }
}
