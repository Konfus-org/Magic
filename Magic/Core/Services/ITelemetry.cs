namespace Magic.Interfaces;

/// <summary>
/// The engine's numbers: a system <see cref="Set"/>s one under a dotted name (<c>Frame.Time.RenderSyncMs</c>,
/// <c>Memory.Assets.Texture.UsedMB</c>) as often as it changes, from any thread, and the debug display shows them.
/// The first part names the tab, the last the number. Nothing is computed here: a rate or an average is the caller's.
/// </summary>
public interface ITelemetry
{
    void Set(string name, double value);
}
