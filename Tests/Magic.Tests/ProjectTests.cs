using Magic.Contexts.Assets;
using Magic.Services;
using System.Text.Json;
using Xunit;

namespace Magic.Tests;

public sealed class ProjectTests
{
    [Fact]
    public void An_empty_file_is_a_project_with_defaults()
    {
        Project project = JsonSerializer.Deserialize<Project>("{}", AssetJson.Options)!;

        Assert.Equal("Magic", project.Name);
        Assert.False(project.Icon.IsValid);
    }

    [Fact]
    public void Folders_hang_off_the_root_whoever_sets_it()
    {
        Project project = new() { Root = @"C:\Games\Demo" };

        Assert.Equal(@"C:\Games\Demo\Assets", project.Assets);
        Assert.Equal(@"C:\Games\Demo\Cache", project.Cache);
        Assert.Equal(@"C:\Games\Demo\Logs", project.Logs);
    }

    [Fact]
    public void The_file_uses_the_asset_json_dialect()
    {
        Project project = JsonSerializer.Deserialize<Project>("""
            {
                // camelCase keys, handles as { id }, trailing commas
                "name": "Demo",
                "icon": { "id": 36 },
                "root": "ignored",
            }
            """, AssetJson.Options)!;

        Assert.Equal("Demo", project.Name);
        Assert.Equal(36ul, project.Icon.Id);
        Assert.Equal("", project.Root);
    }
}
