namespace Magic.Contexts;

/// <summary>
/// One frame, as everything sees it: its number (from 1), seconds since start, the step in seconds (the fixed step
/// inside FixedUpdate) and the events published since the previous frame (<see cref="Services.Events.Of{T}"/>). Built once
/// by the frame loop and handed down to every gem and system in turn.
/// </summary>
public readonly record struct Frame(long Number, double Time, float Delta, Services.Events Events);
