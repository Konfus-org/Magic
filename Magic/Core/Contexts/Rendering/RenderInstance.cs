namespace Magic.Contexts.Components;

/// <summary>
/// The render system's bookkeeping for a drawn entity: the instance handle it gave its <see cref="Renderer"/> and
/// whether it was registered as static. Written by the render system, never by a scene. It lives on the entity rather than in a dictionary so
/// the per-frame sweep over moved instances reads one contiguous column instead of probing a hash per entity.
/// </summary>
public struct RenderInstance
{
    public uint Handle;

    public bool Static;
}
