namespace Magic.Events;

/// <summary>
/// A message between gems (<see cref="EventCoordinator"/>). A gem that sets <see cref="IsHandled"/> on one it reads takes
/// it: the gems after it in load order don't get it.
/// </summary>
public interface IEvent
{
    bool IsHandled { get; set; }
}
