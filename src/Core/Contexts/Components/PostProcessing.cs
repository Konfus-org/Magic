using Magic.Contexts.Rendering;
using Magic.Interfaces;

namespace Magic.Contexts.Components;

/// <summary>
/// How a world's image is finished: the post effects run on each view's lit image, in order. A world has one, on an
/// entity of its own; without one, its frame is shown as it is lit. Exposed for the light where the camera is, then
/// tone mapped:
/// <code>
/// { "effects": [
///     { "type": "Exposure", "shader": { "id": 7101 } },
///     { "type": "Tonemap", "shader": { "id": 7102 } } ] }
/// </code>
/// A script changes an effect as it runs:
/// <c>if (post.Effects.TryGet(out Grade grade)) post.Effects.Set(grade with { Saturation = 0 });</c>.
/// </summary>
public record struct PostProcessing : IComponent
{
    public RenderEffects<IPostEffect> Effects;
}
