using Magic.Utils;

namespace Magic.Services;

public enum UpdateType
{
    Update,
    FixedUpdate,
    LateUpdate
}

/// <summary>
/// Systems that run every frame. A system is registered once and runs on every tick of its
/// <see cref="UpdateType"/> until the handle returned by <see cref="Register"/> is disposed.
/// Gems must dispose their handles when they unload (their <c>Dispose</c> is the natural place), or the
/// registry keeps a delegate into the unloaded assembly alive.
/// </summary>
public sealed class SystemRegistry
{
    private readonly Dictionary<UpdateType, List<Registration>> _systems = new()
    {
        [UpdateType.Update] = [],
        [UpdateType.FixedUpdate] = [],
        [UpdateType.LateUpdate] = [],
    };
    private readonly object _lock = new();

    /// <summary>Runs <paramref name="system"/> every tick of <paramref name="when"/>, in registration order.</summary>
    public IDisposable Register(Action<double> system, UpdateType when = UpdateType.Update)
    {
        Registration registration = new(this, system, when);
        lock (_lock)
        {
            _systems[when].Add(registration);
        }
        return registration;
    }

    /// <summary>Runs every system registered for <paramref name="when"/> once. Called by the main loop.</summary>
    internal void Run(UpdateType when, double deltaTime)
    {
        Registration[] snapshot;
        lock (_lock)
        {
            snapshot = _systems[when].ToArray(); // systems may (un)register while running
        }

        foreach (Registration registration in snapshot)
        {
            try
            {
                registration.System(deltaTime);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error($"A {when} system threw and was skipped this frame. {ex}");
            }
        }
    }

    private void Unregister(Registration registration)
    {
        lock (_lock)
        {
            _systems[registration.When].Remove(registration);
        }
    }

    private sealed class Registration(SystemRegistry owner, Action<double> system, UpdateType when) : IDisposable
    {
        public Action<double> System { get; } = system;
        public UpdateType When { get; } = when;

        public void Dispose()
        {
            owner.Unregister(this);
        }
    }
}
