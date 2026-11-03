using System.Collections.Immutable;

namespace Magic.Ecs;

/// <summary>
/// One entity to make: its components, each of its own type.
/// </summary>
public readonly record struct EntityTemplate(ImmutableArray<IComponent> Components);
