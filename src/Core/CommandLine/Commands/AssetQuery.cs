using System.CommandLine;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Files;
using Magic.Utils;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>query asset</c>: prints the id, the path and the type of an asset, found by its path or by its id.
/// </summary>
/// <remarks>
/// A tool that holds an id or a path reads the other from here, so that no tool reads a <c>.meta</c> itself.
/// </remarks>
internal static class AssetQuery
{
    public static Command Create(App app, IFileSystem files, AssetManager assets, Option<string> output)
    {
        Argument<string?> path = new("path")
        {
            Description =
                "The path of an asset under its project, gem or Magic, as Materials/Carpet.mat, or its file's name",
            Arity = ArgumentArity.ZeroOrOne,
        };
        Option<string> id = new("--id") { Description = "An asset's id, which gives its path and its type" };
        Command asset = new("asset", "Prints the id, the path and the type of an asset, by its path or its id")
        {
            path,
            id,
        };
        asset.SetAction(parsed => Print(parsed.GetValue(id) ?? parsed.GetValue(path), parsed.GetValue(output)));

        return asset;

        void Print(string? asked, string? file)
        {
            if (!CommandLookups.IsProjectOpen(app, "look up an asset"))
                return;

            if (asked is null)
            {
                Log.Error("No asset was printed: give an asset's path, or its id as --id 300124.");

                return;
            }

            Result<IndexedFile> found = CommandLookups.FindAsset(assets, asked);
            if (found.Ok)
                CommandLookups.Print(files, file, CommandLookups.DescribeAsset(found.Value), "The asset is in");
            else
                Log.Error($"No asset was printed: {found.Error}");
        }
    }
}
