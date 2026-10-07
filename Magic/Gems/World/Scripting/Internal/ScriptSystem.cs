using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Domain;
using Magic.Contexts.Events;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Numerics;

namespace ScriptingGem;

/// <summary>
/// Runs the scripts chunks attach to entities. Whoever spawns an entity offers each of its <c>scripts</c> entries
/// (<see cref="Attach"/>); every one taken becomes one instance its <see cref="Script"/> makes (<see cref="Script.Make"/>,
/// whatever language the script is written in). An <see cref="ISystem"/> goes on the <see cref="IEcs.Add(ISystem)"/>; an
/// <see cref="IBehavior"/> has its hooks called here, once per phase. The instances end with their entity (its
/// <see cref="Scripts"/> going says so), and when gems change all of them are made
/// again from the classes there are now: that is how a rebuilt project's scripts reload. It runs in Update.
/// <para>
/// A behaviour far from every camera is updated less often: Update and LateUpdate run every frame within
/// <see cref="FullRateDistance"/> metres of the nearest camera, then every 2nd, 4th, 8th frame as that distance
/// doubles, up to every <see cref="MaxInterval"/>th. A call that was waited for gets the time since the last one as
/// its frame's delta, so what moves by delta moves as far. FixedUpdate and Render are never held back, nor is an
/// entity without a <see cref="WorldTransform"/> (it is nowhere), nor a script that is <see cref="Script.AlwaysUpdate"/>.
/// </para>
/// <para>
/// While the world is loading (a domain opened in its place is not there yet, <see cref="World.State"/>) scripts are held: they
/// are not made until it is there, but for the <see cref="Script.Impatient"/> ones, which are made
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

    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly World _world;
    private readonly IEcsQuery<Camera, WorldTransform> _cameras;
    private readonly QueryChunkAction<Camera, WorldTransform> _addCameraPositions;
    private readonly List<Vector3> _cameraPositions = []; // as of this frame's Update
    private readonly Dictionary<Handle, List<Attached>> _attached = [];
    private readonly HashSet<Handle> _pending = []; // entities with scripts still to be made
    private readonly Predicate<Handle> _makeAll; // one delegate, not one per frame
    private readonly List<Handle> _removed = []; // filled by the observer, which must not change anything itself
    private readonly List<Attached> _faulted = [];
    private readonly HashSet<ulong> _missing = []; // script ids no asset has, each said once
    private readonly Subscription _observer;
    private readonly Disposables<Hook> _hooks;
    private readonly Disposables<Subscription> _scheduled;

    public ScriptSystem(IEcs ecs, Assets assets, World world)
    {
        _makeAll = MakeAll;
        _ecs = ecs;
        _assets = assets;
        _world = world;
        _cameras = ecs.Query<Camera, WorldTransform>().Build();
        _addCameraPositions = AddCameraPositions;
        _observer = ecs.Observe<Scripts>(ComponentEvent.Removed, _removed.Add);
        _hooks = new Disposables<Hook>(new Hook(this, UpdateType.FixedUpdate), new Hook(this, UpdateType.LateUpdate), new Hook(this, UpdateType.Render));
        _scheduled = new Disposables<Subscription>([.. _hooks.Select(hook => ecs.Add(hook))]);
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
    /// <see cref="Script.Impatient"/>.
    /// </summary>
    private bool Held => _world.State == DomainState.Loading;

    /// <summary>
    /// Attaches the script asset <paramref name="id"/> to the entity, with its chunk entry (the id and the instance's
    /// values, one JSON object). It is made in this system's next run, once the world is there unless its script is
    /// <see cref="Script.Impatient"/>, and ends with the entity. An id no asset has is said once and left out.
    /// </summary>
    internal void Attach(Handle entity, ulong id, Unread entry)
    {
        if (_assets.PathOf(id) is null)
        {
            if (_missing.Add(id))
                Log.Warn($"There is no script {id}: {entry}");

            return;
        }

        if (!_attached.TryGetValue(entity, out List<Attached>? attached))
            _attached[entity] = attached = [];

        attached.Add(new Attached(id, entry));
        _pending.Add(entity);
        _ecs.Add<Scripts>(entity);
    }

    public void Run(in Frame frame)
    {
        if (frame.Events.Of<GemsChanged>().Length > 0)
            Remake();

        _cameraPositions.Clear();
        _cameras.Run(_addCameraPositions);

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

    private void AddCameraPositions(ReadOnlySpan<Handle> entities, Span<Camera> cameras, Span<WorldTransform> worlds)
    {
        foreach (WorldTransform world in worlds)
            _cameraPositions.Add(world.Value.Translation);
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
                    Log.Error($"Script {behavior.GetType().Name} threw in {phase}. {ex}");
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
        if (attached.Script is { } known)
            return !Held && TryMake(entity, attached, known);

        attached.Loading ??= _assets.LoadAsync(new Handle<Script>(attached.Id));

        if (!attached.Loading.IsCompleted)
            return false;

        Task<Script?> loading = attached.Loading;
        attached.Loading = null;
        if (!loading.IsCompletedSuccessfully)
        {
            Log.Error($"A script on {_ecs.GetName(entity) ?? entity.ToString()} could not be loaded: {loading.Exception?.GetBaseException().Message}");
            return true;
        }

        if (loading.Result is not { Make: not null } script)
            return true; // the asset manager logged why

        if (Held && !script.Impatient)
        {
            attached.Script = script; // made when its domain is there
            return false;
        }

        return TryMake(entity, attached, script);
    }

    /// <summary>
    /// Makes the entry's script; true whether or not that worked: it is not tried again.
    /// </summary>
    private bool TryMake(Handle entity, Attached attached, Script script)
    {
        attached.Script = null;
        try
        {
            if (script.Make?.Invoke(entity, attached.Entry) is not { } instance)
            {
                Log.Error($"Script {script.Name} on {_ecs.GetName(entity) ?? entity.ToString()} could not be made.");
                return true;
            }

            attached.Instance = instance;
            attached.Always = script.AlwaysUpdate;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"Script {script.Name} on {_ecs.GetName(entity) ?? entity.ToString()} could not be made: {ex.Message}");
            return true;
        }

        if (attached.Instance is ISystem system)
            attached.Scheduled = _ecs.Add(system);

        return true;
    }

    /// <summary>
    /// Takes the instance off the schedule and disposes it; the entry stays, to be made again.
    /// </summary>
    private static void End(Attached attached)
    {
        attached.Scheduled?.Dispose();
        try
        {
            attached.Instance?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Script {attached.Instance?.GetType().Name}: Dispose failed. {ex}");
        }

        attached.Scheduled = null;
        attached.Instance = null;
        attached.Loading = null;
        attached.Script = null;
        attached.UpdateWaited = 0f;
        attached.LateUpdateWaited = 0f;
    }

    /// <summary>
    /// One script of one entity: its script asset, the chunk's entry, and the instance made from it while there is one.
    /// </summary>
    private sealed class Attached(ulong id, Unread entry)
    {
        public ulong Id { get; } = id;

        public Unread Entry { get; } = entry;

        public IScript? Instance { get; set; }

        /// <summary>
        /// Its script asset on the way, from when it is first to be made until it has arrived.
        /// </summary>
        public Task<Script?>? Loading { get; set; }

        /// <summary>
        /// Its script, loaded and waiting to be made: its entity is held.
        /// </summary>
        public Script? Script { get; set; }

        /// <summary>
        /// An <see cref="ISystem"/>'s place on the schedule.
        /// </summary>
        public Subscription? Scheduled { get; set; }

        /// <summary>
        /// Its script is <see cref="Script.AlwaysUpdate"/>.
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
    /// The behaviours' hook for one phase besides Update, which is this system's own: scheduled in that phase.
    /// </summary>
    private sealed class Hook(ScriptSystem scripts, UpdateType phase) : ISystem
    {
        public UpdateType Phase => phase;

        public void Run(in Frame frame)
        {
            scripts.Call(phase, frame);
        }
    }
}
