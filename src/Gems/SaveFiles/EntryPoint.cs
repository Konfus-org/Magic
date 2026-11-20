using Magic.CommandLine;
using Magic.Files;
using Magic.Projects;
using Magic.Worlds;

namespace Magic.SaveFiles;

/// <summary>
/// The gem: it makes the <see cref="SaveSerializer"/> that writes the saves, and adds <c>save &lt;name&gt;</c> and
/// <c>load &lt;name&gt;</c> to the console.
/// </summary>
public sealed class EntryPoint : IGem
{
    public EntryPoint(App app, World world, IFileSystem files, ConsoleCommands commands)
    {
        SaveCommands saves = new(app, world, files, commands);
        commands.Register(saves.CreateSave());
        commands.Register(saves.CreateLoad());
    }

    public SaveSerializer SaveSerializer { get; } = new();
}
