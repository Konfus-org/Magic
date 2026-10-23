using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// Which of a view's two cullings a frame runs (<see cref="IRendering.Cull"/>).
/// </summary>
public enum CullPhase : byte
{
    /// <summary>
    /// What the depth pyramid the view last built says is in sight: as it was seen last frame.
    /// </summary>
    First,

    /// <summary>
    /// What the first hid, tested against the depth pyramid built since, of what the first drew.
    /// </summary>
    Second,
}
