namespace Magic.Events;

/// <summary>
/// The messages published during the last frame, as this frame reads them (<see cref="EventCoordinator"/>). Good for
/// this frame only: the next frame's take reuses what they are read from.
/// </summary>
public readonly record struct FrameEvents
{
    private readonly EventCoordinator? _events;

    internal FrameEvents(EventCoordinator events) => _events = events;

    /// <summary>
    /// No messages: what a fixed step reads, so one running several times a frame never hears a message twice.
    /// </summary>
    public static FrameEvents None => default;

    /// <summary>
    /// Every <typeparamref name="T"/> published during the last frame, in the order they were; empty when there were
    /// none.
    /// </summary>
    public ReadOnlySpan<T> Get<T>()
        where T : struct => _events is null ? [] : _events.GetTaken<T>();
}
