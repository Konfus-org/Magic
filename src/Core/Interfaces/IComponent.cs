using System.Diagnostics.CodeAnalysis;

namespace Magic.Interfaces;

/// <summary>
/// Marks a struct as a component: plain unmanaged data an entity holds in the <see cref="IEcs"/>, whose type parameters
/// ask for it, and what a file names by its type's name (<see cref="Services.Assemblies.TryGet{T}"/>), found in the
/// code loaded, Core's, the project's and each gem's; so declaring it is all a gem or a game does.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "A marker the ECS's constraints name, which an attribute could not be.")]
public interface IComponent;
