using Magic.Files;
using Magic.Serialization;
using Magic.Utils;

namespace Magic.Runtime;

public static class ProjectExtensions
{
    extension(Project project)
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

            return Result.Success(jsonReadResult.Value with { Path = projectPath });
        }
    }
}
