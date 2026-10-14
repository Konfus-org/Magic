using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.ECS;

/// <summary>
/// How a world's image is finished: the effects a renderer adds to its frame, such as exposure, tonemapping or ambient
/// occlusion, as a <see cref="Pipeline"/> that lists them in order with the params this world gives them:
/// <c>{ "effects": { "id": 10360 } }</c>. A world has one, on an entity of its own; without one, its frame has no
/// effects. The machine still has the last word: a preset can turn an effect off, or make it cheaper.
/// </summary>
public record struct PostProcessing : IComponent
{
    public Handle<Pipeline> Effects;
}
