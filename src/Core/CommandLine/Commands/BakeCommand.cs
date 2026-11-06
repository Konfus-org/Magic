using System.CommandLine;
using Magic.Debugging;
using Magic.Files;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>bake</c>: queues the lines that open every domain under <c>Domains/</c> in turn, wait for each to load and settle,
/// so their caches are made, and exit.
/// </summary>
internal sealed class BakeCommand : Command
{
    private readonly App _app;

    private readonly Assets _assets;

    private readonly CommandQueue _queue;

    public BakeCommand(App app, Assets assets, CommandQueue queue)
        : base("bake", "Opens every domain under Domains/ in turn, so their caches are made, then exits")
    {
        _app = app;
        _assets = assets;
        _queue = queue;
        Action = new CliCommand(_ => Bake());
    }

    private void Bake()
    {
        if (!CommandLookups.IsProjectOpen(_app, "bake"))
            return;

        int domains = 0;
        foreach (string path in _assets.GetPathsUnder("Domains"))
        {
            if (!path.EndsWith(".domain", StringComparison.OrdinalIgnoreCase))
                continue;

            domains++;
            _queue.Enqueue($"open {path}");
            _queue.Enqueue("wait loaded");
            _queue.Enqueue("wait 120");
        }

        Log.Info($"Baking {domains} domains.");
        _queue.Enqueue("exit");
    }
}
