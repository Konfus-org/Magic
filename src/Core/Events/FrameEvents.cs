namespace Magic.Events;

/// <summary>
/// The messages published during the last frame, which the current frame reads (<see cref="EventCoordinator"/>).
/// </summary>
/// <remarks>
/// Use it only in the current frame. The take of the next frame reuses its memory.
/// </remarks>
public readonly record struct FrameEvents
{
    private readonly EventCoordinator? _events;

    internal FrameEvents(EventCoordinator events) => _events = events;

    /// <summary>
    /// No messages, for a step that must not read a message twice, such as a fixed step that runs many times in a
    /// frame.
    /// </summary>
    public static FrameEvents None => default;

    /// <summary>
    /// Gets each <typeparamref name="T"/> published in the last frame that no earlier gem handled, in publish order.
    /// </summary>
    /// <remarks>
    /// To keep a message from the gems after this one, set <see cref="IEvent.IsHandled"/> through a <c>ref</c>.
    /// </remarks>
    public Span<T> Get<T>()
        where T : struct, IEvent => _events is null ? [] : _events.GetTaken<T>();
}
