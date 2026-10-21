using Magic.Interfaces;
using Magic.Services;

namespace Magic.Contexts.Assets;

/// <summary>
/// An asset finished loading (<see cref="Services.Assets.LoadAsync{T}"/>), the first time or again after it was let go:
/// a message on <see cref="Events"/>, of the asset's type, so a gem watches only the types it uses. Which asset it is,
/// is its <see cref="IAsset{TSelf}.Handle"/>.
/// </summary>
public readonly record struct AssetLoaded<T>(T Asset)
    where T : IAsset<T>;
