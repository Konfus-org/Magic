using System.Collections.Immutable;
using System.CommandLine;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Files;
using Magic.Threading;
using Magic.Utils;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>import &lt;file&gt; [folder] [--replace] [--meta &lt;json&gt;]</c>: copies a file into a folder of the project,
/// gives it its <c>.meta</c>, loads it one time, and prints the id, the path and the type that it has as an asset. A
/// file that the project holds already gets its <c>.meta</c> where it stands, with no copy.
/// </summary>
/// <remarks>
/// It holds the lines after it until the asset is loaded, so what its importer makes is cached when the next line runs.
/// </remarks>
internal static class ImportCommand
{
    public static Command Create(App app, IFileSystem files, AssetManager assets, CommandQueue queue)
    {
        Argument<string> file = new("file")
        {
            Description =
                "The file to copy into the project; one that the project holds already gets its .meta where it stands",
        };
        Argument<string?> folder = new("folder")
        {
            Description = "The folder under the project that takes it, as Textures; the project's folder when left out",
            Arity = ArgumentArity.ZeroOrOne,
        };
        Option<bool> replace = new("--replace")
        {
            Description =
                "Write over the asset of that name in the folder, which keeps its .meta and so its id; without it, a file of that name stops the import",
        };
        Option<string?> meta = new("--meta")
        {
            Description =
                "The import settings that the new asset's .meta sets, as the JSON of that .meta, as {\"usage\": \"mask\"} for a texture of data; a file that is an asset already keeps the .meta it has, and this stops the import",
        };
        Command import = new(
            "import",
            "Copies a file into the project as an asset, and prints the id, the path and the type of the asset"
        )
        {
            file,
            folder,
            replace,
            meta,
        };
        import.SetAction(parsed =>
        {
            string from = parsed.GetRequiredValue(file);
            string? into = parsed.GetValue(folder);
            bool over = parsed.GetValue(replace);
            Result<JsonObject> settings = Settings(parsed.GetValue(meta));
            if (!settings.Ok)
            {
                Log.Error($"{from} was not imported: {settings.Error}");

                return;
            }

            Import(
                from,
                InProject(from) is { } held ? assets.MakeHandle(held, settings.Value)
                    : over ? Replace(from, into, settings.Value)
                    : Add(from, into, settings.Value)
            );
        });

        return import;

        // The import settings that --meta names, and nothing to set when it names none.
        static Result<JsonObject> Settings(string? text)
        {
            if (text is null)
                return Result.Success(new JsonObject());

            Result<JsonObject> read = Json.Read<JsonObject>(Encoding.UTF8.GetBytes(text));

            return read.Ok ? read : Result.Failure($"--meta is no JSON object: {read.Error}");
        }

        // Loads the asset that one of the two writes made, or says why it made none.
        void Import(string file, Result<IndexedFile> made)
        {
            if (!made.Ok)
            {
                Log.Error($"{file} was not imported: {made.Error}");

                return;
            }

            Task loading = LoadAsync(made.Value);
            loading.FireAndForget();
            queue.Hold(() => !loading.IsCompleted);
        }

        // The file as it stands in the project, which gets its .meta there: nothing is copied, and the asset keeps the
        // path that every file which names it holds. Null when the file is not in the project. It is joined to the
        // project's folder, because an asset's path is made from that folder, which a path relative to the terminal's
        // folder does not share.
        string? InProject(string file)
        {
            if (app.Project is not { } project)
                return null;

            string directory = files.GetParent(project.Path);
            string under = Path.GetRelativePath(directory, file);

            return Path.IsPathRooted(under) || under.StartsWith("..", StringComparison.Ordinal)
                ? null
                : files.Combine(directory, under);
        }

        // The folder under the project that takes the copy.
        Result<string> Destination(string? folder)
        {
            if (app.Project is not { } project)
                return Result.Failure("no project is running to import a file into.");

            string directory = files.GetParent(project.Path);

            return Result.Success(folder is null ? directory : files.Combine(directory, folder));
        }

        // Copies the file in as an asset of its own and writes its .meta. A copy that gets no .meta is deleted again,
        // so that no file without one stays in the project.
        Result<IndexedFile> Add(string file, string? folder, JsonObject settings)
        {
            Result<string> directory = Destination(folder);
            if (!directory.Ok)
                return Result.Failure(directory.Error);

            string copy = files.Combine(directory.Value, Path.GetFileName(file));
            if (files.FileExists(copy))
                return Result.Failure($"{copy} exists already, and --replace writes over it.");

            Result copied = Copy(file, copy);
            if (!copied.Ok)
                return Result.Failure(copied.Error);

            Result<IndexedFile> made = assets.MakeHandle(copy, settings);
            if (!made.Ok)
                files.Delete(copy);

            return made;
        }

        // Writes the file over the asset of that name. The .meta beside it stays, so the asset keeps the id that every
        // file which names it holds, and MakeHandle reads that .meta back.
        Result<IndexedFile> Replace(string file, string? folder, JsonObject settings)
        {
            Result<string> directory = Destination(folder);
            if (!directory.Ok)
                return Result.Failure(directory.Error);

            string copy = files.Combine(directory.Value, Path.GetFileName(file));
            if (!files.FileExists(copy + AssetIndex.Sidecar))
                return Result.Failure($"there is no asset at {copy} to replace, so import it without --replace.");

            // Before the copy: the asset keeps the .meta it has, so --meta would write the settings a second time,
            // and the copy over the file would stand while the import failed.
            if (settings.Count != 0)
            {
                return Result.Failure(
                    $"{copy}{AssetIndex.Sidecar} holds the import settings of {copy} already; set them in that .meta."
                );
            }

            Result copied = Copy(file, copy);

            return copied.Ok ? assets.MakeHandle(copy, settings) : Result.Failure(copied.Error);
        }

        Result Copy(string file, string copy)
        {
            if (!files.FileExists(file))
                return Result.Failure($"there is no file at {file}.");

            Result copied = Result.FindFailure(files.CreateDirectory(files.GetParent(copy)), files.Copy(file, copy));

            return copied.Ok ? Result.Success() : Result.Failure($"{copy} was not written: {copied.Error}");
        }

        async Task LoadAsync(IndexedFile made)
        {
            Result<ImmutableArray<ulong>> loaded = await assets
                .BakeAsync(made.Path, Terminal.Progress, app.Lifetime)
                .ConfigureAwait(false);
            if (!loaded.Ok)
                Log.Error($"{made.Path} was imported, but did not load: {loaded.Error}");

            CommandLookups.PrintAsset(made);
        }
    }
}
