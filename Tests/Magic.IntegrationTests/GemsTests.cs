using Magic.Services;
using Xunit;

namespace Magic.IntegrationTests;

/// <summary>
/// Which gems load from where: the engine folder filtered by the project's list, the project tree taken whole.
/// The engine folder here is bin\Tests\Gems, where TestGem.csproj builds to.
/// </summary>
public sealed class GemsTests
{
    private static readonly string EngineGems = Path.Combine(AppContext.BaseDirectory, "Gems");

    [Theory]
    [InlineData("TestGem", 1)]
    [InlineData("default", 1)]
    [InlineData("Nope", 0)]
    public void The_engine_folder_loads_only_the_listed_names(string name, int expected)
    {
        using TempFolder project = new();
        using Gems gems = new(new Container(), new FileSystem(), new Events());

        gems.Load(EngineGems, [name], project.Path);

        Assert.Equal(expected, gems.Loaded.Length);
    }

    [Fact]
    public void An_empty_list_loads_no_engine_gems()
    {
        using TempFolder project = new();
        using Gems gems = new(new Container(), new FileSystem(), new Events());

        gems.Load(EngineGems, [], project.Path);

        Assert.Empty(gems.Loaded);
    }

    [Fact]
    public void A_gem_in_the_project_tree_loads_without_being_listed()
    {
        using TempFolder temp = new();
        string project = Path.Combine(temp.Path, "Project");
        CopyTestGem(Path.Combine(project, "Scripts", "bin"));
        using Gems gems = new(new Container(), new FileSystem(), new Events());

        // An engine folder beside the project, not inside it: inside would make this the engine's own project.
        gems.Load(Path.Combine(temp.Path, "NoEngineGemsHere"), [], project);

        Assert.Single(gems.Loaded);
    }

    [Theory]
    [InlineData("obj")]
    [InlineData("Cache")]
    public void Build_and_cache_folders_of_the_project_are_not_searched(string folder)
    {
        using TempFolder temp = new();
        string project = Path.Combine(temp.Path, "Project");
        CopyTestGem(Path.Combine(project, folder, "Scripts"));
        using Gems gems = new(new Container(), new FileSystem(), new Events());

        gems.Load(Path.Combine(temp.Path, "NoEngineGemsHere"), [], project);

        Assert.Empty(gems.Loaded);
    }

    [Fact]
    public void The_engine_as_its_own_project_does_not_load_its_gems_twice()
    {
        using Gems gems = new(new Container(), new FileSystem(), new Events());

        gems.Load(EngineGems, ["default"], Path.GetDirectoryName(EngineGems)!);

        Assert.Single(gems.Loaded);
    }

    /// <summary>Puts TestGem.dll and its deps.json in <paramref name="folder"/>.</summary>
    private static void CopyTestGem(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (string file in new[] { "TestGem.dll", "TestGem.deps.json" })
            File.Copy(Path.Combine(EngineGems, file), Path.Combine(folder, file));
    }
}
