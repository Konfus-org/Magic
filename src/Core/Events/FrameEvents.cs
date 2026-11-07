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
    /// Every <typeparamref name="T"/> published during the last frame that no gem before this one has handled, in the
    /// order they were published; empty when there are none. Set <see cref="IEvent.IsHandled"/> through a
    /// <c>ref</c> to take one from the gems after this one.
    /// </summary>
    public Span<T> Get<T>()
        where T : struct, IEvent => _events is null ? [] : _events.GetTaken<T>();
}
