using System.CommandLine;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Files;
using Magic.Utils;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>new handle</c>: gives a file that is no asset yet its <c>.meta</c>, and prints the id, the path and the type
/// that it has as an asset.
/// </summary>
internal static class HandleCommand
{
    public static Command Create(App app, AssetManager assets)
    {
        Argument<string> made = new("file")
        {
            Description =
                "The file of a folder that this run reads assets from: writes its .meta with the next id free in that folder, and prints the id of a file that has one already",
        };
        Command handle = new("handle", "Gives a file its .meta, and prints the id, the path and the type of the asset")
        {
            made,
        };
        handle.SetAction(parsed => Print(parsed.GetRequiredValue(made)));

        return handle;

        void Print(string file)
        {
            if (!CommandLookups.IsProjectOpen(app, "make an asset"))
                return;

            Result<IndexedFile> made = assets.MakeHandle(file);
            if (made.Ok)
                Terminal.Write(AssetQuery.Describe(made.Value).ToJsonString(Json.Options));
            else
                Log.Error($"No asset was made: {made.Error}");
        }
    }
}
