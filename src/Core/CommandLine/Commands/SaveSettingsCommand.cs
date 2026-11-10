using System.CommandLine;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>save settings</c>: saves the settings that differ from their defaults for this user and project, which the
/// project opens with from then on.
/// </summary>
internal static class SaveSettingsCommand
{
    public static Command Create(Settings settings)
    {
        Command save = new("settings", "Saves the settings that differ from their defaults, for this user and project");
        save.SetAction(_ => SaveSettingsAsync().FireAndForget());

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
