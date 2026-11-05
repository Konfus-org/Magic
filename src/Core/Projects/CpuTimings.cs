namespace Magic.Projects;

/// <summary>
/// How long the main thread worked on a frame, in milliseconds: each hook over every gem, and the whole frame from the
/// end of one sleep to the start of the next, which also holds what runs between the hooks. <see cref="RenderMs"/>
/// includes waiting for the GPU and the display, as a submit does for an earlier frame and a free window image.
/// </summary>
public readonly record struct CpuTimings(
    double UpdateMs,
    double FixedUpdateMs,
    double LateUpdateMs,
    double RenderMs,
    double TotalMs
);
