using System.CommandLine;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>save settings</c>: saves the settings that differ from their defaults for this user and project.
/// </summary>
/// <remarks>
/// The project opens with them from then on. The command is the subcommand of <c>save</c>, which a gem can give an
/// argument for the saves of the game.
/// </remarks>
internal static class SaveSettingsCommand
{
    public static Command Create(Settings settings)
    {
        Command settingsCommand = new(
            "settings",
            "Saves the settings that differ from their defaults, for this user and project"
        );
        settingsCommand.SetAction(_ => SaveSettingsAsync().FireAndForget());
        Command save = new("save", "Saves the settings") { settingsCommand };

        return save;

        async Task SaveSettingsAsync()
        {
            Result saved = await settings.SaveAsync(CancellationToken.None).ConfigureAwait(false);
            if (saved.Ok)
                Log.Info("Saved the settings.");
            else
                Log.Error($"The settings were not saved: {saved.Error}");
        }
    }
}
