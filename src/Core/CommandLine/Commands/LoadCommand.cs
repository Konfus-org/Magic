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
internal static class LoadCommand
{
    public static Command Create(App app, World world, CommandQueue queue)
    {
        Argument<string> name = new("name") { Description = "The save's name" };
        Command load = new("load", "Puts the game back as Saves/<name>.save beside Magic holds it") { name };
        load.SetAction(parsed => LoadGameAsync(parsed.GetRequiredValue(name)).FireAndForget());

        return load;

        async Task LoadGameAsync(string saveName)
        {
            if (!CommandLookups.IsProjectOpen(app, $"load {saveName} in"))
                return;

            Result loaded = await queue
                .ChangeWorldAsync(() => world.RestoreAsync(saveName, CancellationToken.None))
                .ConfigureAwait(false);
            if (!loaded.Ok)
                Log.Error($"The save {saveName} was not loaded: {loaded.Error}");
        }
    }
}
