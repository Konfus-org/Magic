using System.CommandLine;
using System.Linq;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>export &lt;asset&gt; &lt;file&gt;</c>: loads an asset, found by its path or by its id, and writes it to a file
/// outside the assets.
/// </summary>
/// <remarks>
/// The gem that writes the asset's type makes the file, or Core for a type that it reads itself. It holds the lines
/// after it until the file is written.
/// </remarks>
internal static class ExportCommand
{
    public static Command Create(App app, AssetManager assets, CommandQueue queue)
    {
        Argument<string> asset = new("asset")
        {
            Description = "The asset's path under its project, gem or Magic, as Materials/Carpet.mat, or its id",
        };
        Argument<string> file = new("file")
        {
            Description = "The file to write, which ends in the extension of the asset's type, as Carpet.png",
        };
        Option<bool> defaults = new("--defaults")
        {
            Description = "Write each value that holds the default of its type too; without it, they are left out",
        };
        Command export = new("export", "Writes an asset, by its path or its id, to a file") { asset, file, defaults };
        export.SetAction(parsed =>
            Export(parsed.GetRequiredValue(asset), parsed.GetRequiredValue(file), parsed.GetValue(defaults))
        );

        return export;

        void Export(string asked, string file, bool includesDefaults)
        {
            if (!CommandLookups.IsProjectOpen(app, "export an asset of"))
                return;

            Task exporting = ExportAsync(asked, file, includesDefaults);
            exporting.FireAndForget();
            queue.Hold(() => !exporting.IsCompleted);
        }

        async Task ExportAsync(string asked, string file, bool includesDefaults)
        {
            Result<IndexedFile> found = asked.All(char.IsAsciiDigit)
                ? CommandLookups.FindAssetById(assets, asked)
                : CommandLookups.FindAssetByPath(assets, asked);
            if (!found.Ok)
            {
                Log.Error($"{asked} was not exported: {found.Error}");

                return;
            }

            Result written = await assets
                .ExportAsync(found.Value.Id, file, includesDefaults, Terminal.Progress, app.Lifetime)
                .ConfigureAwait(false);
            if (written.Ok)
                Log.Info($"Exported {found.Value.Path} to {file}.");
            else
                Log.Error($"{asked} was not exported: {written.Error}");
        }
    }
}
