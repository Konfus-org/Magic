using Magic.Attributes.Scripts;
using Magic.Contexts;
using Magic.Interfaces;
using System.Reflection;

namespace Magic.Services;

/// <summary>
/// Where every <see cref="ISystem"/> is added. It keeps no list of its own: a system is scheduled on the
/// <see cref="IEcs"/> it is added with, which runs it in its phase, in the order the systems were added. The frame
/// loop tells it the frame at the top of each one, and that is the frame the systems are handed. Main thread only.
/// </summary>
public sealed class Scheduler
{
    private readonly Dictionary<string, int> _added = []; // by type name, to tell two of one type apart
    private Frame _currentFrame;

    internal Scheduler()
    {
    }

    /// <summary>
    /// Schedules <paramref name="system"/> on <paramref name="ecs"/> in the phase its class is marked with by
    /// <see cref="PhaseAttribute"/>, <see cref="UpdateType.Update"/> when it is not, after those already added.
    /// Disposing the handle takes it off the schedule; the system itself stays the caller's to dispose. The ECS
    /// knows it by its type name, numbered from the second of a type on: one name is one system there.
    /// </summary>
    public IDisposable Add(IEcs ecs, ISystem system)
    {
        string name = system.GetType().Name;
        int count = _added[name] = _added.GetValueOrDefault(name) + 1;
        if (count > 1)
            name = $"{name}_{count}";

        UpdateType phase = system.GetType().GetCustomAttribute<PhaseAttribute>()?.Phase ?? UpdateType.Update;
        return ecs.Schedule(name).On(phase).Run(dt => system.Run(_currentFrame with { Delta = dt }));
    }

    /// <summary>
    /// The frame the systems are handed from here on.
    /// </summary>
    internal void SetFrame(in Frame frame)
    {
        _currentFrame = frame;
    }
}
