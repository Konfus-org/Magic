namespace Magic.Worlds;

/// <summary>
/// A gem that holds state of the world's entities outside their components, and writes it into them for a save.
/// </summary>
/// <remarks>
/// The hook runs on the game thread, just before a save takes the world, and before a domain that changed opens
/// again, so what it writes is in the save and in the entities that come again. A gem whose state is in the
/// components already does not need it.
/// </remarks>
public interface IHookIntoWorldSave
{
    /// <summary>
    /// Writes what the gem holds of the world's entities into their components, so the save that follows keeps it.
    /// </summary>
    void OnWorldSaving();
}
