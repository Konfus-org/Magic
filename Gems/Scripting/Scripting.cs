using Magic.Contexts;
using Magic.Interfaces;
using Magic.Services;
using System.Text.Json;

namespace ScriptingGem;

/// <summary>
/// The gem: runs the C# scripts (<see cref="IBehavior"/>s and <see cref="ISystem"/>s) attached to entities, taking
/// the entries whose <see cref="Magic.Contexts.Assets.Script"/> asset resolves to a loaded class. Another language
/// is another gem of this shape, loaded beside it.
/// </summary>
internal sealed class Scripting : IGem, IScripting
{
    private readonly ScriptSystem _system;
    private readonly IDisposable _scheduled;

    public Scripting(IEcs ecs, Assets assets, World world, Scheduler scheduler, IServices services)
    {
        _system = new ScriptSystem(ecs, assets, world, scheduler, services);
        _scheduled = scheduler.Add(ecs, _system);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _system.Dispose();
    }

    public IDisposable? Attach(Handle entity, JsonElement script)
    {
        return _system.Attach(entity, script);
    }
}
