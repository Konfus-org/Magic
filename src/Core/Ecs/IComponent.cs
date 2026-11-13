using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Magic.Ecs;

/// <summary>
/// Marks a struct as a component: plain unmanaged data in public fields, which an entity holds in the
/// <see cref="IEcs"/>.
/// </summary>
/// <remarks>
/// Other members give values that follow from the fields. Make a component with <c>new</c>, which gives the fields
/// their defaults. Do not use <c>default</c>, which sets every field to zero.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "A marker the ECS's constraints name, which an attribute could not be."
)]
[JsonConverter(typeof(ComponentJsonConverter))]
public interface IComponent;
