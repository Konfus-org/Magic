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
/// <see cref="UpdateType"/> until the handle returned by <see cref="Schedule"/> is disposed. A gem keeps that
/// handle and disposes it in its own Dispose; the scheduler knows nothing about gems.
/// </summary>
public sealed class SystemScheduler
{
    private readonly Dictionary<UpdateType, List<Registration>> _systems = new()
    {
        [UpdateType.Update] = [],
        [UpdateType.FixedUpdate] = [],
        [UpdateType.LateUpdate] = [],
    };
    private readonly Lock _lock = new();

    /// <summary>Runs <paramref name="system"/> every tick of <paramref name="when"/>, in registration order.</summary>
    public IDisposable Schedule(Action<double> system, UpdateType when = UpdateType.Update)
    {
        Registration registration = new(this, system, when);
        lock (_lock)
        {
            _systems[when].Add(registration);
        }
        return registration;
    }

    public void Update(double dt)
    {
        Process(UpdateType.Update, dt);
    }

    public void FixedUpdate(double dt)
    {
        Process(UpdateType.FixedUpdate, dt);
    }

    public void LateUpdate(double dt)
    {
        Process(UpdateType.LateUpdate, dt);
    }

    /// <summary>Runs every system registered for <paramref name="when"/> once. Called by the main loop.</summary>
    private void Process(UpdateType when, double deltaTime)
    {
        Registration[] snapshot;
        lock (_lock)
        {
            snapshot = [.. _systems[when]]; // systems may (un)register while running
        }

        foreach (Registration registration in snapshot)
        {
            try
            {
                registration.System(deltaTime);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debugging.LogError($"A {when} system threw and was skipped this frame. {ex}");
            }
        }
    }

    private void Unschedule(Registration registration)
    {
        lock (_lock)
        {
            _systems[registration.When].Remove(registration);
        }
    }

    private sealed class Registration(SystemScheduler owner, Action<double> system, UpdateType when) : IDisposable
    {
        public Action<double> System { get; } = system;
        public UpdateType When { get; } = when;

        public void Dispose()
        {
            owner.Unschedule(this);
        }
    }
}
