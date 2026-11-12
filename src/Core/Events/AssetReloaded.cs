namespace Magic.Events;

/// <summary>
/// Tells that a loaded asset was loaded again because its file, or a file it is made from, changed.
/// </summary>
/// <remarks>
/// <paramref name="Asset"/> is the new asset. One message is published for each asset, when its load completes.
/// A listener can replace what it made from the old asset, such as a prefab or a domain in the world.
/// </remarks>
public record struct AssetReloaded(ulong Id, object Asset) : IEvent
{
    public bool IsHandled { get; set; }
}
