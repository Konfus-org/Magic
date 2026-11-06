using System.Collections.Immutable;
using System.CommandLine;
using System.Linq;
using System.Numerics;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Files.Types;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>spawn</c>: puts a prefab, by its path or name, in the world at x y z, or a few metres in front of the player,
/// holding <c>wait loaded</c> while it comes, and logs the entities it made.
/// </summary>
internal sealed class SpawnCommand : Command
{
    /// <summary>
    /// How far in front of the player a prefab spawned without a place goes.
    /// </summary>
    private const float SpawnAheadMetres = 3;

    private readonly App _app;

    private readonly World _world;

    private readonly Assets _assets;

    private readonly CommandQueue _queue;

    public SpawnCommand(App app, World world, Assets assets, CommandQueue queue)
        : base("spawn", "Puts a prefab in the world at x y z, or a few metres in front of the player")
    {
        _app = app;
        _world = world;
        _assets = assets;
        _queue = queue;
        Argument<string> prefab = new("prefab") { Description = "The prefab's path or name" };
        Argument<string[]> at = new("at")
        {
            Description = "x y z; none for in front of the player",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Arguments.Add(prefab);
        Arguments.Add(at);
        Action = new CliCommand(parsed => Spawn(parsed.GetRequiredValue(prefab), parsed.GetValue(at) ?? []));
    }

    private void Spawn(string name, string[] at)
    {
        if (!CommandLookups.IsProjectOpen(_app, $"spawn {name} in"))
            return;

        Handle<Prefab> prefab = CommandLookups.FindAsset<Prefab>(_assets, name);
        if (prefab == Handle<Prefab>.None)
        {
            Log.Error($"There is no prefab at or named {name}.");

            return;
        }

        Result<Vector3> position =
            at.Length == 0 ? Result.Success(GetAheadOfPlayer()) : CommandLookups.ParsePosition(at);
        if (!position.Ok)
        {
            Log.Error($"spawn {name}: {position.Error}");

            return;
        }

        SpawnAsync(prefab, name, new Transform { Position = position.Value }).FireAndForget();
    }

    private async Task SpawnAsync(Handle<Prefab> prefab, string name, Transform at)
    {
        Result<ImmutableArray<Entity>> spawned = await _queue
            .ChangeWorldAsync(() => _world.SpawnAsync(prefab, at, null, CancellationToken.None))
            .ConfigureAwait(false);
        if (!spawned.Ok)
        {
            Log.Error($"{name} was not spawned: {spawned.Error}");

            return;
        }

        string ids = string.Join(", ", spawned.Value.Select(entity => entity.Id));
        Log.Info($"Spawned {name} at {at.Position} as entities {ids}.");
    }

    /// <summary>
    /// <see cref="SpawnAheadMetres"/> in front of the player, the way it faces; the origin when there is no player.
    /// </summary>
    private Vector3 GetAheadOfPlayer() =>
        CommandLookups.FindPlayer(_world) is { } player
            ? player.Position + (Vector3.Transform(Vector3.UnitZ, player.Rotation) * SpawnAheadMetres)
            : Vector3.Zero;
}
