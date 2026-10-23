using Magic.Services;

namespace Magic.Contexts.Assets;

/// <summary>
/// The project being run: what its .magic file holds. Plain data; <see cref="Engine"/> finds and reads it, and the
/// <see cref="App"/> holds it with the folder it is in.
/// </summary>
public sealed class Project
{
    public required string Name { get; init; }

    /// <summary>
    /// The domain the project opens with.
    /// </summary>
    public required Handle<Domain> EntryPoint { get; init; }

    /// <summary>
    /// The domain shown while another replaces the world (<see cref="World.OpenAsync"/>); none shows the engine's,
    /// <c>Domains/Loading/Loading.domain</c>.
    /// </summary>
    public Handle<Domain> LoadingDomain { get; set; }

    /// <summary>
    /// What is spawned at a domain's spawn when it opens in place of the world (<see cref="World.OpenAsync"/>): the
    /// player, whose camera is tagged <see cref="ECS.Tag.Player"/>. None spawns nothing.
    /// </summary>
    public Handle<Prefab> Player { get; set; }

    /// <summary>
    /// The gems to run, by name, each with the version it must be when that matters: <c>Name</c> runs whatever
    /// version of that gem is there, <c>Name@1.2</c> only a 1.2.x, and a gem of another version is not made. Where
    /// dependencies do not decide, gems are made in this order.
    /// </summary>
    public IReadOnlyList<string> Gems { get; set; } = [];
}
