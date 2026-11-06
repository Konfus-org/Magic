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
internal static class SpawnCommand
{
    /// <summary>
    /// How far in front of the player a prefab spawned without a place goes.
    /// </summary>
    private const float SpawnAheadMetres = 3;

    public static Command Create(App app, World world, Assets assets, CommandQueue queue)
    {
        Argument<string> prefab = new("prefab") { Description = "The prefab's path or name" };
        Argument<string[]> at = new("at")
        {
            Description = "x y z; none for in front of the player",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Command spawn = new("spawn", "Puts a prefab in the world at x y z, or a few metres in front of the player")
        {
            prefab,
            at,
        };
        spawn.SetAction(parsed => Spawn(parsed.GetRequiredValue(prefab), parsed.GetValue(at) ?? []));

        return spawn;

        void Spawn(string name, string[] where)
        {
            if (!CommandLookups.IsProjectOpen(app, $"spawn {name} in"))
                return;

            Handle<Prefab> found = CommandLookups.FindAsset<Prefab>(assets, name);
            if (found == Handle<Prefab>.None)
            {
                Log.Error($"There is no prefab at or named {name}.");

                return;
            }

            Result<Vector3> position =
                where.Length == 0 ? Result.Success(GetAheadOfPlayer()) : CommandLookups.ParsePosition(where);
            if (!position.Ok)
            {
                Log.Error($"spawn {name}: {position.Error}");

                return;
            }

            SpawnAsync(found, name, new Transform { Position = position.Value }).FireAndForget();
        }

        async Task SpawnAsync(Handle<Prefab> found, string name, Transform place)
        {
            Result<ImmutableArray<Entity>> spawned = await queue
                .ChangeWorldAsync(() => world.SpawnAsync(found, place, null, CancellationToken.None))
                .ConfigureAwait(false);
            if (!spawned.Ok)
            {
                Log.Error($"{name} was not spawned: {spawned.Error}");

                return;
            }

            string ids = string.Join(", ", spawned.Value.Select(entity => entity.Id));
            Log.Info($"Spawned {name} at {place.Position} as entities {ids}.");
        }

        // SpawnAheadMetres in front of the player, the way it faces; the origin when there is no player.
        Vector3 GetAheadOfPlayer() =>
            CommandLookups.FindPlayer(world) is { } player
                ? player.Position + (Vector3.Transform(Vector3.UnitZ, player.Rotation) * SpawnAheadMetres)
                : Vector3.Zero;
    }
}
