using System.Linq;
using System.Text.Json.Nodes;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Ecs;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>query world</c>: prints whether the world loads or is paused, how fast its time runs, its domains and how many
/// entities it holds.
/// </summary>
internal static class WorldQuery
{
    public static JsonObject Describe(AssetManager assets, World world)
    {
        int entities = 0;
        foreach (EcsChunk chunk in world.Query())
        {
            entities += chunk.Entities.Length;
        }

        AssetIndex index = assets.Database.Index;

        return new JsonObject
        {
            ["isLoading"] = world.IsLoading,
            ["isPaused"] = world.IsPaused,
            ["timeScale"] = world.TimeScale,
            ["domains"] = new JsonArray([
                .. world
                    .Domains.Select(domain => index.ById.GetValueOrDefault(domain.Id))
                    .OfType<IndexedFile>()
                    .Select(AssetQuery.Describe),
            ]),
            ["entities"] = entities,
        };
    }
}
