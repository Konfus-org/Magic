using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Magic.Files;
using Magic.Serialization;
using Magic.Utils;

namespace Magic.Runtime;

/// <summary>
/// A project as its <c>.magic</c> file describes it.
/// </summary>
public readonly record struct Project
{
    public required string Name { get; init; }

    public required ImmutableArray<string> Gems { get; init; }

    /// <summary>
    /// The <c>.magic</c> file it was read from, which <see cref="Load"/> gives it; never read from the file.
    /// </summary>
    [JsonIgnore]
    public string Path { get; init; }

    internal static Result<Project> Load(string projectPath, IFileSystem fileSystem)
    {
        var byteReadResult = fileSystem.ReadBytes(projectPath);
        if (!byteReadResult.Ok)
            return Result.Failure($"Failed to load the project file from {projectPath}. Ex: {byteReadResult.Error}");

        var jsonReadResult = Json.Read<Project>(byteReadResult.Value);
        if (!jsonReadResult.Ok)
            return Result.Failure($"Failed to load the project file from {projectPath}. Ex: {jsonReadResult.Error}");

        return Result.Success(jsonReadResult.Value with { Path = projectPath });
    }
}
