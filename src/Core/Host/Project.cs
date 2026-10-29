using System.Collections.Immutable;
using Magic.Files;
using Magic.Serialization;
using Magic.Utils;

namespace Magic.Host;

public readonly record struct Project
{
    public required string Name { get; init; }
    public required ImmutableArray<string> Gems { get; init; }
}

public static class ProjectExtensions
{
    extension(Project)
    {
        public static Result<Project> Load(string projectPath, IFileSystem fileSystem)
        {
            var byteReadResult = fileSystem.ReadBytes(projectPath);
            if (!byteReadResult.Ok)
                return Result.Failure(
                    $"Failed to load the project file from {projectPath}. Ex: {byteReadResult.Error}"
                );

            var jsonReadResult = Json.Read<Project>(byteReadResult.Value);
            if (!jsonReadResult.Ok)
                return Result.Failure(
                    $"Failed to load the project file from {projectPath}. Ex: {jsonReadResult.Error}"
                );

            return jsonReadResult;
        }
    }
}
