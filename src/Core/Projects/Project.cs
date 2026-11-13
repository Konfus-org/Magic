using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Magic.Assets;
using Magic.Files;
using Magic.Utils;

namespace Magic.Projects;

/// <summary>
/// A project as its <c>.magic</c> file describes it.
/// </summary>
public readonly record struct Project
{
    public required string Name { get; init; }

    public required ImmutableArray<string> Gems { get; init; }

    /// <summary>
    /// The domain the run opens first; none opens the default one.
    /// </summary>
    public Handle<Domain> EntryPoint { get; init; }

    /// <summary>
    /// What shows while a domain replaces the world; none shows the engine's.
    /// </summary>
    public Handle<Domain> LoadingScreen { get; init; }

    /// <summary>
    /// What is put at a domain's spawn as it replaces the world; none puts nothing.
    /// </summary>
    public Handle<Prefab> Player { get; init; }

    /// <summary>
    /// The <c>.magic</c> file it was read from, set by the open command. It is not stored in the file.
    /// </summary>
    [JsonIgnore]
    public string Path { get; init; }

    /// <summary>
    /// Gets the path of the build of the project's own code, or null when it has none: <c>Projects/&lt;name&gt;/&lt;name&gt;.dll</c>
    /// beside Magic, with the name of the one csproj next to the <c>.magic</c> file.
    /// </summary>
    internal string? FindCodePath(IFileSystem files)
    {
        Result<string[]> csprojs = files.EnumerateFiles(files.GetParent(Path), "*.csproj", recursive: false);
        if (csprojs is not { Ok: true, Value: [string csproj] })
            return null;

        string name = System.IO.Path.GetFileNameWithoutExtension(csproj);
        string code = files.Combine(AppContext.BaseDirectory, "Projects", name, name + ".dll");

        return files.FileExists(code) ? code : null;
    }
}
