namespace Magic.Contexts.Components;

/// <summary>
/// The marker under <see cref="Tag.Hidden"/>: on every entity whose <see cref="Tags"/> hold it, kept there by the tag
/// system and put on with the tags when a chunk spawns; the streaming system also puts it on a chunk's root while
/// the chunk is made ready. It turns rendering off for the entity and everything under it: its
/// <see cref="Renderer"/>s are not drawn, and its lights and glows do not show. Nothing else changes: scripts run and
/// colliders collide. A hidden renderer is still registered, its model and materials loaded and uploaded, so taking
/// the tag away shows all of it in the next frame; that is also how a chunk's other level of detail is made ready
/// behind the one on screen. A query leaves hidden entities out with <c>WithoutAbove&lt;Hidden&gt;()</c>. Not
/// something to add by hand: tag the entity.
/// </summary>
public struct Hidden;
