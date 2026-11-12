using Magic.Projects;

namespace Magic.Graphics;

/// <summary>
/// A gem's part of the frame on the GPU that takes nothing from other gems and makes nothing for them, on the render
/// thread, from what <c>LateUpdate</c> last handed it: as often as the GPU draws, more or less often than the game
/// thread's frames. A gem's <c>Renderer</c> part implements it. Core calls <see cref="Render"/> once a frame, after
/// every other form; the part records its passes into the frame's graph, each in its <see cref="RenderStage"/>, and
/// once every gem has, the GPU provider presents it. A part that takes or makes what other gems draw from implements
/// <see cref="IRenderFrom{TInput}"/>, <see cref="IRenderInto{TOutput}"/> or <see cref="IRender{TInput, TOutput}"/>
/// instead. A part with more than one step in the frame implements a form for each. What it cannot record it tells
/// <see cref="RenderGraph.Fail"/>, and goes on; a step that throws is a failure of the frame, and Core runs it again
/// after a pause.
/// </summary>
public interface IRender
{
    /// <summary>
    /// The gem's passes, recorded into <paramref name="graph"/>.
    /// </summary>
    void Render(in Frame frame, RenderGraph graph);
}

/// <summary>
/// A gem's part of the frame, as <see cref="IRender"/>'s, drawn from what other gems made: Core calls it after every gem
/// whose output it takes.
/// </summary>
/// <typeparam name="TInput">
/// What it takes, a record of other gems' outputs, one property each: a gem must make each non-nullable one, else it
/// renders nothing, and a nullable one is null while no listed gem makes it.
/// </typeparam>
public interface IRenderFrom<TInput>
    where TInput : struct
{
    /// <summary>
    /// The gem's passes, recorded into <paramref name="graph"/>. It isn't called on a frame a non-nullable input wasn't
    /// made in.
    /// </summary>
    void Render(in Frame frame, RenderGraph graph, in TInput input);
}

/// <summary>
/// A gem's part of the frame, as <see cref="IRender"/>'s, which makes what other gems take: Core hands what it returns
/// to every gem that takes it.
/// </summary>
/// <typeparam name="TOutput">
/// What it makes for other gems to take: one type, or a tuple of types, each made for the gems that take it.
/// </typeparam>
public interface IRenderInto<TOutput>
    where TOutput : struct
{
    /// <summary>
    /// The gem's passes, recorded into <paramref name="graph"/>, and what it made this frame: null when it made
    /// nothing, and every gem that needs it renders nothing this frame.
    /// </summary>
    TOutput? Render(in Frame frame, RenderGraph graph);
}

/// <summary>
/// A gem's part of the frame, as <see cref="IRender"/>'s, drawn from what other gems made and making what others take:
/// <see cref="IRenderFrom{TInput}"/> and <see cref="IRenderInto{TOutput}"/> in one.
/// </summary>
/// <typeparam name="TInput">As <see cref="IRenderFrom{TInput}"/>'s.</typeparam>
/// <typeparam name="TOutput">As <see cref="IRenderInto{TOutput}"/>'s.</typeparam>
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
