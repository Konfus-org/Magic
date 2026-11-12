using System.CommandLine;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>save</c>: saves the game and holds <c>wait loaded</c> meanwhile; <c>save settings</c> saves the settings instead.
/// </summary>
/// <remarks>
/// The file is <c>Magic/&lt;project&gt;/Saves/&lt;name&gt;.save</c> in the local application data of the user.
/// </remarks>
internal static class SaveCommand
{
    public static Command Create(App app, Settings settings, World world, CommandQueue queue)
    {
        Argument<string> name = new("name") { Description = "The save's name" };
        Command save = new(
            "save",
            "Saves the game as Magic/<project>/Saves/<name>.save in the user's local application data"
        )
        {
            name,
            SaveSettingsCommand.Create(settings),
        };
        save.SetAction(parsed => SaveGameAsync(parsed.GetRequiredValue(name)).FireAndForget());

        return save;

        async Task SaveGameAsync(string saveName)
        {
            if (!CommandLookups.IsProjectOpen(app, $"save {saveName} in"))
                return;

            Result saved = await queue
                .ChangeWorldAsync(world.SaveAsync(saveName, CancellationToken.None))
                .ConfigureAwait(false);
            if (saved.Ok)
                Log.Info($"Saved the game as {saveName}.");
            else
                Log.Error($"The game was not saved as {saveName}: {saved.Error}");
        }
    }
}
