namespace Magic.Contexts.Components;

/// <summary>
/// The marker under <see cref="Tag.Static"/>: on every entity whose <see cref="Tags"/> hold it, kept there by the tag
/// system and put on with the tags when a chunk spawns. A query asks for it (<c>With&lt;Static&gt;()</c>) or leaves it
/// out, which the tag inside the <see cref="Tags"/> container cannot do: what never moves is then never walked. Not
/// something to add by hand: tag the entity.
/// </summary>
internal struct Static;
