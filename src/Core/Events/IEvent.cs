namespace Magic.Events;

/// <summary>
/// A message between gems (<see cref="EventCoordinator"/>).
/// </summary>
/// <remarks>
/// If a gem sets <see cref="IsHandled"/> on a message, the gems after it in load order do not get the message.
/// </remarks>
public interface IEvent
{
    bool IsHandled { get; set; }
}
