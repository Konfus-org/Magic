using Magic.Interfaces;

namespace Magic.Services;

/// <summary>
/// The engine's numbers in one place: whatever a system wants shown is <see cref="Set"/> under a dotted name,
/// from any thread, as often as it changes; the debug display takes them all and prints them. The first part is
/// the tab (<c>Frame</c>, <c>Memory</c>, <c>Rendering</c>, <c>Streaming</c>), the last the name, and anything between
/// is a sub-category shown as a block within the tab: <c>Rendering.Draws</c>, <c>Frame.Time.RenderSyncMs</c>,
/// <c>Memory.Assets.Texture.UsedMB</c>, <c>Memory.GC.HeapMB</c>. A tab holds what it is about, whoever sets it:
/// every per-frame timing goes under <c>Frame.Time</c>, every memory figure under <c>Memory</c>, in megabytes. Nothing
/// here is computed: a rate or an average is the caller's to make before setting it. Gems set through
/// <see cref="ITelemetry"/> (the debug tools set the frame's and the GC's own); the stats window reads this.
/// </summary>
public sealed class Telemetry : ITelemetry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, double> _values = [];

    public void Set(string name, double value)
    {
        lock (_lock)
            _values[name] = value;
    }

    /// <summary>
    /// Every value set, name-ordered, added to <paramref name="into"/>.
    /// </summary>
    public void CopyTo(List<(string Name, double Value)> into)
    {
        lock (_lock)
        {
            foreach ((string name, double value) in _values)
                into.Add((name, value));
        }

        into.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
    }
}
