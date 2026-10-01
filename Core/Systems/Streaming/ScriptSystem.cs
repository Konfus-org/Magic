using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Events;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Magic.Systems.Streaming;

/// <summary>
/// Runs the scripts chunks attach to entities. The streaming system hands over each entity's <c>scripts</c> as it
/// spawns it; here every entry becomes one instance of its <see cref="Script"/>'s class, constructed as a gem is
/// (constructor parameters from the <see cref="Container"/>, plus the entity's <see cref="Handle"/>) and then filled
/// from the entry's other values by the serializer. An <see cref="ISystem"/> goes on the <see cref="Scheduler"/>; an
/// <see cref="IBehavior"/> has its hooks called here, once per phase. The instances end with their entity (its
/// <see cref="Scripts"/> going says so), and when gems change all of them are made again from the classes there are
/// now: that is how a rebuilt project's scripts reload. It runs in Update, after streaming.
/// </summary>
internal sealed class ScriptSystem : ISystem
{
    private static readonly DefaultJsonTypeInfoResolver Resolver = new();

    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly Scheduler _scheduler;
    private readonly Container _container;
    private readonly Dictionary<Handle, List<Attached>> _attached = [];
    private readonly HashSet<Handle> _pending = []; // entities with scripts still to be made
    private readonly List<Handle> _removed = []; // filled by the observer, which must not change anything itself
    private readonly List<Attached> _faulted = [];
    private readonly IDisposable _observer;
    private readonly IDisposable[] _hooks;

    public ScriptSystem(IEcs ecs, Assets assets, Scheduler scheduler, Container container)
    {
        _ecs = ecs;
        _assets = assets;
        _scheduler = scheduler;
        _container = container;
        _observer = ecs.Observe<Scripts>(ComponentEvent.Removed, _removed.Add);
        _hooks =
        [
            scheduler.Add(ecs, new Hook(this, UpdateType.FixedUpdate)),
            scheduler.Add(ecs, new Hook(this, UpdateType.LateUpdate)),
            scheduler.Add(ecs, new Hook(this, UpdateType.Render))
        ];
    }

    public void Dispose()
    {
        foreach (IDisposable hook in _hooks)
            hook.Dispose();

        _observer.Dispose();
        foreach (List<Attached> scripts in _attached.Values)
            scripts.ForEach(End);

        _attached.Clear();
        _pending.Clear();
        _removed.Clear();
    }

    /// <summary>
    /// Gives <paramref name="entity"/> the scripts a chunk lists for it, one JSON object each: the <c>id</c> of the
    /// script asset and the instance's values. They are made in this system's next run.
    /// </summary>
    internal void Attach(Handle entity, JsonElement[] scripts)
    {
        if (scripts.Length == 0)
            return;

        if (!_attached.TryGetValue(entity, out List<Attached>? attached))
            _attached[entity] = attached = [];

        attached.AddRange(scripts.Select(entry => new Attached(entry)));
        _pending.Add(entity);
        _ecs.Add<Scripts>(entity);
    }

    public void Run(in Frame frame)
    {
        foreach (Event change in frame.Events.Span)
        {
            if (change.Type != EventType.GemsChanged)
                continue;

            Remake();
            break;
        }

        Call(UpdateType.Update, frame);
    }

    /// <summary>Ends every instance and queues its entity: the class it was made from may be of an assembly that just went.</summary>
    private void Remake()
    {
        foreach ((Handle entity, List<Attached> scripts) in _attached)
        {
            scripts.ForEach(End);
            _pending.Add(entity);
        }
    }

    /// <summary>One phase: entities that went give up their scripts, new scripts are made, then every behaviour gets the phase's hook.</summary>
    private void Call(UpdateType phase, in Frame frame)
    {
        ForgetRemoved();
        MakePending();

        foreach (List<Attached> scripts in _attached.Values)
        {
            foreach (Attached attached in scripts)
            {
                if (attached.Instance is not IBehavior behavior)
                    continue;

                try
                {
                    switch (phase)
                    {
                        case UpdateType.Update:
                            behavior.Update(frame);
                            break;
                        case UpdateType.FixedUpdate:
                            behavior.FixedUpdate(frame);
                            break;
                        case UpdateType.LateUpdate:
                            behavior.LateUpdate(frame);
                            break;
                        case UpdateType.Render:
                            behavior.Render(frame);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Said once: the script is ended rather than left to throw every frame.
                    Debugging.Log.Error($"Script {behavior.GetType().Name} threw in {phase} and was stopped. {ex}");
                    _faulted.Add(attached);
                }
            }
        }

        _faulted.ForEach(End);
        _faulted.Clear();
    }

    private void ForgetRemoved()
    {
        foreach (Handle entity in _removed)
        {
            if (_attached.Remove(entity, out List<Attached>? scripts))
                scripts.ForEach(End);

            _pending.Remove(entity);
        }

        _removed.Clear();
    }

    private void MakePending()
    {
        if (_pending.Count == 0)
            return;

        foreach (Handle entity in _pending)
        {
            foreach (Attached attached in _attached[entity])
            {
                if (attached.Instance is null)
                    Make(entity, attached);
            }
        }

        _pending.Clear();
    }

    /// <summary>
    /// The entry's script, loaded and constructed; one that cannot be is said why and left unmade until gems change
    /// (a rebuilt project may have fixed it).
    /// </summary>
    private void Make(Handle entity, Attached attached)
    {
        if (!attached.Entry.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt64(out ulong asset))
        {
            Debugging.Log.Error($"A script on {_ecs.GetName(entity) ?? entity.ToString()} has no id: {attached.Entry.GetRawText()}");
            return;
        }

        if (_assets.Load(new Handle<Script>(asset))?.Type is not { } type)
            return; // the asset manager logged why

        try
        {
            // The serializer makes the instance through the host's injection, then sets the entry's values on it.
            JsonTypeInfo info = Resolver.GetTypeInfo(type, AssetJson.Options);
            info.CreateObject = () => type.Create(_container, entity);
            attached.Instance = (IScript)JsonSerializer.Deserialize(attached.Entry, info)!;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debugging.Log.Error($"Script {type.Name} on {_ecs.GetName(entity) ?? entity.ToString()} could not be made: {ex.Message}");
            return;
        }

        if (attached.Instance is ISystem system)
            attached.Scheduled = _scheduler.Add(_ecs, system);
    }

    /// <summary>Takes the instance off the schedule and disposes it; the entry stays, to be made again.</summary>
    private static void End(Attached attached)
    {
        attached.Scheduled?.Dispose();
        attached.Scheduled = null;

        try
        {
            attached.Instance?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debugging.Log.Warn($"Script {attached.Instance?.GetType().Name}: Dispose failed. {ex}");
        }

        attached.Instance = null;
    }

    /// <summary>One script of one entity: the chunk's entry, and the instance made from it while there is one.</summary>
    private sealed class Attached(JsonElement entry)
    {
        public JsonElement Entry { get; } = entry;

        public IScript? Instance { get; set; }

        /// <summary>An <see cref="ISystem"/>'s place on the schedule.</summary>
        public IDisposable? Scheduled { get; set; }
    }

    /// <summary>The behaviours' hook for one phase besides Update, which is this system's own.</summary>
    private sealed class Hook(ScriptSystem scripts, UpdateType phase) : ISystem
    {
        public UpdateType Phase => phase;

        public void Run(in Frame frame)
        {
            scripts.Call(phase, frame);
        }
    }
}
