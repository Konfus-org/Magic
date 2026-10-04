using System.Numerics;

namespace Magic.Contexts.Components;

/// <summary>
/// What the sky looks like: for now its linear colour, the light that reaches a surface from above where nothing is in
/// the way, and what the GI sees through a cave's mouth. One per world, on an entity of its own like
/// <see cref="PostProcessing"/>; of several the first is followed, warned about once. Without one the sky is a dim
/// blue-grey. In a chunk: <c>{ "name": "Sky", "components": { "Sky": { "color": { "x": 0.4, "y": 0.55, "z": 0.85 } } } }</c>.
/// </summary>
public struct Sky : IComponent
{
    public Vector3 Color { get; set; }
}
