using DryIoc;
using Magic.Contexts;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Runtime.CompilerServices;
using TestGems.Contracts;

// Log and TestEvents are process-wide, so the test classes must not interleave.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Core.Tests;

public sealed class GemManagerTests : IDisposable
{
    private static readonly string GemsDirectory = Path.Combine(AppContext.BaseDirectory, "Gems");
    private static string Gem(string name)
    {
        return Path.Combine(GemsDirectory, name + ".dll");
    }

    private readonly Container _container = new();
    private readonly GemManager _manager;

    public GemManagerTests()
    {
        TestEvents.Reset();
        _container.RegisterInstance<IContainer>(_container);
        _container.Register<IFileOperations, FileOperations>(Reuse.Singleton);
        _container.Register<SystemRegistry>(Reuse.Singleton);
        _container.Register<GemLoader>(Reuse.Singleton);
        _container.Register<GemManager>(Reuse.Singleton);
        _manager = _container.Resolve<GemManager>();
    }

    public void Dispose()
    {
        _manager.UnloadAll();
        _container.Dispose();
    }

    [Fact]
    public async Task Unload_lets_the_gem_assembly_be_collected()
    {
        WeakReference context = await LoadUseAndUnload();
        AssertCollected(context, "ProviderGem");
    }

    [Fact]
    public async Task Logger_gem_loads_first_and_unloads_last()
    {
        // Deliberately scrambled: the manager must order them, not the caller.
        await _manager.LoadAsync(Gem("ConsumerGem"), Gem("ProviderGem"), Gem("LoggerGem"));

        Assert.Equal("Logger.ctor", TestEvents.Events[0]);

        Log.Info("hello from the test");
        Assert.Contains(TestEvents.Logs, l => l == "Information: hello from the test");

        _manager.UnloadAll();
        Assert.Equal("Logger.dispose", TestEvents.Events[^1]);
    }

    [Fact]
    public async Task Gems_load_in_dependency_order_and_unload_in_reverse()
    {
        await _manager.LoadAsync(Gem("ConsumerGem"), Gem("ProviderGem"), Gem("LoggerGem"));

        Assert.Equal(["LoggerGem", "ProviderGem", "ConsumerGem"], _manager.Loaded.Select(g => g.Name));
        Assert.True(TestEvents.Events.IndexOf("Provider.ctor") < TestEvents.Events.IndexOf("Consumer.ctor"));

        ITestConsumer consumer = _container.Resolve<ITestConsumer>();
        Assert.Same(_container.Resolve<ITestService>(), consumer.Service);
        Assert.Null(consumer.OptionalImport); // optional [GemImport] with no provider stays null

        _manager.UnloadAll();
        Assert.True(TestEvents.Events.IndexOf("Consumer.dispose") < TestEvents.Events.IndexOf("Provider.dispose"));
        Assert.Empty(_manager.Loaded);
        Assert.False(_container.IsRegistered<ITestService>());
        Assert.False(_container.IsRegistered<ITestConsumer>());
    }

    [Fact]
    public async Task Gem_with_missing_dependency_is_skipped_with_a_warning()
    {
        await _manager.LoadAsync(Gem("LoggerGem"), Gem("ConsumerGem"));

        Assert.Equal(["LoggerGem"], _manager.Loaded.Select(g => g.Name));
        Assert.DoesNotContain("Consumer.ctor", TestEvents.Events);
        Assert.Contains(TestEvents.Logs, l =>
            l.StartsWith("Warning:") && l.Contains("ConsumerGem") && l.Contains(nameof(ITestService)));
    }

    [Fact]
    public async Task Reload_carries_state_and_rebuilds_dependents()
    {
        WeakReference[] oldContexts = await LoadReloadAndCheck();
        foreach (WeakReference old in oldContexts)
            AssertCollected(old, "a reloaded gem");
    }

    // Everything that touches gem instances lives in these helpers, so no local in the test method keeps
    // an assembly alive while we check it was collected.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> LoadUseAndUnload()
    {
        IReadOnlyList<GemContext> loaded = await _manager.LoadAsync(Gem("ProviderGem"));
        Assert.Single(loaded);

        // A consumer resolving the export: the container compiles a resolve delegate for a gem type.
        ITestService service = _container.Resolve<ITestService>();
        service.Counter = 7;
        Assert.Same(service, _container.Resolve<ITestService>());

        _manager.UnloadAll();
        Assert.False(_container.IsRegistered<ITestService>());
        Assert.Contains("Provider.dispose", TestEvents.Events);
        return new WeakReference(loaded[0].Alc);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference[]> LoadReloadAndCheck()
    {
        await _manager.LoadAsync(Gem("LoggerGem"), Gem("ProviderGem"), Gem("ConsumerGem"));
        _container.Resolve<ITestService>().Counter = 42;
        WeakReference[] oldContexts = _manager.Loaded
            .Where(g => g.Name != "LoggerGem")
            .Select(g => new WeakReference(g.Alc))
            .ToArray();
        TestEvents.Events.Clear();

        await _manager.ReloadAsync(Gem("ProviderGem"));

        Assert.Equal(["Consumer.dispose", "Provider.dispose", "Provider.ctor", "Consumer.ctor"], TestEvents.Events);
        Assert.Equal(["LoggerGem", "ProviderGem", "ConsumerGem"], _manager.Loaded.Select(g => g.Name));
        Assert.Equal(42, _container.Resolve<ITestService>().Counter);
        ITestConsumer consumer = _container.Resolve<ITestConsumer>();
        Assert.Equal([1, 2, 3], consumer.RestoredState);
        Assert.Same(_container.Resolve<ITestService>(), consumer.Service); // wired to the new provider
        return oldContexts;
    }

    private static void AssertCollected(WeakReference context, string what)
    {
        for (int i = 0; i < 10 && context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(context.IsAlive, $"The load context of {what} is still reachable after unload: something holds a reference into the gem assembly.");
    }
}
