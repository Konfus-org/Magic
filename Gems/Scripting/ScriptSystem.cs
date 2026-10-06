using Magic.Attributes.Scripts;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Domain;
using Magic.Contexts.Events;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ScriptingGem;

/// <summary>
/// Runs the scripts chunks attach to entities. Whoever spawns an entity offers each of its <c>scripts</c> entries
/// (<see cref="Attach"/>); every one taken becomes one instance of its <see cref="Script"/>'s class, constructed as
/// a gem is (constructor parameters from the <see cref="IServices"/>, plus the entity's <see cref="Handle"/>) and then filled
/// from the entry's other values by the serializer. An <see cref="ISystem"/> goes on the <see cref="Scheduler"/>; an
/// <see cref="IBehavior"/> has its hooks called here, once per phase. The instances end with their entity (its
/// <see cref="Scripts"/> going says so) or when what took them is disposed, and when gems change all of them are made
/// again from the classes there are now: that is how a rebuilt project's scripts reload. It runs in Update.
/// <para>
/// A behaviour far from every camera is updated less often: Update and LateUpdate run every frame within
/// <see cref="FullRateDistance"/> metres of the nearest camera, then every 2nd, 4th, 8th frame as that distance
/// doubles, up to every <see cref="MaxInterval"/>th. A call that was waited for gets the time since the last one as
/// its frame's delta, so what moves by delta moves as far. FixedUpdate and Render are never held back, nor is an
/// entity without a <see cref="WorldTransform"/> (it is nowhere), nor a class marked <see cref="AlwaysUpdateAttribute"/>.
/// </para>
/// <para>
/// While the world is loading behind the loading domain (<see cref="World.Loading"/> is open) scripts are held: they
/// are not made until it is there, but for the classes marked <see cref="ImpatientAttribute"/>, which are made
/// and run as their entity spawns: the loading screen's own, and anything else that runs while the world loads.
/// </para>
/// </summary>
internal sealed class ScriptSystem : ISystem
{
    /// <summary>
    /// Metres from the nearest camera within which a behaviour is updated every frame.
    /// </summary>
    private const float FullRateDistance = 64f;

    /// <summary>
    /// The most frames between two updates of a behaviour, however far it is.
    /// </summary>
    private const int MaxInterval = 32;

    private static readonly DefaultJsonTypeInfoResolver Resolver = new();

    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly Scheduler _scheduler;
    private readonly World _world;
    private readonly IServices _services;
    private readonly IEcsQuery<Camera, WorldTransform> _cameras;
    private readonly List<Vector3> _cameraPositions = []; // as of this frame's Update
    private readonly Dictionary<Handle, List<Attached>> _attached = [];
    private readonly HashSet<Handle> _pending = []; // entities with scripts still to be made
    private readonly Predicate<Handle> _makeAll; // one delegate, not one per frame
    private readonly List<Handle> _removed = []; // filled by the observer, which must not change anything itself
    private readonly List<Attached> _faulted = [];
    private readonly IDisposable _observer;
    private readonly Disposables<Hook> _hooks;
    private readonly Disposables<IDisposable> _scheduled;

    public ScriptSystem(IEcs ecs, Assets assets, World world, Scheduler scheduler, IServices services)
    {
        _makeAll = MakeAll;
        _ecs = ecs;
        _assets = assets;
        _world = world;
        _scheduler = scheduler;
        _services = services;
        _cameras = ecs.Query<Camera, WorldTransform>().Build();
        _observer = ecs.Observe<Scripts>(ComponentEvent.Removed, _removed.Add);
        _hooks = new Disposables<Hook>(new FixedUpdateHook(this), new LateUpdateHook(this), new RenderHook(this));
        _scheduled = new Disposables<IDisposable>([.. _hooks.Select(hook => scheduler.Add(ecs, hook))]);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _hooks.Dispose();

        _observer.Dispose();
        _cameras.Dispose();
        foreach (List<Attached> scripts in _attached.Values)
            scripts.ForEach(End);

        _attached.Clear();
        _pending.Clear();
        _removed.Clear();
    }

    /// <summary>
    /// Whether the world is still loading behind the loading domain: scripts wait while it is, but for the classes
    /// marked <see cref="ImpatientAttribute"/>.
    /// </summary>
    private bool Held => _world.Loading.IsValid && _world.StateOf(_world.Loading) != DomainState.Closed;

    /// <summary>
    /// Takes the entry when its script asset is a C# one (a <c>.cs</c> file, what the C# script loader resolves):
    /// the <c>id</c> of the script asset and the instance's values, one JSON object. It is made in this system's
    /// next run, once the world is there unless its class is marked <see cref="ImpatientAttribute"/>. Disposing
    /// the answer ends the instance; so does the entity going.
    /// </summary>
    internal IDisposable? Attach(Handle entity, JsonElement script)
    {
        if (!script.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt64(out ulong asset))
            return null;

        if (_assets.PathOf(asset) is not { } path || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!_attached.TryGetValue(entity, out List<Attached>? attached))
            _attached[entity] = attached = [];

        Attached entry = new(script);
        attached.Add(entry);
        _pending.Add(entity);
        _ecs.Add<Scripts>(entity);

        return new Subscription(() => Detach(entity, entry));
    }

    /// <summary>
    /// Ends one script of the entity; the last one going takes the <see cref="Scripts"/> marker with it.
    /// </summary>
    private void Detach(Handle entity, Attached script)
    {
        if (!_attached.TryGetValue(entity, out List<Attached>? attached) || !attached.Remove(script))
            return;

        End(script);
        if (attached.Count > 0)
            return;

        _attached.Remove(entity);
        _pending.Remove(entity);
        if (_ecs.IsAlive(entity) && _ecs.Has<Scripts>(entity))
            _ecs.Remove<Scripts>(entity);
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

        _cameraPositions.Clear();
        _cameras.Run((ReadOnlySpan<Handle> _, Span<Camera> _, Span<WorldTransform> worlds) =>
        {
            foreach (WorldTransform world in worlds)
                _cameraPositions.Add(world.Value.Translation);
        });

        Call(UpdateType.Update, frame);
    }

    /// <summary>
    /// How many frames apart the entity's behaviours are updated: 1 (every frame) near a camera, doubling with the
    /// distance to the nearest one. 1 too when there is no camera, or the entity has no place in the world.
    /// </summary>
    private int Interval(Handle entity)
    {
        if (_cameraPositions.Count == 0 || !_ecs.TryGet<WorldTransform>(entity, out WorldTransform world))
            return 1;

        Vector3 position = world.Value.Translation;
        float nearest = float.MaxValue;
        foreach (Vector3 camera in _cameraPositions)
            nearest = MathF.Min(nearest, Vector3.DistanceSquared(position, camera));

        int interval = 1;
        for (float reach = FullRateDistance; interval < MaxInterval && nearest > reach * reach; reach *= 2f)
            interval *= 2;

        return interval;
    }

    /// <summary>
    /// Ends every instance and queues its entity: the class it was made from may be of an assembly that just went.
    /// </summary>
    private void Remake()
    {
        foreach ((Handle entity, List<Attached> scripts) in _attached)
        {
            scripts.ForEach(End);
            _pending.Add(entity);
        }
    }

    /// <summary>
    /// One phase: entities that went give up their scripts, new scripts are made, then every behaviour gets the phase's hook.
    /// </summary>
    private void Call(UpdateType phase, in Frame frame)
    {
        ForgetRemoved();
        MakePending();

        foreach ((Handle entity, List<Attached> scripts) in _attached)
        {
            // Entities take turns by their id, so the far ones do not all fall on one frame.
            bool paced = phase is UpdateType.Update or UpdateType.LateUpdate;
            bool due = !paced || (frame.Number + (uint)entity.Id) % Interval(entity) == 0;

            foreach (Attached attached in scripts)
            {
                if (attached.Instance is not IBehavior behavior)
                    continue;

                try
                {
                    switch (phase)
                    {
                        case UpdateType.Update when !due && !attached.Always:
                            attached.UpdateWaited += frame.Delta;
                            break;
                        case UpdateType.Update:
                            behavior.Update(frame with { Delta = frame.Delta + attached.UpdateWaited });
                            attached.UpdateWaited = 0f;
                            break;
                        case UpdateType.FixedUpdate:
                            behavior.FixedUpdate(frame);
                            break;
                        case UpdateType.LateUpdate when !due && !attached.Always:
                            attached.LateUpdateWaited += frame.Delta;
                            break;
                        case UpdateType.LateUpdate:
                            behavior.LateUpdate(frame with { Delta = frame.Delta + attached.LateUpdateWaited });
                            attached.LateUpdateWaited = 0f;
                            break;
                        case UpdateType.Render:
                            behavior.Render(frame);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Said once: the script is ended rather than left to throw every frame.
                    Debugging.Log.Error($"Script {behavior.GetType().Name} threw in {phase}. {ex}");
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

    /// <summary>
    /// Makes the scripts whose assets have arrived; an entity with one still loading is looked at again next time.
    /// </summary>
    private void MakePending()
    {
        if (_pending.Count > 0)
            _pending.RemoveWhere(_makeAll);
    }

    /// <summary>
    /// True once every script of the entity has been made, or cannot be.
    /// </summary>
    private bool MakeAll(Handle entity)
    {
        if (!_attached.TryGetValue(entity, out List<Attached>? scripts))
            return true;

        bool done = true;
        foreach (Attached attached in scripts)
        {
            if (attached.Instance is null)
                done &= TryMake(entity, attached);
        }

        return done;
    }

    /// <summary>
    /// The entry's script, constructed once its asset has loaded, which is begun here and not waited for: false
    /// while it is still loading, or while its entity is held and its class is not one that runs on loading. One
    /// that cannot be made is said why and left unmade until gems change (a rebuilt project may have fixed it).
    /// </summary>
    private bool TryMake(Handle entity, Attached attached)
    {
        if (attached.Type is { } known)
            return !Held && TryMake(entity, attached, known);

        if (attached.Loading is null)
        {
            if (!attached.Entry.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt64(out ulong asset))
            {
                Debugging.Log.Error($"A script on {_ecs.GetName(entity) ?? entity.ToString()} has no id: {attached.Entry.GetRawText()}");
                return true;
            }

            attached.Loading = _assets.LoadAsync(new Handle<Script>(asset));
        }

        if (!attached.Loading.IsCompleted)
            return false;

        Task<Script?> loading = attached.Loading;
        attached.Loading = null;
        if (!loading.IsCompletedSuccessfully)
        {
            Debugging.Log.Error($"A script on {_ecs.GetName(entity) ?? entity.ToString()} could not be loaded: {loading.Exception?.GetBaseException().Message}");
            return true;
        }

        if (loading.Result?.Type is not { } type)
            return true; // the asset manager logged why

        if (Held && !type.IsDefined(typeof(ImpatientAttribute), inherit: true))
        {
            attached.Type = type; // made when its domain is there
            return false;
        }

        return TryMake(entity, attached, type);
    }

    /// <summary>
    /// Constructs the entry's script from its class; true whether or not that worked: it is not tried again.
    /// </summary>
    private bool TryMake(Handle entity, Attached attached, Type type)
    {
        attached.Type = null;
        try
        {
            // The serializer makes the instance through the host's injection, then sets the entry's values on it.
            JsonTypeInfo info = Resolver.GetTypeInfo(type, AssetJson.Options);
            info.CreateObject = () => type.Create(_services, entity);
            if (JsonSerializer.Deserialize(attached.Entry, info) is not IScript instance)
            {
                Debugging.Log.Error($"Script {type.Name} on {_ecs.GetName(entity) ?? entity.ToString()} could not be made: its entry is null.");
                return true;
            }

            attached.Instance = instance;
            attached.Always = type.IsDefined(typeof(AlwaysUpdateAttribute), inherit: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debugging.Log.Error($"Script {type.Name} on {_ecs.GetName(entity) ?? entity.ToString()} could not be made: {ex.Message}");
            return true;
        }

        if (attached.Instance is ISystem system)
            attached.Scheduled = _scheduler.Add(_ecs, system);

        return true;
    }

    /// <summary>
    /// Takes the instance off the schedule and disposes it; the entry stays, to be made again.
    /// </summary>
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
        attached.Loading = null;
        attached.Type = null;
        attached.UpdateWaited = 0f;
        attached.LateUpdateWaited = 0f;
    }

    /// <summary>
    /// One script of one entity: the chunk's entry, and the instance made from it while there is one.
    /// </summary>
    private sealed class Attached(JsonElement entry)
    {
        public JsonElement Entry { get; } = entry;

        public IScript? Instance { get; set; }

        /// <summary>
        /// Its script asset on the way, from when it is first to be made until it has arrived.
        /// </summary>
        public Task<Script?>? Loading { get; set; }

        /// <summary>
        /// Its class, found and waiting to be made: its entity is held.
        /// </summary>
        public Type? Type { get; set; }

        /// <summary>
        /// An <see cref="ISystem"/>'s place on the schedule.
        /// </summary>
        public IDisposable? Scheduled { get; set; }

        /// <summary>
        /// Its class is marked <see cref="AlwaysUpdateAttribute"/>.
        /// </summary>
        public bool Always { get; set; }

        /// <summary>
        /// Seconds of frames its Update was not called for, handed over with the next call.
        /// </summary>
        public float UpdateWaited { get; set; }

        /// <summary>
        /// As <see cref="UpdateWaited"/>, for LateUpdate.
        /// </summary>
        public float LateUpdateWaited { get; set; }
    }

    /// <summary>
    /// The behaviours' hook for one phase besides Update, which is this system's own.
    /// </summary>
    private abstract class Hook(ScriptSystem scripts, UpdateType phase) : ISystem
    {
        public void Run(in Frame frame)
        {
            scripts.Call(phase, frame);
        }
    }

    [Phase(UpdateType.FixedUpdate)]
    private sealed class FixedUpdateHook(ScriptSystem scripts) : Hook(scripts, UpdateType.FixedUpdate);

    [Phase(UpdateType.LateUpdate)]
    private sealed class LateUpdateHook(ScriptSystem scripts) : Hook(scripts, UpdateType.LateUpdate);

    [Phase(UpdateType.Render)]
    private sealed class RenderHook(ScriptSystem scripts) : Hook(scripts, UpdateType.Render);
}
