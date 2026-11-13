using System.CommandLine;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>load</c>: puts the game back as the save named <c>&lt;name&gt;</c> holds it, and holds <c>wait loaded</c>
/// meanwhile.
/// </summary>
internal static class LoadCommand
{
    public static Command Create(App app, World world, CommandQueue queue)
    {
        Argument<string> name = new("name") { Description = "The save's name" };
        Command load = new("load", "Puts the game back as the save named <name> holds it") { name };
        load.SetAction(parsed => LoadGameAsync(parsed.GetRequiredValue(name)).FireAndForget());

        return load;

        async Task LoadGameAsync(string saveName)
        {
            if (!CommandLookups.IsProjectOpen(app, $"load {saveName} in"))
                return;

            Result loaded = await queue
                .ChangeWorldAsync(world.RestoreAsync(saveName, Terminal.Progress, CancellationToken.None))
                .ConfigureAwait(false);
            if (!loaded.Ok)
                Log.Error($"The save {saveName} was not loaded: {loaded.Error}");
        }
    }
}
