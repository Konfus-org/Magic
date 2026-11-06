using System.CommandLine;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>save</c>: saves the game as <c>Saves/&lt;name&gt;.save</c> beside Magic, holding <c>wait loaded</c> meanwhile.
/// </summary>
internal sealed class SaveCommand : Command
{
    private readonly App _app;

    private readonly World _world;

    private readonly CommandQueue _queue;

    public SaveCommand(App app, World world, CommandQueue queue)
        : base("save", "Saves the game as Saves/<name>.save beside Magic")
    {
        _app = app;
        _world = world;
        _queue = queue;
        Argument<string> name = new("name") { Description = "The save's name" };
        Arguments.Add(name);
        Action = new CliCommand(parsed => SaveGameAsync(parsed.GetRequiredValue(name)).FireAndForget());
    }

    private async Task SaveGameAsync(string name)
    {
        if (!CommandLookups.IsProjectOpen(_app, $"save {name} in"))
            return;

        Result saved = await _queue
            .ChangeWorldAsync(() => _world.SaveAsync(name, CancellationToken.None))
            .ConfigureAwait(false);
        if (saved.Ok)
            Log.Info($"Saved the game as {name}.");
        else
            Log.Error($"The game was not saved as {name}: {saved.Error}");
    }
}
