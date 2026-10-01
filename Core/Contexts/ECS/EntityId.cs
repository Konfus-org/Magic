namespace Magic.Contexts.Components;

/// <summary>
/// The id an entity has in its chunk file, stable across loads and unlike a <see cref="Handle"/>, which is
/// whatever the ECS handed out this time. For references between entities and for saving; entities without
/// an id in the file do not get one.
/// </summary>
public struct EntityId : IComponent
{
    public ulong Value { get; set; }
}
