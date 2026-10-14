using Magic.Interfaces;
using Magic.Services;

namespace Magic.Contexts.Assets;

/// <summary>
/// A loaded asset's file or <c>.meta</c> sidecar changed on disk, and it was loaded again: <see cref="Asset"/> is what
/// it is now, with the same <see cref="IAsset{TSelf}.Handle"/>, and whoever holds the old object takes it in its place.
/// A message on <see cref="Events"/>, of the asset's type. One that no longer loads is logged instead, and the old
/// object stays what it is.
/// </summary>
public readonly record struct AssetReloaded<T>(T Asset)
    where T : IAsset<T>;
