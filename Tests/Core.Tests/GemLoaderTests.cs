using Core.Contexts;
using Core.Interfaces;
using Core.Services;
using DryIoc;
using System.Runtime.CompilerServices;

namespace Core.Tests;

public sealed class GemLoaderTests
{
    private static readonly string HelloWorldGem =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Gems", "HelloWorld.dll"));

    [Fact]
    public async Task Unload_lets_the_gem_assembly_be_collected()
    {
        using Container container = new();
        container.Register<IFileOperations, FileOperations>(Reuse.Singleton);
        container.Register<GemLoader>(Reuse.Singleton);
        GemLoader loader = container.Resolve<GemLoader>();

        WeakReference context = await LoadUseAndUnload(loader, container);

        for (int i = 0; i < 10 && context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(context.IsAlive, "GemLoadContext is still reachable after Unload: something holds a reference into the gem assembly.");
    }

    // Everything that touches gem types lives here, so no local in the test method keeps the assembly alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadUseAndUnload(GemLoader loader, IContainer container)
    {
        GemContext? gem = await loader.LoadSingleAsync(HelloWorldGem, null, CancellationToken.None);
        Assert.NotNull(gem);

        // Act like a gem offering a service and a consumer resolving it: the container now holds
        // (weakly) an instance of a gem type, and has compiled a resolve delegate for it.
        container.Provide<IGem>(gem.Loaded);
        Assert.Same(gem.Loaded, container.Resolve<IGem>());

        gem.Loaded.OnUnload();
        loader.Unload(gem);
        Assert.False(container.IsRegistered<IGem>());
        return new WeakReference(gem.Context);
    }
}
