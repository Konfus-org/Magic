using Magic.Contexts;
using System.Text.Json;

namespace Magic.Interfaces;

/// <summary>
/// Runs the scripts of one language. Whoever spawns an entity offers each script entry its chunk lists for it (one
/// JSON object: the <c>id</c> of its <see cref="Contexts.Assets.Script"/> asset, and beside it the instance's values)
/// to every provider in turn, until one takes it. Several languages load side by side, each taking its own.
/// </summary>
public interface IScripting
{
    /// <summary>
    /// Takes the entry when its script asset is one this runs, and answers what ends the instance; null when it is
    /// not. The instance is made in the provider's next run, once the world has loaded unless its class is marked
    /// <see cref="Attributes.RunOnLoadingAttribute"/>. Destroying the entity ends its scripts on its own; disposing
    /// the answer after that does nothing.
    /// </summary>
    IDisposable? Attach(Handle entity, JsonElement script);
}
