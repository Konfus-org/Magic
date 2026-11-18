using System.CommandLine;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Files;
using Magic.Threading;
using Magic.Utils;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>new asset</c>: makes a new asset file with the values of Magic's default asset of its type, gives it a
/// <c>.meta</c>, and prints the id, the path and the type that it has as an asset.
/// </summary>
/// <remarks>
/// A tool that makes an asset of its own, as the Blender add-on makes a material, makes it here, then writes its
/// values with <c>set</c> and <c>export</c>: the type and the format stay Magic's.
/// </remarks>
internal static class AssetCommand
{
    public static Command Create(App app, IFileSystem files, AssetManager assets, CommandQueue queue)
    {
        Argument<string> made = new("file")
        {
            Description =
                "The file to make, in a folder that this run reads assets from, as Materials/Spruce.mat: it gets the values of Magic's default asset of that extension's type and the next id free in its folder",
        };
        Command asset = new(
            "asset",
            "Makes a new asset file with the values of Magic's default asset of its type, and prints the id, the path and the type of the asset"
        )
        {
            made,
        };
        asset.SetAction(parsed => Make(parsed.GetRequiredValue(made)));

        return asset;

        void Make(string file)
        {
            if (!CommandLookups.IsProjectOpen(app, "make an asset of"))
                return;

            // The export writes over a file, and MakeHandle keeps the .meta beside one, so a new asset over an asset
            // that exists would take every value of it away.
            if (files.FileExists(file))
            {
                Log.Error(
                    $"{file} was not made: there is a file there already. Name another file, or set the values of the "
                        + "asset that stands there."
                );

                return;
            }

            Result<IndexedFile> standard = assets.FindDefaultOf(file);
            if (!standard.Ok)
            {
                Log.Error($"{file} was not made: {standard.Error}");

                return;
            }

            Result written = default;
            Task writing = WriteAsync();
            writing.FireAndForget();

            // The .meta is written on the game thread, where the asset folders are read again.
            queue.Hold(writing, WriteMeta);

            async Task WriteAsync() =>
                written = await assets
                    .ExportAsync(standard.Value.Id, file, includesDefaults: false, Terminal.Progress, app.Lifetime)
                    .ConfigureAwait(false);

            void WriteMeta()
            {
                if (!written.Ok)
                {
                    Log.Error($"{file} was not made: {written.Error}");

                    return;
                }

                Result<IndexedFile> made = assets.MakeHandle(file, []);
                if (made.Ok)
                    CommandLookups.PrintAsset(made.Value);
                else
                    Log.Error($"{file} was written, and it is no asset: {made.Error}");
            }
        }
    }
}
