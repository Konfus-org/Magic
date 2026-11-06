using System.CommandLine;
using Magic.Debugging;
using Magic.Files;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>bake</c>: queues the lines that open every domain under <c>Domains/</c> in turn, wait for each to load and settle,
/// so their caches are made, and exit.
/// </summary>
internal static class BakeCommand
{
    public static Command Create(App app, Assets assets, CommandQueue queue)
    {
        Command bake = new("bake", "Opens every domain under Domains/ in turn, so their caches are made, then exits");
        bake.SetAction(_ => Bake());

        return bake;

        void Bake()
        {
            if (!CommandLookups.IsProjectOpen(app, "bake"))
                return;

            int domains = 0;
            foreach (string path in assets.GetPathsUnder("Domains"))
            {
                if (!path.EndsWith(".domain", StringComparison.OrdinalIgnoreCase))
                    continue;

                domains++;
                queue.Enqueue($"open {path}");
                queue.Enqueue("wait loaded");
                queue.Enqueue("wait 120");
            }

            Log.Info($"Baking {domains} domains.");
            queue.Enqueue("exit");
        }
    }
}
