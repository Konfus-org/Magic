using Magic.Attributes;
using TestGems.Contracts;

namespace ProviderGem;

/// <summary>
/// Exports ITestService through a separate class, and the gem class itself depends on that export, which
/// exercises ordering of parts within one gem. Carries the counter across a hot reload.
/// </summary>
[Gem("ProviderGem", "1.0.0", "Exports ITestService.")]
public sealed class ProviderGem : IDisposable
{
    private readonly ITestService _service;

    public ProviderGem(ITestService service)
    {
        _service = service;
        TestEvents.Events.Add("Provider.ctor");
    }

    [OnGemReloading]
    private byte[] Save() => BitConverter.GetBytes(_service.Counter);

    [OnGemReloaded]
    private void Restore(byte[] state) => _service.Counter = BitConverter.ToInt32(state);

    public void Dispose()
    {
        TestEvents.Events.Add("Provider.dispose");
    }
}

[GemExport]
public sealed class TestService : ITestService
{
    public int Counter { get; set; }
}
