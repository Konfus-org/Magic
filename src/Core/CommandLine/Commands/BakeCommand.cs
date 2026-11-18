using System.Collections.Immutable;
using System.CommandLine;
using Magic.Assets;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>bake [path]</c>: loads one asset, each asset under a folder, or every asset, so that what the importers and
/// preprocessors make of them is cached.
/// </summary>
/// <remarks>
/// It holds the lines after it until the bake ends, so an <c>exit</c> after it leaves with full caches. It opens no
/// domain, so what only drawing makes, such as a compiled shader, is cached when a domain first draws.
/// </remarks>
internal static class BakeCommand
{
    public static Command Create(App app, AssetManager assets, CommandQueue queue)
    {
        Argument<string?> path = new("path")
        {
            Description =
                "An asset's path under its project, gem or Magic, as Materials/Carpet.mat, or a folder of them, as Materials; every asset when left out",
            Arity = ArgumentArity.ZeroOrOne,
        };
        Command bake = new(
            "bake",
            "Loads an asset, each asset under a folder, or every asset, so that what importers and preprocessors make is cached"
        )
        {
            path,
        };
        bake.SetAction(parsed => Bake(parsed.GetValue(path)));

        return bake;

        void Bake(string? what)
        {
            if (!CommandLookups.IsProjectOpen(app, "bake"))
                return;

            Task baking = BakeAsync(what);
            baking.FireAndForget();
            queue.Hold(() => !baking.IsCompleted);
        }

        async Task BakeAsync(string? what)
        {
            Task<Result<ImmutableArray<ulong>>> loading = what is null
                ? assets.BakeAsync(Terminal.Progress, app.Lifetime)
                : assets.BakeAsync(what, Terminal.Progress, app.Lifetime);
            Result<ImmutableArray<ulong>> baked = await loading.ConfigureAwait(false);
            if (baked.Ok)
                Log.Info($"Baked {baked.Value.Length} assets.");
            else
                Log.Error($"Not every asset baked: {baked.Error}");
        }
    }
}
