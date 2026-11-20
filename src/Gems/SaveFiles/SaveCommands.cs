using System.CommandLine;
using Magic.CommandLine;
using Magic.Debugging;
using Magic.Files;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.SaveFiles;

/// <summary>
/// The console commands <c>save &lt;name&gt;</c> and <c>load &lt;name&gt;</c>, and where the save files are.
/// </summary>
/// <remarks>
/// A save is <c>Magic/&lt;project&gt;/Saves/&lt;name&gt;.save</c> under the local application data of the user. Each
/// command has <c>wait loaded</c> wait for its work on the world. The snapshot of a save is taken when its command runs,
/// and a <c>load</c> reads its file after the last save is written, so <c>save a</c> then <c>load a</c> in two lines
/// loads what the first saved.
/// </remarks>
internal sealed class SaveCommands(App app, World world, IFileSystem files, ConsoleCommands commands)
{
    /// <summary>
    /// The write of the last save, which a load waits for. Game thread.
    /// </summary>
    private Task _lastWrite = Task.CompletedTask;

    public Command CreateSave()
    {
        Argument<string> name = new("name") { Description = "The save's name" };
        Command save = new(
            "save",
            "Saves the game as Magic/<project>/Saves/<name>.save in the user's local application data"
        )
        {
            name,
        };
        save.SetAction(parsed => SaveAsync(parsed.GetRequiredValue(name)).FireAndForget());

        return save;
    }

    public Command CreateLoad()
    {
        Argument<string> name = new("name") { Description = "The save's name" };
        Command load = new("load", "Puts the game back as the save named <name> holds it") { name };
        load.SetAction(parsed => LoadAsync(parsed.GetRequiredValue(name)).FireAndForget());

        return load;
    }

    private async Task SaveAsync(string name)
    {
        if (!TryGetPath(name, "save", out string path))
            return;

        Task<Result> writing = WriteAsync(path);
        _lastWrite = writing;
        Result saved = await commands.ChangeWorldAsync(writing).ConfigureAwait(false);
        if (saved.Ok)
            Log.Info($"Saved the game as {name}.");
        else
            Log.Error($"The game was not saved as {name}: {saved.Error}");
    }

    private async Task<Result> WriteAsync(string path)
    {
        Result<byte[]> bytes = await world.SaveAsync(CancellationToken.None).ConfigureAwait(false);

        return bytes.Ok
            ? await files.WriteAsync(path, bytes.Value, CancellationToken.None).ConfigureAwait(false)
            : Result.Failure(bytes.Error);
    }

    private async Task LoadAsync(string name)
    {
        if (!TryGetPath(name, "load", out string path))
            return;

        Result loaded = await commands.ChangeWorldAsync(ReadAsync(path)).ConfigureAwait(false);
        if (!loaded.Ok)
            Log.Error($"The save {name} was not loaded: {loaded.Error}");
    }

    private async Task<Result> ReadAsync(string path)
    {
        await _lastWrite.ConfigureAwait(false);
        Result<byte[]> bytes = await files.ReadBytesAsync(path, CancellationToken.None).ConfigureAwait(false);

        return bytes.Ok
            ? await world.RestoreAsync(bytes.Value, Terminal.Progress, CancellationToken.None).ConfigureAwait(false)
            : Result.Failure(bytes.Error);
    }

    private bool TryGetPath(string name, string verb, out string path)
    {
        path = string.Empty;
        if (app.Project is not { } project)
        {
            Log.Error($"No project is running to {verb} {name} in.");

            return false;
        }

        path = files.Combine(UserFolders.GetProjectFolder(files, project), "Saves", name + ".save");

        return true;
    }
}
