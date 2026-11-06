using System.CommandLine;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>load</c>: puts the game back as <c>Saves/&lt;name&gt;.save</c> beside Magic holds it, holding <c>wait loaded</c>
/// meanwhile.
/// </summary>
internal sealed class LoadCommand : Command
{
    private readonly App _app;

    private readonly World _world;

    private readonly CommandQueue _queue;

    public LoadCommand(App app, World world, CommandQueue queue)
        : base("load", "Puts the game back as Saves/<name>.save beside Magic holds it")
    {
        _app = app;
        _world = world;
        _queue = queue;
        Argument<string> name = new("name") { Description = "The save's name" };
        Arguments.Add(name);
        Action = new CliCommand(parsed => LoadGameAsync(parsed.GetRequiredValue(name)).FireAndForget());
    }

    private async Task LoadGameAsync(string name)
    {
        if (!CommandLookups.IsProjectOpen(_app, $"load {name} in"))
            return;

        Result loaded = await _queue
            .ChangeWorldAsync(() => _world.RestoreAsync(name, CancellationToken.None))
            .ConfigureAwait(false);
        if (!loaded.Ok)
            Log.Error($"The save {name} was not loaded: {loaded.Error}");
    }
}
