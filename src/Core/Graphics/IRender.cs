using Magic.Projects;

namespace Magic.Graphics;

/// <summary>
/// A gem's part of the frame on the GPU, on the render thread, from what <c>LateUpdate</c> last handed it: as often as
/// the GPU draws, more or less often than the game thread's frames. Core calls <see cref="Render"/> once a frame,
/// after every gem whose output it takes, and hands what it returns to every gem that takes it. The gem records its
/// passes into the frame's graph, each in its <see cref="RenderStage"/>; once every gem has, the GPU provider presents
/// it.
/// </summary>
/// <typeparam name="TInput">
/// What it takes, a record of other gems' outputs, one property each: a gem must make each non-nullable one, else no
/// gem renders, and a nullable one is null while no listed gem makes it. <see cref="Nothing"/> takes none.
/// </typeparam>
/// <typeparam name="TOutput">What it makes for other gems to take; <see cref="Nothing"/> makes none.</typeparam>
public interface IRender<TInput, TOutput>
    where TInput : struct
    where TOutput : struct
{
    /// <summary>
    /// The gem's passes, recorded into <paramref name="graph"/>, and what it made this frame: null when it made
    /// nothing, and every gem that needs it renders nothing this frame. It isn't called on a frame a non-nullable
    /// input wasn't made in.
    /// </summary>
    TOutput? Render(in Frame frame, RenderGraph graph, in TInput input);
}
