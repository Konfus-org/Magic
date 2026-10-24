using Magic.Contexts.Assets;
using Magic.Contexts.Utils;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Opens a domain for the samples' scripts that move the player between them, and says why when it does not open, as
/// the world only hands the reason back.
/// </summary>
internal static class SampleDomains
{
    public static async Task OpenAsync(World world, Handle<Domain> domain, DomainOpenMode mode)
    {
        Result<Domain> opened = await world.OpenAsync(domain, mode).ConfigureAwait(false);
        if (!opened.Ok) Debugging.Error($"A sample did not open: {opened.Error}");
    }
}
