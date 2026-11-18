using System.Linq;
using System.Text.Json.Nodes;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Ecs;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>query world</c>: prints the world's domains and how many entities it holds.
/// </summary>
/// <remarks>
/// The console path <c>World</c> gives what a path can give, as <c>query World.IsPaused</c>, and <c>wait loaded</c>
/// holds a line until the world is there.
/// </remarks>
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
            ["domains"] = new JsonArray([
                .. world
                    .Domains.Select(domain => index.ById.GetValueOrDefault(domain.Id))
                    .OfType<IndexedFile>()
                    .Select(CommandLookups.DescribeAsset),
            ]),
            ["entities"] = entities,
        };
    }
}
