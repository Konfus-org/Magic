using Magic.Assets;
using Magic.Events;
using Magic.Files;
using Magic.Utils;
using Xunit;

namespace Magic.Tests.Assets;

/// <summary>
/// An asset registered from memory: found, named and loaded as a file's, unless a file has its path.
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
}
