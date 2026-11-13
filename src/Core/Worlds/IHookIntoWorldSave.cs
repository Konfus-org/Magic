namespace Magic.Worlds;

/// <summary>
/// A gem that holds state of the world's entities outside their components, and saves it into them before the world
/// saves its entities.
/// </summary>
/// <remarks>
/// The world saves its entities to a save file, and before a domain that changed opens again. The hook runs on the
/// game thread just before each, so what it writes is in the save file and in the entities that come again. The saved
/// state comes back in the components with the entities. A gem whose state is in the components already does not need
/// the hook.
/// </remarks>
public interface IHookIntoWorldSave
{
    /// <summary>
    /// Writes what the gem holds of the world's entities into their components, so the save that follows keeps it.
    /// </summary>
    void OnWorldSaving();
}
