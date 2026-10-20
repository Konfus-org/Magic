using Magic.Contexts.Assets;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Internal;

/// <summary>
/// Finds a project's .magic file and reads it into a <see cref="Project"/>, which is plain data: the path given, or the
/// one .magic file in that folder. <see cref="Project.Root"/> is set here, from where the file is.
/// </summary>
internal static class ProjectFile
{
    /// <summary>
    /// Reads the project at <paramref name="path"/>: a .magic file, or the folder holding exactly one.
    /// </summary>
    internal static Result<Project> Open(IFileSystem files, string path)
    {
        Result<string> found = Find(files, path);
        if (!found.Ok) return Result.Failure<Project>(found.Error);

        Result<byte[]> bytes = files.Read(found.Value);
        if (!bytes.Ok) return Result.Failure<Project>(bytes.Error);

        Result<Project> read = Json.Read<Project>(bytes.Value);
        if (!read.Ok) return Result.Failure<Project>($"{found.Value} is not a valid project: {read.Error}");

        Project project = read.Value;
        project.Root = files.GetParent(found.Value);
        return Result.Success(project);
    }

    private static Result<string> Find(IFileSystem files, string path)
    {
        if (files.FileExists(path)) return Result.Success(path);

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
