using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Contexts.Assets;

/// <summary>
/// The project being run: what its .magic file holds, and the folder that file is in. Plain data;
/// <see cref="Open"/> finds and reads it.
/// </summary>
public sealed class Project
{
    public required string Name { get; init; }

    /// <summary>
    /// The domain the project opens with.
    /// </summary>
    public required Handle<Domain> EntryPoint { get; init; }

    /// <summary>
    /// The domain shown while another replaces the world (<see cref="World.OpenAsync"/>); none shows the engine's,
    /// <c>Domains/Loading/Loading.domain</c>.
    /// </summary>
    public Handle<Domain> Loading { get; set; }

    /// <summary>
    /// What is spawned at a domain's spawn when it opens in place of the world (<see cref="World.OpenAsync"/>): the
    /// player, whose camera is tagged <see cref="ECS.Tag.Player"/>. None spawns nothing.
    /// </summary>
    public Handle<Prefab> Player { get; set; }

    /// <summary>
    /// The gems to run, by name. Where dependencies do not decide, gems are made in this order.
    /// </summary>
    public IReadOnlyList<string> Gems { get; set; } = [];

    /// <summary>
    /// The folder the .magic file is in. Set when the file is read, so it is never part of the file.
    /// </summary>
    public string Root { get; internal set; } = string.Empty;

    /// <summary>
    /// The folders the engine's own assets and each loaded gem's are in, the engine's first and the gems' in the order
    /// listed: in a release, the copies the build made beside each assembly; in any other build, the source folders the
    /// build recorded, so assets are read where they are written. Set as the gems load, so it is never part of the file.
    /// </summary>
    public IReadOnlyList<string> Resources { get; internal set; } = [];

    /// <summary>
    /// Reads the project at <paramref name="path"/>: a .magic file, or the folder holding exactly one.
    /// </summary>
    internal static Result<Project> Open(IFileSystem files, string path)
    {
        Result<string> found = Find(files, path);
        if (!found.Ok)
        {
            return Result.Failure<Project>(found.Error);
        }

        Result<byte[]> bytes = files.Read(found.Value);
        if (!bytes.Ok)
        {
            return Result.Failure<Project>(bytes.Error);
        }

        Result<Project> read = Json.Read<Project>(bytes.Value);
        if (!read.Ok)
        {
            return Result.Failure<Project>($"{found.Value} is not a valid project: {read.Error}");
        }

        Project project = read.Value;
        project.Root = files.Parent(found.Value);
        return Result.Success(project);
    }

    private static Result<string> Find(IFileSystem files, string path)
    {
        if (files.FileExists(path))
        {
            return Result.Success(path);
        }

        if (!files.FolderExists(path))
        {
            return Result.Failure<string>($"There is no project at {path}: it is neither a .magic file nor a folder.");
        }

        IReadOnlyList<string> found = files.Find(path, ".magic");
        if (found.Count != 1)
        {
            return Result.Failure<string>($"The project folder {path} must hold exactly one .magic file; it holds {found.Count}.");
        }

        return Result.Success(found[0]);
    }
}
