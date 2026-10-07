namespace Magic.Contexts.Domain;

/// <summary>
/// An open domain is there: its globals and the chunks its cameras wanted are spawned.
/// </summary>
public readonly record struct DomainLoaded(ulong Domain);
