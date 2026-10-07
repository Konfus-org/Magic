using Magic.Contexts;

namespace Magic.Interfaces;

/// <summary>
/// Runs the scripts chunks attach to entities, whatever language they are written in: a language is a gem that loads
/// <see cref="Contexts.Assets.Script"/> assets. Whoever spawns an entity hands it each script entry its chunk lists.
/// </summary>
public interface IScripting
{
    /// <summary>
    /// Attaches the script asset <paramref name="id"/> to the entity, with its chunk entry (one JSON object: the id, and
    /// beside it the instance's values). The instance is made in the next run, once the world has loaded unless its
    /// class is marked <see cref="Attributes.Scripts.ImpatientAttribute"/>, and ends with the entity. An id no asset
    /// has is said once and left out.
    /// </summary>
    void Attach(Handle entity, ulong id, Unread entry);
}
