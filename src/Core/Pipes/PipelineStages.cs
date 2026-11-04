using Magic.Assets;
using Magic.Projects;
using Magic.Worlds;

namespace Magic.Pipes;

/// <summary>
/// First of a run, before the first frame: the assets indexed, the gems made, the world filling. <paramref name="KeptWorld"/>
/// is the saved world the last run kept for this one, written by now; none when it kept none.
/// </summary>
internal readonly record struct AppStart(Project Project, Handle<WorldState> KeptWorld);

/// <summary>
/// Last of a run, after the last frame, with the gems still whole.
/// </summary>
internal readonly record struct AppEnd(bool IsExiting)
{
    /// <summary>
    /// What the world's pipeline keeps for the next run when the app restarts, once written; null when it keeps none.
    /// </summary>
    public Task<Handle<WorldState>>? KeptWorld { get; init; }
}

/// <summary>
/// What keeps the services going, before each frame: what was posted to the main thread, the terminal, the assets, the
/// gems' builds.
/// </summary>
internal readonly record struct ServicePump;

/// <summary>
/// Once a frame, for the game's work: every gem's <see cref="Gems.IGem.Update"/>.
/// </summary>
internal readonly record struct Update(Frame Frame);

/// <summary>
/// Work that is deterministic, independent of the frame rate, such as physics: after <see cref="Update"/>, as many times
/// a frame as the clock owes, up to its limit, each with one step's clocks and no messages.
/// </summary>
internal readonly record struct FixedUpdate(Frame Frame);

/// <summary>
/// What follows from the frame's work: transforms, listeners, what goes back to where it came from.
/// </summary>
internal readonly record struct LateUpdate(Frame Frame);

/// <summary>
/// Drawing the frame.
/// </summary>
internal readonly record struct Render(Frame Frame);
