using Magic.Contexts.ECS;

namespace Magic.Interfaces;

/// <summary>
/// A gem that needs time with what a domain spawns before it can draw, simulate or play it as it should: a renderer
/// uploads a model and compiles its materials' pipelines, then draws the world until its light and shadows settle, a
/// physics gem shapes a body like its model, the gem that runs scripts starts them. It keeps nothing of what it is
/// asked: the world says what streams in each frame (<see cref="WorldStream"/>), the gem acts on that in its own hooks,
/// and the world asks it how far it is. A domain spawns once every such gem has what it needs, and one that replaces
/// the world, spawned out of sight, is shown once every one has readied it, as is a saved world put back; so a domain
/// is there whole in its first frame, and nothing dissolves in, stands still or starts late. A gem is one by
/// implementing it; like <see cref="ILogSink"/>, it is not provided to other gems, so any number of gems can be one.
/// </summary>
public interface IPreloader
{
    /// <summary>
    /// How far this gem has what <paramref name="stream"/>'s <see cref="WorldStream.TakingIn"/> needs before it can
    /// draw or simulate it, 0 to 1, starting whatever of it is not on its way; what does not load counts as in, drawn
    /// as the failure. Asked on the main thread as each frame begins, while anything is taken in, until every such gem
    /// says 1; worked out from what the gem has, keeping nothing of the asking.
    /// </summary>
    float GetProgress(in WorldStream stream);

    /// <summary>
    /// Whether this gem draws, simulates or plays the world being readied (<see cref="StreamState.Readying"/>) as it
    /// should from the frame it is shown. Asked on the main thread as each frame begins, once everything replacing the
    /// world has spawned, until every such gem says so.
    /// </summary>
    bool IsReady(in WorldStream stream);
}
