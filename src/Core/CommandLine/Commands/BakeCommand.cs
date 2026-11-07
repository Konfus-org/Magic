using System.Collections.Immutable;
using System.CommandLine;
using Magic.Assets;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>bake</c>: loads every asset, so what their importers and preprocessors make is cached; then queues the lines that
/// open every domain under <c>Domains/</c> in turn, wait for each to load and settle, so what drawing them makes is
/// cached too, and exit.
/// </summary>
internal static class BakeCommand
{
    public static Command Create(App app, AssetManager assets, CommandQueue queue)
    {
        Command bake = new(
            "bake",
            "Loads every asset, then opens every domain under Domains/ in turn, so their caches are made, then exits"
        );
        bake.SetAction(_ => Bake());

        return bake;

        void Bake()
        {
            if (!CommandLookups.IsProjectOpen(app, "bake"))
                return;

            Task baking = BakeAssetsAsync();
            baking.FireAndForget();
            queue.Hold(() => !baking.IsCompleted);
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

            Log.Info($"Baking every asset, then {domains} domains.");
            queue.Enqueue("exit");
        }

        async Task BakeAssetsAsync()
        {
            Result<ImmutableArray<ulong>> baked = await assets.BakeAsync(null, app.Lifetime).ConfigureAwait(false);
            if (baked.Ok)
                Log.Info($"Baked {baked.Value.Length} assets.");
            else
                Log.Error($"Not every asset baked: {baked.Error}");
        }
    }
}
