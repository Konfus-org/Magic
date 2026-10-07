namespace Magic.Contexts.Events;

/// <summary>
/// The game was asked to end; the frame loop stops after this frame.
/// </summary>
public readonly record struct Quit;

/// <summary>
/// A gem was loaded, reloaded or unloaded, so the set of types in the process changed.
/// </summary>
public readonly record struct GemsChanged;
