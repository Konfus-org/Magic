using System.CommandLine;
using Magic.Assets;
using Magic.Debugging;
using Magic.Files;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>open</c>: runs the project at a path and ends the run of any other, or opens a domain of the running project,
/// by its path or its name, in place of the world.
/// </summary>
/// <remarks>
/// For a domain, it holds <c>wait loaded</c> while the domain comes.
/// </remarks>
internal static class OpenCommand
{
    public static Command Create(App app, IFileSystem files, AssetManager assets, World world, CommandQueue queue)
    {
        Argument<string> project = new("project")
        {
            Description = "The .magic file to run; or the path or the name of a domain of the running project",
        };
        Command open = new(
            "open",
            "Opens the project at the given path and runs it, ending the run of any other; or opens a domain of the running project in place of the world"
        )
        {
            project,
        };
        open.SetAction(parsed => Open(parsed.GetRequiredValue(project)));

        return open;

        // Opens the domain that the running project has at the path or by the name. Else it reads the project at the
        // path and has the app run it; one that won't read is logged as an error, and whatever runs goes on.
        void Open(string path)
        {
            Handle<Domain> domain = app.Project is null
                ? Handle<Domain>.None
                : CommandLookups.FindAsset<Domain>(assets, path);
            if (domain != Handle<Domain>.None)
            {
                OpenDomainAsync(path, domain).FireAndForget();

                return;
            }

            if (path.EndsWith(".domain", StringComparison.OrdinalIgnoreCase))
            {
                Log.Error($"There is no domain at {path}.");

                return;
            }

            Result<byte[]> bytes = files.ReadBytes(path);
            Result<Project> read = bytes.Ok ? Json.Read<Project>(bytes.Value) : Result.Failure(bytes.Error);
            if (!read.Ok)
            {
                Log.Error($"The project {path} did not open: {read.Error}");
                return;
            }

            // The user's folder of a project has the project's name, and the user's caches are in the one named Cache.
            if (read.Value.Name.Equals("Cache", StringComparison.OrdinalIgnoreCase))
            {
                Log.Error($"The project {path} did not open: a project cannot have the name {read.Value.Name}.");
                return;
            }

            app.Project = read.Value with { Path = path };
        }

        // Opens the domain in place of the world, holding wait loaded meanwhile.
        async Task OpenDomainAsync(string path, Handle<Domain> domain)
        {
            Result opened = await queue
                .ChangeWorldAsync(world.OpenAsync(domain, Terminal.Progress, CancellationToken.None))
                .ConfigureAwait(false);
            if (!opened.Ok)
                Log.Error($"{path} did not open: {opened.Error}");
        }
    }
}
