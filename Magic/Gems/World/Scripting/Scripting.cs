using Magic.Contexts;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;

namespace ScriptingGem;

/// <summary>
/// The gem: runs the scripts chunks attach to entities (<see cref="IScripting"/>), whatever language
/// they are written in. A language is a gem that loads <see cref="Magic.Contexts.Assets.Script"/> assets: it says how one is
/// made and whether it is impatient or always updated, and the script system does the rest.
/// </summary>
internal sealed class Scripting : IGem, IScripting
{
    private readonly ScriptSystem _system;
    private readonly Subscription _scheduled;

    public Scripting(IEcs ecs, Assets assets, World world)
    {
        _system = new ScriptSystem(ecs, assets, world);
        _scheduled = ecs.Add(_system);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _system.Dispose();
    }

    public void Attach(Handle entity, ulong id, Unread entry)
    {
        _system.Attach(entity, id, entry);
    }
}
