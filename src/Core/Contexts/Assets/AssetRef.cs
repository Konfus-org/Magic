namespace Magic.Contexts.Assets;

/// <summary>
/// An asset of any type, by its type and the id in its <c>.meta</c> sidecar: where a <see cref="Handle{T}"/> cannot say
/// which type, as among what an asset was made from (<see cref="Services.Assets.GetDependencies{T}"/>).
/// </summary>
public readonly record struct AssetRef(Type Type, ulong Id);
