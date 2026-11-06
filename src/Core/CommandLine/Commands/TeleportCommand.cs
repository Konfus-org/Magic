using System.CommandLine;
using System.Numerics;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Files.Types;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>teleport</c>: moves the player to x y z; or, with a domain first, opens it in place of the world, which puts the
/// player at its spawn, then moves the player to the x y z after it, if any, once it has loaded.
/// </summary>
internal sealed class TeleportCommand : Command
{
    private const string Usage = "teleport takes x y z, a domain, or a domain then x y z";

    private readonly App _app;

    private readonly World _world;

    private readonly Assets _assets;

    private readonly CommandQueue _queue;

    public TeleportCommand(App app, World world, Assets assets, CommandQueue queue)
        : base(
            "teleport",
            "Moves the player to x y z; or opens a domain in place of the world, with the player at its spawn or at x y z"
        )
    {
        _app = app;
        _world = world;
        _assets = assets;
        _queue = queue;
        Argument<string[]> where = new("where")
        {
            Description = "x y z; or a domain's path or name, then x y z if wanted",
            Arity = new ArgumentArity(1, 4),
        };
        Arguments.Add(where);
        Action = new CliCommand(parsed => Teleport(parsed.GetRequiredValue(where)));
    }

    /// <summary>
    /// Moves the player with three words; with a domain first, queues its opening, then, with x y z after it, a wait for
    /// it to load and the move there.
    /// </summary>
    private void Teleport(string[] where)
    {
        if (!CommandLookups.IsProjectOpen(_app, "teleport in"))
            return;

        if (where.Length == 3)
        {
            MovePlayer(where);

            return;
        }

        if (where.Length == 2)
        {
            Log.Error($"{Usage}, not two words.");

            return;
        }

        Handle<Domain> domain = CommandLookups.FindAsset<Domain>(_assets, where[0]);
        if (_assets.GetPath(domain) is not { } path)
        {
            Log.Error($"There is no domain at or named {where[0]}.");

            return;
        }

        Result<Vector3> position =
            where.Length == 4 ? CommandLookups.ParsePosition(where.AsSpan(1)) : Result.Success(Vector3.Zero);
        if (!position.Ok)
        {
            Log.Error($"{Usage}: {position.Error}");

            return;
        }

        _queue.Enqueue($"open \"{path}\"");
        if (where.Length != 4)
            return;

        _queue.Enqueue("wait loaded");
        _queue.Enqueue($"teleport {string.Join(' ', where[1..])}");
    }

    /// <summary>
    /// Moves every entity tagged player by how far the first is from the x y z of <paramref name="to"/>, so they keep
    /// their places around it.
    /// </summary>
    private void MovePlayer(string[] to)
    {
        Result<Vector3> position = CommandLookups.ParsePosition(to);
        if (!position.Ok)
        {
            Log.Error($"{Usage}: {position.Error}");

            return;
        }

        if (CommandLookups.FindPlayer(_world) is not { } player)
        {
            Log.Error("There is no entity tagged player with a transform to teleport.");

            return;
        }

        Vector3 by = position.Value - player.Position;
        foreach (EcsChunk<Tags, Transform> chunk in _world.Query<Tags, Transform>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                if (chunk.First[i].Has(Tag.Player))
                    chunk.Second[i].Position += by;
            }
        }

        Log.Info($"Teleported the player to {position.Value}.");
    }
}
