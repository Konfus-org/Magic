using Magic.Projects;

namespace Magic.Files;

/// <summary>
/// Where what is the user's own is kept, under their local application data: a folder per project, with its
/// <c>Settings</c> and its <c>Saves</c>.
/// </summary>
internal static class UserFolders
{
    /// <summary>
    /// <c>Magic/&lt;project&gt;</c> under the user's local application data.
    /// </summary>
    public static string GetProjectFolder(IFileSystem files, Project project) =>
        files.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Magic", project.Name);
}
