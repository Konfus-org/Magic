using System.Collections.ObjectModel;
using Magic.Files;
using Magic.Serialization;
using Magic.State;

namespace Magic.Projects;

public readonly record struct Project(string Name, ReadOnlyCollection<string> Gems);

public static class ProjectExtensions
{
    extension(Project)
    {
        public static Result<Project> Load(string projectPath, IFileSystem fileSystem)
        {
            var byteReadResult = fileSystem.ReadAllBytes(projectPath);
            if (!byteReadResult.Ok)
                return Result.Failure<Project>(
                    $"Failed to load the project file from {projectPath}. Ex: {byteReadResult.Error}"
                );

            var jsonReadResult = Json.Read<Project>(byteReadResult.Value);
            if (!jsonReadResult.Ok)
                return Result.Failure<Project>(
                    $"Failed to load the project file from {projectPath}. Ex: {jsonReadResult.Error}"
                );

            return jsonReadResult;
        }
    }
}
