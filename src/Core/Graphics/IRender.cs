using Magic.Projects;

namespace Magic.Graphics;

/// <summary>
/// A gem's step of the GPU frame that takes nothing from other gems and makes nothing for them.
/// </summary>
/// <remarks>
/// <para>
/// Core calls <see cref="Render"/> on the render thread once a frame, after every other form, from what
/// <c>LateUpdate</c> last gave. The GPU frame can run more or less often than the game thread's frames.
/// </para>
/// <para>
/// A step that takes or makes what other gems draw from implements <see cref="IRenderFrom{TInput}"/>,
/// <see cref="IRenderInto{TOutput}"/> or <see cref="IRender{TInput, TOutput}"/> instead. A part with more than one
/// step implements a form for each.
/// </para>
/// <para>
/// Tell <see cref="RenderGraph.Fail"/> what cannot be recorded, and go on. A step that throws fails the frame, and
/// Core runs it again after a pause.
/// </para>
/// </remarks>
public interface IRender
{
    /// <summary>
    /// Records the gem's passes into <paramref name="graph"/>, each in its <see cref="RenderStage"/>.
    /// </summary>
    void Render(in Frame frame, RenderGraph graph);
}

/// <summary>
/// A gem's step of the GPU frame, as <see cref="IRender"/>, that draws from what other gems made.
/// </summary>
/// <remarks>
/// Core calls it after each gem whose output it takes.
/// </remarks>
/// <typeparam name="TInput">
/// A record of other gems' outputs, one property each. Without each non-nullable one the step does not run. A
/// nullable one is null while no gem makes it.
/// </typeparam>
public interface IRenderFrom<TInput>
    where TInput : struct
{
    /// <summary>
    /// Records the gem's passes into <paramref name="graph"/>. Core does not call it in a frame that lacks a
    /// non-nullable input.
    /// </summary>
    void Render(in Frame frame, RenderGraph graph, in TInput input);
}

/// <summary>
/// A gem's step of the GPU frame, as <see cref="IRender"/>, that makes what other gems take.
/// </summary>
/// <remarks>
/// Core gives what it returns to each gem that takes it.
/// </remarks>
/// <typeparam name="TOutput">
/// What it makes for other gems: one type, or a tuple of types, each given to the gems that take it.
/// </typeparam>
public interface IRenderInto<TOutput>
    where TOutput : struct
{
    /// <summary>
    /// Records the gem's passes into <paramref name="graph"/> and returns what it made this frame.
    /// </summary>
    /// <remarks>
    /// Return null when it made nothing. Then no gem that needs it renders this frame.
    /// </remarks>
    TOutput? Render(in Frame frame, RenderGraph graph);
}

/// <summary>
/// A gem's step of the GPU frame: <see cref="IRenderFrom{TInput}"/> and <see cref="IRenderInto{TOutput}"/> in one.
/// </summary>
/// <typeparam name="TInput">As <see cref="IRenderFrom{TInput}"/>'s.</typeparam>
/// <typeparam name="TOutput">As <see cref="IRenderInto{TOutput}"/>'s.</typeparam>
public interface IRender<TInput, TOutput>
    where TInput : struct
    where TOutput : struct
{
    /// <summary>
    /// Records the gem's passes into <paramref name="graph"/> and returns what it made this frame.
    /// </summary>
    /// <remarks>
    /// Return null when it made nothing. Then no gem that needs it renders this frame. Core does not call it in a frame
    /// that lacks a non-nullable input.
    /// </remarks>
    TOutput? Render(in Frame frame, RenderGraph graph, in TInput input);
}
