using Magic.Services;

namespace Magic.Contexts.ECS;

/// <summary>
/// A domain began, or finished, opening in place of the world (<see cref="World.IsLoading"/>): told as the open
/// domains close and the loading domain is shown, and again as the new one is there, or is not coming. A message on
/// <see cref="Events"/>, heard the frame after, for a gem the world is built on, which cannot take the world: the gem
/// that runs scripts holds those not <see cref="Attributes.ImpatientAttribute"/> back while it is loading.
/// </summary>
public readonly record struct WorldLoading(bool IsLoading);
