using System.Numerics;
using Magic.Contexts.Assets;

namespace Magic.AssimpModelLoader;

/// <summary>
/// What a model's <c>.meta</c> holds beside its id: how its file is made into a <see cref="Model"/>,
/// <c>{ "origin": { "x": -1, "y": 0, "z": 0 } }</c>.
/// </summary>
internal sealed class ModelMeta
{
    /// <summary>
    /// The point of the model, in the engine's frame and metres, moved to (0, 0, 0): what sits at its entity's
    /// position, and what scale and rotation turn about.
    /// </summary>
    public Vector3 Origin { get; set; }
}
