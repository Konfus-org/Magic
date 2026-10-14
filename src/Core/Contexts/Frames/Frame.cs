namespace Magic.Contexts.Frames;

/// <summary>
/// What one turn of the frame loop knows about time. Every hook and system gets it by <c>in</c>.
/// </summary>
/// <param name="Index">Counts frames from 0.</param>
/// <param name="DeltaSeconds">Time since the previous frame started, 0 on the first; in FixedUpdate, the fixed step.</param>
/// <param name="Seconds">Time since the first frame started; in FixedUpdate, the time the step reaches.</param>
public readonly record struct Frame(ulong Index, float DeltaSeconds, double Seconds);
