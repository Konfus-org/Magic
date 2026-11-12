using Magic.Assets;
using Magic.Events;
using Magic.Files;
using Magic.Utils;
using Xunit;

namespace Magic.Tests.Assets;

/// <summary>
/// An asset registered from memory: found, named and loaded as a file's, unless a file has its path or its id.
/// </summary>
public sealed class AssetManagerTests
{
    [Fact]
    public async Task ARegisteredShaderIsFoundByItsPathAndLoadsItsText()
    {
        using App app = new();
        FileSystem files = new();
        using AssetManager assets = new(files, new Caches(files, app), new EventCoordinator(), app);
        string path = "Shaders/Tests/FromMemory.slang";

        using Subscription registered = assets.Register(path, new Shader { Text = "// from memory" });
        Handle<Shader> found = assets.Find<Shader>(path);
        Result<Shader> loaded = await assets.LoadAsync(found, null, TestContext.Current.CancellationToken);

        Assert.Equal(path, assets.GetPath(found));
        Assert.Equal("// from memory", loaded.Value?.Text);
    }

    [Fact]
    public void AFileAtTheSamePathWins()
    {
        using App app = new();
        FileSystem files = new();
        using AssetManager assets = new(files, new Caches(files, app), new EventCoordinator(), app);
        string path = "Shaders/Core/Heap.slang";
        Handle<Shader> file = assets.Find<Shader>(path);

        using Subscription registered = assets.Register(path, new Shader { Text = "// from memory" });

        Assert.NotEqual(Handle<Shader>.None, file);
        Assert.Equal(file, assets.Find<Shader>(path));
    }

    [Fact]
    public void DisposingAnEarlierRegisterOfAPathKeepsTheLaterAsset()
    {
        using App app = new();
        FileSystem files = new();
        using AssetManager assets = new(files, new Caches(files, app), new EventCoordinator(), app);
        string path = "Shaders/Tests/FromMemory.slang";

        Subscription first = assets.Register(path, new Shader { Text = "// first" });
        using Subscription second = assets.Register(path, new Shader { Text = "// second" });
        first.Dispose();

        Assert.NotEqual(Handle<Shader>.None, assets.Find<Shader>(path));
    }

    [Fact]
    public async Task AFileWithTheIdOfARegisteredAssetWins()
    {
        FileSystem files = new();
        string directory = files.Combine(System.IO.Path.GetTempPath(), $"Magic-{Guid.NewGuid():N}");
        try
        {
            using App app = new();
            using AssetManager assets = new(files, new Caches(files, app), new EventCoordinator(), app);
            using Subscription registered = assets.Register(
                "Shaders/Tests/FromMemory.slang",
                new Shader { Text = "// from memory" }
            );
            Handle<Shader> found = assets.Find<Shader>("Shaders/Tests/FromMemory.slang");
            files.CreateDirectory(files.Combine(directory, "Shaders"));
            files.Write(files.Combine(directory, "Shaders", "FromFile.slang"), "// from a file");
            files.Write(
                files.Combine(directory, "Shaders", "FromFile.slang.meta"),
                $$"""{ "id": {{found.Id}}, "type": "Shader" }"""
            );
            assets.Register(directory);

            Result<Shader> loaded = await assets.LoadAsync(found, null, TestContext.Current.CancellationToken);

            Assert.Equal("Shaders/FromFile.slang", assets.GetPath(found));
            Assert.Equal("// from a file", loaded.Value?.Text);
        }
        finally
        {
            files.Delete(directory, true);
        }
    }
}
