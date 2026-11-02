using Magic.GemAPI;
using Magic.Runtime;

namespace Magic.Pipelines;

/// <summary>
/// Once, every gem made, before the first frame. Awaited: frames don't run until every one has ended, though what it
/// awaits does (<see cref="HostPump"/>).
/// </summary>
public readonly record struct Startup(Project Project);

/// <summary>
/// What keeps the host going, before each frame and while <see cref="Startup"/> or <see cref="Shutdown"/> are awaited, when
/// no frames run: what was posted to the main thread, the terminal, the assets. A gem whose work outside the frames must
/// go on then too, such as a window's, joins here.
/// </summary>
public readonly record struct HostPump;

/// <summary>
/// First in a frame, with the frame's messages: for what the frame's work reads, such as input.
/// </summary>
public readonly record struct BeginUpdate(Frame Frame);

/// <summary>
/// Once a frame, for the game's work.
/// </summary>
public readonly record struct Update(Frame Frame);

/// <summary>
/// Work that is deterministic, independent of the frame rate, such as physics: after
/// <see cref="Update"/>, as many times a frame as the clock owes, up to its limit, each with one step's
/// clocks and no messages.
/// </summary>
public readonly record struct FixedUpdate(Frame Frame);

/// <summary>
/// What follows from the frame's work: transforms, listeners, what goes back to where it came from.
/// </summary>
public readonly record struct LateUpdate(Frame Frame);

/// <summary>
/// Drawing the frame.
/// </summary>
public readonly record struct Render(Frame Frame);

/// <summary>
/// Last in a frame's work, before the wait for the next frame.
/// </summary>
public readonly record struct EndUpdate(Frame Frame);

/// <summary>
/// Once, after the last frame, before the gems end, last gem first. Awaited, as <see cref="Startup"/> is.
/// </summary>
public readonly record struct Shutdown(bool IsExiting);
