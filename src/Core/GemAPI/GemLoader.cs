using System.Diagnostics.CodeAnalysis;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Pipelines;
using Magic.Worlds;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.GemAPI;

/// <summary>
/// Makes the gems a <see cref="GemRegistry"/> registered, each after the gems it needs, and sets them up; ends them
/// with <see cref="Unload"/>. A gem that can't be made is logged, and the others go on without it.
/// </summary>
internal static class GemLoader
{
    /// <summary>
    /// Makes the gems in <paramref name="registry"/> through <paramref name="provider"/>, which was built from the
    /// services they were registered with, and sets each up: on the log when it is a sink, reading for
    /// <paramref name="assets"/> the asset types it reads, and in the <paramref name="pipelines"/> it joins, so they run
    /// in the order the gems are made. The gems made, in dependency order. End them with
    /// <see cref="Unload"/>, which also frees the gems' assemblies once nothing holds the provider.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem's constructor throws, it is logged and the gem is not made; the others go on."
    )]
    public static IReadOnlyList<LoadedGem> Load(
        IServiceProvider provider,
        GemRegistry registry,
        AssetDatabase assets,
        PipelineCoordinator pipelines,
        World world
    )
    {
        List<LoadedGem> loaded = [];
        foreach (GemInfo gem in Sort(registry))
        {
            IGem made;
            try
            {
                made = (IGem)provider.GetRequiredService(gem.Type);
            }
            catch (Exception ex)
            {
                // A missing service, a gem it needs that wasn't made, or a gem that can't start says why in the message;
                // anything else is a bug in the gem, and its stack says where.
                string why = ex is InvalidOperationException ? ex.Message : ex.ToString();
                Log.Error($"Gem {gem.Name} was not made: {why}");
                continue;
            }

            if (made is IEcs ecs)
                world.Ecs = ecs;

            LoadedGem setUp = new(made, gem);
            loaded.Add(setUp);
            if (made is ILogSink sink)
                setUp.Subscriptions.Add(Log.Register(sink));

            if (gem.Reads.Count > 0)
                setUp.Subscriptions.Add(assets.AddLoader(made));

            if (gem.Joins.Count > 0)
                setUp.Subscriptions.Add(pipelines.Register(made, gem.Name, gem.Joins));

            string what = gem.Description.Length > 0 ? $": {gem.Description}" : string.Empty;
            Log.Info($"Gem {gem.Name} {gem.Version.ToString(3)} by {gem.Author} started{what}");
        }

        return loaded;
    }

    public static void Unload(IReadOnlyList<LoadedGem> gems)
    {
        // Last made, first ended, so no gem outlives one it was made after.
        for (int i = gems.Count - 1; i >= 0; i--)
        {
            Log.Info($"Unloading {gems[i].Name}.");
            gems[i].Dispose();
        }
    }

    /// <summary>
    /// The gems in <paramref name="registry"/>, each after the gems it needs. A gem in a cycle, or one that needs it, is
    /// logged and left out, since the provider hangs making a cycle.
    /// </summary>
    private static List<GemInfo> Sort(GemRegistry registry)
    {
        List<GemInfo> sorted = [];
        HashSet<string> placed = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> refused = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visiting = new(StringComparer.OrdinalIgnoreCase);
        foreach (GemInfo gem in registry.Gems)
        {
            Place(gem, registry, sorted, placed, refused, visiting);
        }

        return sorted;
    }

    /// <summary>
    /// Places <paramref name="gem"/> in <paramref name="sorted"/> after the gems it needs: whether it was.
    /// </summary>
    private static bool Place(
        GemInfo gem,
        GemRegistry registry,
        List<GemInfo> sorted,
        HashSet<string> placed,
        HashSet<string> refused,
        HashSet<string> visiting
    )
    {
        if (placed.Contains(gem.Name))
            return true;

        // A gem visited again before it is placed is in a cycle; the gem that needs it says so.
        if (refused.Contains(gem.Name) || !visiting.Add(gem.Name))
            return false;

        foreach (string name in gem.Needs)
        {
            if (registry.Find(name) is not { } need || Place(need, registry, sorted, placed, refused, visiting))
                continue;

            string why = visiting.Contains(name) ? "which needs it: a cycle" : "which was not made";
            Log.Error($"Gem {gem.Name} was not made: it needs {name}, {why}.");
            visiting.Remove(gem.Name);
            refused.Add(gem.Name);
            return false;
        }

        visiting.Remove(gem.Name);
        placed.Add(gem.Name);
        sorted.Add(gem);
        return true;
    }
}
