namespace Magic.Contexts.Components;

/// <summary>
/// The renderer's bookkeeping for a drawn entity: the handle <see cref="Interfaces.IRendering.Add"/> gave its
/// <see cref="Renderer"/> and whether it was registered as static. Written by
/// the host's render system, never by a scene. It lives on the entity rather than in a dictionary because
/// the per-frame sweep over moved instances is then one contiguous column read rather than a hash probe per
/// entity (perf-driven, flagged).
/// </summary>
public struct RenderInstance
{
    public uint Handle;

    public bool Static;
}
