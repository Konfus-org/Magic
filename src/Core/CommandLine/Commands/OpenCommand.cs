using System.CommandLine;
using Magic.Debugging;
using Magic.Files;
using Magic.Files.Types;
using Magic.Projects;
using Magic.Serialization;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>open</c>: runs the project at a path, ending the run of any other; or, for a <c>.domain</c>, opens that domain in
/// place of the world, holding <c>wait loaded</c> while it comes.
/// </summary>
internal static class OpenCommand
{
    public static Command Create(App app, IFileSystem files, Assets assets, World world, CommandQueue queue)
    {
        Argument<string> project = new("project") { Description = "The .magic file to run" };
        Command open = new(
            "open",
            "Opens the project at the given path and runs it, ending the run of any other; or, for a .domain, opens that domain in place of the world"
        )
        {
            project,
        };
        open.SetAction(parsed => Open(parsed.GetRequiredValue(project)));

        return open;

        // Reads the project at the path and has the app run it; one that won't read is logged as an error, and whatever
        // runs goes on.
        void Open(string path)
        {
            if (path.EndsWith(".domain", StringComparison.OrdinalIgnoreCase))
            {
                OpenDomainAsync(path).FireAndForget();

                return;
            }

            Result<byte[]> bytes = files.ReadBytes(path);
            Result<Project> read = bytes.Ok ? Json.Read<Project>(bytes.Value) : Result.Failure(bytes.Error);
            if (!read.Ok)
            {
                Log.Error($"The project {path} did not open: {read.Error}");
                return;
            }

            app.Project = read.Value with { Path = path };
        }

        // Opens the domain at the path in place of the world, holding wait loaded meanwhile.
        async Task OpenDomainAsync(string path)
        {
            if (!CommandLookups.IsProjectOpen(app, $"open {path} in"))
                return;

            Handle<Domain> domain = assets.Find<Domain>(path);
            if (domain == Handle<Domain>.None)
            {
                Log.Error($"There is no domain at {path}.");

                return;
            }

            Result opened = await queue
                .ChangeWorldAsync(() => world.OpenAsync(domain, null, CancellationToken.None))
                .ConfigureAwait(false);
            if (!opened.Ok)
                Log.Error($"{path} did not open: {opened.Error}");
        }
    }
}
