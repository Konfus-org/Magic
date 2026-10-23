using Magic.Attributes;
using Magic.Contexts.ECS;
using Magic.Services;

namespace Magic.Contexts.Frames;

/// <summary>
/// What one turn of the frame loop knows about time. Every hook and system gets it by <c>in</c>. It holds two clocks:
/// the world's, which <see cref="World.TimeScale"/> speeds up or slows down and which stands still while the world is
/// paused, and the real one, which runs as a clock on the wall does, whatever the world does. Game code moves by the
/// world's; what moves while the world is paused, such as a camera flying or a menu, by the real one. Neither runs while
/// the app is paused (<see cref="App.IsPaused"/>), as no frame does.
/// </summary>
/// <param name="Index">Counts frames from 0.</param>
/// <param name="WorldDeltaSec">The world's time since the previous frame, 0 on the first and while the world is
/// paused; in FixedUpdate, the fixed step.</param>
/// <param name="WorldSec">The world's time since the first frame; in FixedUpdate, the time the step reaches.</param>
/// <param name="RealDeltaSec">Real time since the previous frame started, 0 on the first.</param>
/// <param name="RealSec">Real time since the first frame started.</param>
/// <param name="IsPaused">Whether the world is paused this frame (<see cref="World.IsPaused"/>): there are no fixed
/// steps, and of the scripts and systems only those that are <see cref="UnpausableAttribute">[Unpausable]</see>, or a
/// script on an entity tagged <see cref="Tag.Unpausable"/>, run.</param>
public readonly record struct Frame(
    ulong Index,
    float WorldDeltaSec,
    double WorldSec,
    float RealDeltaSec,
    double RealSec,
    bool IsPaused);
