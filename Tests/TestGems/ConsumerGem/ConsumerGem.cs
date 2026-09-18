using Magic.Attributes;
using Magic.Interfaces;
using TestGems.Contracts;

namespace ConsumerGem;

/// <summary>Depends on ITestService from another gem; has an optional import nobody provides.</summary>
[Gem("ConsumerGem", "1.0.0", "Consumes ITestService.")]
[GemExport]
public sealed class ConsumerGem : ITestConsumer, IDisposable
{
    public ConsumerGem(ITestService service)
    {
        Service = service;
        TestEvents.Events.Add("Consumer.ctor");
    }

    public ITestService Service { get; }
    public byte[]? RestoredState { get; private set; }

    [GemImport(Required = false)]
    public IWindow? Window { get; set; }

    public object? OptionalImport => Window;

    [OnGemReloading]
    private byte[] Save() => [1, 2, 3];

    [OnGemReloaded]
    private void Restore(byte[] state) => RestoredState = state;

    public void Dispose()
    {
        TestEvents.Events.Add("Consumer.dispose");
    }
}
