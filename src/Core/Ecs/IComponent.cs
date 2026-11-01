using System.Diagnostics.CodeAnalysis;

namespace Magic.Ecs;

/// <summary>
/// Marks a struct as a component: plain unmanaged data an entity holds in the <see cref="IEcs"/>, as public fields,
/// with members for what follows from them. Make one with <c>new</c>, which gives its fields their defaults, rather
/// than <c>default</c>, which leaves every field zero.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "A marker the ECS's constraints name, which an attribute could not be."
)]
public interface IComponent;
