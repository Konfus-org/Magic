using Magic.Services;
using Xunit;

namespace Magic.UnitTests.Services;

/// <summary>
/// The path methods only: pure string work. What touches disk is covered by the integration suite.
/// </summary>
public sealed class FileSystemTests
{
    [Fact]
    public void Segments_are_the_names_between_the_separators()
    {
        FileSystem files = new();

        string[] segments = files.Segments(files.Combine("Assets", "Models", "Monkey.glb"));

        Assert.Equal(["Assets", "Models", "Monkey.glb"], segments);
    }

    [Fact]
    public void A_path_beneath_the_root_is_under_it()
    {
        FileSystem files = new();

        bool under = files.IsUnder("Project", files.Combine("Project", "Assets", "Monkey.glb"));

        Assert.True(under);
    }

    [Fact]
    public void The_root_is_under_itself()
    {
        FileSystem files = new();

        bool under = files.IsUnder("Project", "Project");

        Assert.True(under);
    }

    [Fact]
    public void A_folder_that_only_shares_the_roots_prefix_is_not_under_it()
    {
        FileSystem files = new();

        bool under = files.IsUnder("Project", files.Combine("ProjectBackup", "Monkey.glb"));

        Assert.False(under);
    }

    [Fact]
    public void A_name_starting_with_dots_is_still_under_the_root()
    {
        FileSystem files = new();

        bool under = files.IsUnder("Project", files.Combine("Project", "..hidden"));

        Assert.True(under);
    }
}
