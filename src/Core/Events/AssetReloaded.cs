namespace Magic.Events;

/// <summary>
/// An asset that had loaded was made again, as <paramref name="Asset"/>, since its file or one it is made from changed:
/// what reopens a prefab or domain placed in the world. One per asset, published as the load lands.
/// </summary>
public readonly record struct AssetReloaded(ulong Id, object Asset);
