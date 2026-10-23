using System.Text.Json;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Contexts.ECS;

/// <summary>
/// The world as data (<see cref="World.SaveAsync"/>), which outlives the run that made it: the domains open, every
/// entity with its components and its scripts, and whether the world is paused and how fast its time runs, which
/// <see cref="World.Restore"/> puts back. The engine carries one
/// across a restart (<see cref="App.Restart"/>). It names a component by its type's name and a script by its
/// asset, and holds their values as JSON, so it keeps nothing of the code that made it: it restores into the code
/// loaded then, as a file read would, which is what lets a restart load code again.
/// </summary>
public sealed record WorldState
{
    /// <summary>
    /// The domains open, in the order they were opened.
    /// </summary>
    public required IReadOnlyList<DomainState> Domains { get; init; }

    public required IReadOnlyList<EntityState> Entities { get; init; }

    /// <summary>
    /// <see cref="World.IsPaused"/>.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// <see cref="World.TimeScale"/>.
    /// </summary>
    public float TimeScale { get; set; } = 1;
}

/// <summary>
/// A domain open in the saved world: which, how it was opened, the entities each of its prefabs spawned and the
/// player's, each by its <see cref="EntityState.Id"/>, so that put back, closing it and a prefab read again still find
/// what is theirs. One that had spawned nothing yet, as one still opening, has neither, and opens again from the
/// start.
/// </summary>
public sealed record DomainState(
    Handle<Domain> Domain,
    DomainOpenMode Mode,
    IReadOnlyDictionary<Handle<Prefab>, IReadOnlyList<int>> Prefabs,
    IReadOnlyList<int> Player);

/// <summary>
/// An entity of the saved world: its id, its own within the save and what a component or a script refers to it by
/// (an <see cref="Entity"/> as JSON is its id), its components and its scripts.
/// </summary>
public sealed record EntityState(int Id, IReadOnlyList<ComponentState> Components, IReadOnlyList<ScriptState> Scripts);

/// <summary>
/// A component on an entity of the saved world: which, by its type's name (<see cref="Assemblies.TryGet{T}"/>), and its
/// fields as JSON.
/// </summary>
public sealed record ComponentState(string Type, JsonElement Values);

/// <summary>
/// A script on an entity of the saved world: which, and its values as JSON by name, as the gem that runs scripts
/// saved them (<see cref="IScripts.Save"/>).
/// </summary>
public sealed record ScriptState(Handle<Script> Script, IReadOnlyDictionary<string, JsonElement> Values);
