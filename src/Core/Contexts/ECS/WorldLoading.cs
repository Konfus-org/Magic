using Magic.Services;

namespace Magic.Contexts.ECS;

/// <summary>
/// A domain began, or finished, opening in place of the world, or a saved world being put back in its place
/// (<see cref="World.IsLoading"/>): told as the open domains close and the loading domain is shown, or as the saved
/// world is put back, and again the frame after the new one is first shown, or once it is not coming, so the frame
/// that shows it draws it as it loaded. A message on <see cref="Events"/>, heard the frame it is told, for a gem the
/// world is built on, which cannot take the world, and for what runs the world: the gem that runs scripts holds those
/// not <see cref="Attributes.ImpatientAttribute"/> back while it is loading, and a gem that readied the new one
/// (<see cref="Interfaces.IPreloader.ReadyAsync"/>) holds what it runs, steps or plays until the loading is over.
/// </summary>
public readonly record struct WorldLoading(bool IsLoading);
