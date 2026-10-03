namespace Magic.Contexts.Components;

/// <summary>
/// On a static entity whose world transform has been computed, put there by the transform system. Being a component
/// rather than a check moves settled statics into their own tables, so a query can leave them out
/// (<c>Without&lt;Settled&gt;()</c>) and what never moves costs nothing per frame, or ask for only them and read them
/// once. Setting the entity's <see cref="Tags"/> again takes it off until the transform is computed once more.
/// </summary>
public struct Settled;
