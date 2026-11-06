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
internal static class TeleportCommand
{
    private const string Usage = "teleport takes x y z, a domain, or a domain then x y z";

    public static Command Create(App app, World world, Assets assets, CommandQueue queue)
    {
        Argument<string[]> where = new("where")
        {
            Description = "x y z; or a domain's path or name, then x y z if wanted",
            Arity = new ArgumentArity(1, 4),
        };
        Command teleport = new(
            "teleport",
            "Moves the player to x y z; or opens a domain in place of the world, with the player at its spawn or at x y z"
        )
        {
            where,
        };
        teleport.SetAction(parsed => Teleport(parsed.GetRequiredValue(where)));

        return teleport;

        // Moves the player with three words; with a domain first, queues its opening, then, with x y z after it, a wait
        // for it to load and the move there.
        void Teleport(string[] words)
        {
            if (!CommandLookups.IsProjectOpen(app, "teleport in"))
                return;

            if (words.Length == 3)
            {
                MovePlayer(words);

                return;
            }

            if (words.Length == 2)
            {
                Log.Error($"{Usage}, not two words.");

                return;
            }

            Handle<Domain> domain = CommandLookups.FindAsset<Domain>(assets, words[0]);
            if (assets.GetPath(domain) is not { } path)
            {
                Log.Error($"There is no domain at or named {words[0]}.");

                return;
            }

            Result<Vector3> position =
                words.Length == 4 ? CommandLookups.ParsePosition(words.AsSpan(1)) : Result.Success(Vector3.Zero);
            if (!position.Ok)
            {
                Log.Error($"{Usage}: {position.Error}");

                return;
            }

            queue.Enqueue($"open \"{path}\"");
            if (words.Length != 4)
                return;

            queue.Enqueue("wait loaded");
            queue.Enqueue($"teleport {string.Join(' ', words[1..])}");
        }

        // Moves every entity tagged player by how far the first is from the x y z given, so they keep their places
        // around it.
        void MovePlayer(string[] to)
        {
            Result<Vector3> position = CommandLookups.ParsePosition(to);
            if (!position.Ok)
            {
                Log.Error($"{Usage}: {position.Error}");

                return;
            }

            if (CommandLookups.FindPlayer(world) is not { } player)
            {
                Log.Error("There is no entity tagged player with a transform to teleport.");

                return;
            }

            Vector3 by = position.Value - player.Position;
            foreach (EcsChunk<Tags, Transform> chunk in world.Query<Tags, Transform>())
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
}
