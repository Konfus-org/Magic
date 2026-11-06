using System.CommandLine;
using Magic.Debugging;
using Magic.Files;
using Magic.Files.Types;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>open</c>: runs the project at a path, ending the run of any other; or, for a <c>.domain</c>, opens that domain in
/// place of the world, holding <c>wait loaded</c> while it comes.
/// </summary>
internal sealed class OpenCommand : Command
{
    private readonly App _app;

    private readonly IFileSystem _files;

    private readonly Assets _assets;

    private readonly World _world;

    private readonly CommandQueue _queue;

    public OpenCommand(App app, IFileSystem files, Assets assets, World world, CommandQueue queue)
        : base(
            "open",
            "Opens the project at the given path and runs it, ending the run of any other; or, for a .domain, opens that domain in place of the world"
        )
    {
        _app = app;
        _files = files;
        _assets = assets;
        _world = world;
        _queue = queue;
        Argument<string> project = new("project") { Description = "The .magic file to run" };
        Arguments.Add(project);
        Action = new CliCommand(parsed => Open(parsed.GetRequiredValue(project)));
    }

    /// <summary>
    /// Reads the project at <paramref name="path"/> and has the app run it; one that won't read is logged as an error,
    /// and whatever runs goes on.
    /// </summary>
    private void Open(string path)
    {
        if (path.EndsWith(".domain", StringComparison.OrdinalIgnoreCase))
        {
            OpenDomainAsync(path).FireAndForget();

            return;
        }

        Result<Project> project = Project.Load(path, _files);
        if (!project.Ok)
        {
            Log.Error(project.Error);
            return;
        }

        _app.Project = project.Value;
    }

    /// <summary>
    /// Opens the domain at <paramref name="path"/> in place of the world, holding <c>wait loaded</c> meanwhile.
    /// </summary>
    private async Task OpenDomainAsync(string path)
    {
        if (!CommandLookups.IsProjectOpen(_app, $"open {path} in"))
            return;

        Handle<Domain> domain = _assets.Find<Domain>(path);
        if (domain == Handle<Domain>.None)
        {
            Log.Error($"There is no domain at {path}.");

            return;
        }

        Result opened = await _queue
            .ChangeWorldAsync(() => _world.OpenAsync(domain, DomainOpenMode.Replace, null, CancellationToken.None))
            .ConfigureAwait(false);
        if (!opened.Ok)
            Log.Error($"{path} did not open: {opened.Error}");
    }
}
