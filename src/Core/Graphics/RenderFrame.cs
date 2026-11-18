using Magic.Projects;

namespace Magic.Graphics;

/// <summary>
/// One frame as a render step sees it: its clocks, the view that the step runs for, and what failed in it.
/// </summary>
/// <remarks>
/// Core gives it to each <c>Render</c>. Use it only on the render thread, and only in the call that it was given to.
/// </remarks>
public readonly record struct RenderFrame
{
    private readonly Frame _frame;

    private readonly Rendering _rendering;

    internal RenderFrame(in Frame frame, int view, Rendering rendering)
    {
        _frame = frame;
        _rendering = rendering;
        View = view;
    }

    /// <inheritdoc cref="Frame.Index"/>
    public uint Index => _frame.Index;

    /// <inheritdoc cref="Frame.AppSec"/>
    public double AppSec => _frame.AppSec;

    /// <inheritdoc cref="Frame.AppDeltaSec"/>
    public double AppDeltaSec => _frame.AppDeltaSec;

    /// <inheritdoc cref="Frame.SimSec"/>
    public double SimSec => _frame.SimSec;

    /// <inheritdoc cref="Frame.SimDeltaSec"/>
    public double SimDeltaSec => _frame.SimDeltaSec;

    /// <inheritdoc cref="Frame.IsPaused"/>
    public bool IsPaused => _frame.IsPaused;

    /// <summary>
    /// The view that the step runs for, from 0 (<see cref="RenderViews"/>); 0 in a step that runs once a frame.
    /// </summary>
    /// <remarks>
    /// A gem keeps what it holds for each view by this.
    /// </remarks>
    public int View { get; }

    /// <summary>
    /// What failed in this frame, each one time, in the order first told to <see cref="Fail"/>.
    /// </summary>
    /// <remarks>
    /// Read it late in the frame, after the other gems have recorded.
    /// </remarks>
    public ReadOnlySpan<(RenderFailure Kind, string What)> Failures => _rendering.Failures;

    /// <summary>
    /// Whether no step makes <typeparamref name="T"/> this frame. Ask it late in the frame.
    /// </summary>
    /// <remarks>
    /// The maker is out when no gem makes it, or when its step threw and waits for a retry, lacks an input, or told a
    /// failure and made nothing.
    /// </remarks>
    public bool IsUnavailable<T>()
        where T : struct => _rendering.IsUnavailable(typeof(T));

    /// <summary>
    /// Tells that <paramref name="what"/> failed in this frame, so that it shows in the picture.
    /// </summary>
    /// <remarks>
    /// <paramref name="what"/> is a short constant text, such as "Shadows are not traced". The first failed frame after
    /// a good one logs it one time, with <paramref name="reason"/>, the full error. Tell it in each frame that fails,
    /// and try again in the next.
    /// </remarks>
    public void Fail(RenderFailure kind, string what, string reason) => _rendering.Fail(kind, what, reason);
}
