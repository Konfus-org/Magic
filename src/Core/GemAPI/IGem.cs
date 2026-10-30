namespace Magic.GemAPI;

/// <summary>
/// A gem synonymous with a plugin.
/// </summary>
public interface IGem
{
    /// <summary>
    /// Called once a frame.
    /// </summary>
    void Tick(in Frame frame) { }

    /// <summary>
    /// Used for any logic that needs to be updated in a deterministic way independent of frame rate. This can be called
    /// several times a frame and is garunteed to run 60 times a second (unless the framereate is very low as we max out
    /// at 4 times a frame). Called after <seealso cref="Tick"/>.
    /// </summary>
    void DeterministicTick(in Frame frame) { }

    /// <summary>
    /// Called on render thread after <seealso cref="Tick"/> and <seealso cref="DeterministicTick"/>.
    /// </summary>
    void Render(in Frame frame) { }
}
