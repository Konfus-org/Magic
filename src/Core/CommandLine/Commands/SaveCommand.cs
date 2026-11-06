using System.CommandLine;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>save</c>: saves the game as <c>Saves/&lt;name&gt;.save</c> beside Magic, holding <c>wait loaded</c> meanwhile.
/// </summary>
internal static class SaveCommand
{
    public static Command Create(App app, World world, CommandQueue queue)
    {
        Argument<string> name = new("name") { Description = "The save's name" };
        Command save = new("save", "Saves the game as Saves/<name>.save beside Magic") { name };
        save.SetAction(parsed => SaveGameAsync(parsed.GetRequiredValue(name)).FireAndForget());

        return save;

        async Task SaveGameAsync(string saveName)
        {
            if (!CommandLookups.IsProjectOpen(app, $"save {saveName} in"))
                return;

            Result saved = await queue
                .ChangeWorldAsync(() => world.SaveAsync(saveName, CancellationToken.None))
                .ConfigureAwait(false);
            if (saved.Ok)
                Log.Info($"Saved the game as {saveName}.");
            else
                Log.Error($"The game was not saved as {saveName}: {saved.Error}");
        }
    }
}
