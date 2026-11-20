using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Projects;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CSharpScripting;

/// <summary>
/// Runs a copy of each script that an entity's <see cref="ScriptContainer"/> holds, from the frame after the handle comes until
/// the handle or the entity goes.
/// </summary>
/// <remarks>
/// Each <see cref="Update"/> starts the C# scripts that have no copy, when the world is not loading, and gives each
/// copy the values of its slot that changed since it took them. Each hook ends the copies whose entity or handle went.
/// A script in another language is left to the gem for that language. If a script's class is not found, cannot be
/// made or started, or a hook throws, the error is logged and the handle is taken off the entity. A behaviour far from
/// the player's cameras updates less often (<see cref="Pacing"/>), unless its entity is global. Game thread.
/// </remarks>
public sealed class Runner(World world, AssetManager assets, IServiceProvider services) : Disposable, IHookIntoWorldSave
{
    /// <summary>
    /// The state of a copy as JSON, which a save keeps and a copy from a save takes up.
    /// </summary>
    private readonly ScriptState _state = new(services);

    /// <summary>
    /// The copies that run, by their entity and script, in the order that they started.
    /// </summary>
    private readonly OrderedDictionary<(Entity Entity, Handle<Script> Script), Copy> _attached = [];

    /// <summary>
    /// The slots whose copy is to be started or given the values that changed, found in the query and worked on after
    /// it, as a start can change the entities.
    /// </summary>
    private readonly List<(Entity Entity, ScriptSlot Slot)> _matching = [];

    /// <summary>
    /// Where the cameras of the player are, as of this frame's <see cref="Update"/>.
    /// </summary>
    private readonly List<Vector3> _cameras = [];

    /// <summary>
    /// Writes the state of each copy that runs into its slot of the entity's <see cref="ScriptContainer"/>, so the save
    /// or the domain that opens again keeps it and the copy that comes from it takes it up in place of a start.
    /// </summary>
    public void OnWorldSaving()
    {
        foreach (((Entity entity, Handle<Script> script), Copy copy) in _attached)
        {
            if (!IsNamed(entity, script))
                continue;

            List<KeyValuePair<string, JsonElement>> state = _state.Read(copy.Script);
            ref ScriptContainer container = ref world.Mutate<ScriptContainer>(entity);
            container.Keep(script, CollectionsMarshal.AsSpan(state));

            // The copy holds what the save just read out of it, so the set that the slot now has is no new values.
            copy.Values = GetValues(in container, script);
        }
    }

    internal void Update(in Frame frame)
    {
        if (!world.IsLoading)
            MatchSlots();

        FindCameras();
        Run(Hook.Update, frame);
    }

    internal void FixedUpdate(in Frame frame) => Run(Hook.FixedUpdate, frame);

    internal void LateUpdate(in Frame frame) => Run(Hook.LateUpdate, frame);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
            return;

        foreach (((Entity entity, _), Copy copy) in _attached)
        {
            End(entity, copy.Script);
        }

        _attached.Clear();
        base.Dispose(disposing);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a script throws as it ends, it is logged, and the others end."
    )]
    private static void End(Entity entity, IScript script)
    {
        if (script is not IDisposable disposable)
            return;

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Script {script.GetType().Name} on {entity} threw as it ended: {ex}");
        }
    }

    /// <summary>
    /// Gets what <paramref name="ex"/> was, when reflection wrapped what a script threw.
    /// </summary>
    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    /// <summary>
    /// The values that the slot of <paramref name="script"/> holds in <paramref name="container"/>, or none when no
    /// slot holds it.
    /// </summary>
    private static ScriptValues GetValues(in ScriptContainer container, Handle<Script> script)
    {
        foreach (ScriptSlot slot in container)
        {
            if (slot.Script == script)
                return slot.Values;
        }

        return ScriptValues.None;
    }

    /// <summary>
    /// Makes the copies match the slots of the entities: a copy of each C# script that an entity names and has none
    /// of, and the values of a slot that changed given to the copy that runs it.
    /// </summary>
    private void MatchSlots()
    {
        _matching.Clear();
        foreach (EcsChunk<ScriptContainer> chunk in world.Query<ScriptContainer>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                foreach (ScriptSlot slot in chunk.First[i])
                {
                    if (slot.Script != Handle<Script>.None && !IsTakenUp(chunk.Entities[i], slot))
                        _matching.Add((chunk.Entities[i], slot));
                }
            }
        }

        foreach ((Entity entity, ScriptSlot slot) in _matching)
        {
            // A start before this one can destroy the entity or take the handle off.
            if (!IsNamed(entity, slot.Script))
                continue;

            if (_attached.TryGetValue((entity, slot.Script), out Copy? copy))
                Tune(copy, slot, entity);
            else if (FindClass(slot.Script) is { } found)
            {
                Result<IScript> started = found.Ok ? Start(found.Value, entity, slot) : Result.Failure(found.Error);
                if (started.Ok)
                    _attached.Add((entity, slot.Script), new Copy(started.Value, slot.Values));
                else
                    TakeOff(entity, slot.Script, $"is left off: {started.Error}");
            }
        }
    }

    /// <summary>
    /// Whether the copy of <paramref name="slot"/> runs on <paramref name="entity"/> with the set of values that the
    /// slot holds now.
    /// </summary>
    private bool IsTakenUp(Entity entity, ScriptSlot slot) =>
        _attached.TryGetValue((entity, slot.Script), out Copy? copy) && slot.Values == copy.Values;

    /// <summary>
    /// Gives <paramref name="copy"/> the values that its slot holds now, so a console line or a file that changed
    /// them reaches the script while it runs. A slot marked started holds the state of a copy, so the copy takes up
    /// its fields as well as its values.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a setter of the script throws, it is logged and the script ends. The others go on."
    )]
    private void Tune(Copy copy, ScriptSlot slot, Entity entity)
    {
        try
        {
            if (slot.Started)
                _state.Restore(slot.Values, copy.Script, entity);
            else
                _state.SetValues(slot.Values, copy.Script, entity);
        }
        catch (Exception ex)
        {
            // The next hook finds the handle gone, ends the copy and takes it off the list.
            TakeOff(entity, slot.Script, $"threw as it took up new values and ends: {Unwrap(ex)}");

            return;
        }

        copy.Values = slot.Values;
    }

    /// <summary>
    /// Finds where the cameras of the player are: the entities tagged <see cref="Tag.Player"/> that have a
    /// <see cref="Camera"/>.
    /// </summary>
    private void FindCameras()
    {
        _cameras.Clear();
        foreach (EcsChunk<Transform, Camera> chunk in world.Query<Transform, Camera>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                Entity camera = chunk.Entities[i];
                if (world.Has<Tags>(camera) && world.Get<Tags>(camera).Has(Tag.Player))
                    _cameras.Add(chunk.First[i].Position);
            }
        }
    }

    /// <summary>
    /// Gets the frames between two updates of the behaviour of <paramref name="entity"/>: 1 for a global entity or one
    /// with no place, else by its distance from the cameras of the player.
    /// </summary>
    private int GetIntervalFrames(Entity entity)
    {
        bool isGlobal = world.Has<Tags>(entity) && world.Get<Tags>(entity).Has(Tag.Global);
        if (isGlobal || !world.Has<Transform>(entity))
            return 1;

        return Pacing.GetIntervalFrames(world.Get<Transform>(entity).Position, CollectionsMarshal.AsSpan(_cameras));
    }

    /// <summary>
    /// Whether <paramref name="entity"/> is alive and its <see cref="ScriptContainer"/> holds <paramref name="script"/>.
    /// </summary>
    private bool IsNamed(Entity entity, Handle<Script> script) =>
        world.IsAlive(entity) && world.Has<ScriptContainer>(entity) && world.Get<ScriptContainer>(entity).Has(script);

    /// <summary>
    /// Logs why <paramref name="script"/> failed on <paramref name="entity"/>, and takes its handle off the entity, so it
    /// is not tried again.
    /// </summary>
    private void TakeOff(Entity entity, Handle<Script> script, string why)
    {
        Log.Error($"Script {assets.GetPath(script) ?? script.ToString()} on {entity} {why}");
        if (IsNamed(entity, script))
            world.Mutate<ScriptContainer>(entity).Remove(script);
    }

    /// <summary>
    /// Finds the <see cref="IScript"/> class with the name of the C# file of <paramref name="handle"/>, or null when the
    /// script is in another language.
    /// </summary>
    private Result<Type>? FindClass(Handle<Script> handle)
    {
        if (assets.GetPath(handle) is not { } path)
            return Result.Failure("no asset has its id.");

        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return null;

        return Assemblies.Get<IScript>(Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// Makes a copy of <paramref name="type"/> for <paramref name="entity"/>, sets the values of
    /// <paramref name="slot"/> on it and starts it, or gives why not.
    /// </summary>
    /// <remarks>
    /// Its one public constructor gets the entity for an <see cref="Entity"/> parameter, a service for each other
    /// parameter, and a parameter's default when no service is there. A slot that a save marked started holds the state
    /// of the copy it took, so this copy takes that state up and <see cref="IScript.Resume"/> remakes what the save
    /// does not hold, in place of <see cref="IScript.Start"/>.
    /// </remarks>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a script throws as it is made or started, the entity goes without it."
    )]
    private Result<IScript> Start(Type type, Entity entity, ScriptSlot slot)
    {
        ConstructorInfo[] constructors = type.GetConstructors();
        if (constructors.Length != 1)
            return Result.Failure($"it must have one public constructor. It has {constructors.Length}.");

        ParameterInfo[] parameters = constructors[0].GetParameters();
        object?[] arguments = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            Type wanted = parameters[i].ParameterType;
            if (wanted == typeof(Entity))
                arguments[i] = entity;
            else if (services.GetService(wanted) is { } service)
                arguments[i] = service;
            else if (parameters[i].HasDefaultValue)
                arguments[i] = parameters[i].DefaultValue;
            else
                return Result.Failure(
                    $"its constructor asks for a {wanted.Name}, which no gem that the project lists gives."
                );
        }

        try
        {
            if (constructors[0].Invoke(arguments) is not IScript script)
                return Result.Failure("it is not an IScript.");

            if (slot.Started)
            {
                _state.Restore(slot.Values, script, entity);
                script.Resume();
            }
            else
            {
                _state.SetValues(slot.Values, script, entity);
                script.Start();
            }

            return Result.Success(script);
        }
        catch (Exception ex)
        {
            return Result.Failure($"it threw as it started: {Unwrap(ex)}");
        }
    }

    /// <summary>
    /// Calls <paramref name="hook"/> on each behaviour, in the order that they started. A copy whose entity or handle
    /// went ends instead, also when a behaviour before it in the same hook did that. A behaviour that throws ends.
    /// </summary>
    /// <remarks>
    /// <see cref="IBehavior.FixedUpdate"/> runs at each step. <see cref="IBehavior.Update"/> and
    /// <see cref="IBehavior.LateUpdate"/> run only in the frames that the behaviour is due, with the time since its last
    /// update as the frame's deltas, so it moves as far as it would at each frame.
    /// </remarks>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a behaviour throws, it is logged and the behaviour ends. The others go on."
    )]
    private void Run(Hook hook, in Frame frame)
    {
        for (int i = 0; i < _attached.Count; i++)
        {
            ((Entity entity, Handle<Script> script), Copy copy) = _attached.GetAt(i);
            if (!IsNamed(entity, script))
            {
                End(entity, copy.Script);
                _attached.RemoveAt(i--);
                continue;
            }

            try
            {
                // A system works over many entities, so it runs each frame, wherever its own entity is.
                if (copy.Script is ISystem system && hook == Hook.Update)
                    system.Run(frame, world);
                else if (copy.Script is IBehavior behavior)
                    RunBehavior(hook, entity, copy, behavior, frame);
            }
            catch (Exception ex)
            {
                End(entity, copy.Script);
                _attached.RemoveAt(i);
                i--;
                TakeOff(entity, script, $"threw in {hook} and ends: {ex}");
            }
        }
    }

    /// <summary>
    /// Calls <paramref name="hook"/> on <paramref name="behavior"/>, when it is due in this frame.
    /// </summary>
    private void RunBehavior(Hook hook, Entity entity, Copy copy, IBehavior behavior, in Frame frame)
    {
        // Update works out how often the behaviour is due, and LateUpdate keeps to what Update found.
        if (hook == Hook.Update)
            copy.IntervalFrames = GetIntervalFrames(entity);

        if (hook != Hook.FixedUpdate && !Pacing.IsDue(entity, frame.Index, copy.IntervalFrames))
            return;

        switch (hook)
        {
            case Hook.Update:
                behavior.Update(copy.GetCallFrame(frame));
                break;
            case Hook.FixedUpdate:
                behavior.FixedUpdate(frame);
                break;
            case Hook.LateUpdate:
                behavior.LateUpdate(copy.GetCallFrame(frame));
                copy.LastCalled = frame;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(hook), hook, "There is no such hook.");
        }
    }

    /// <summary>
    /// A hook of <see cref="IBehavior"/>.
    /// </summary>
    private enum Hook : byte
    {
        Update,
        FixedUpdate,
        LateUpdate,
    }

    /// <summary>
    /// A copy of a script that runs on an entity, and how often it updates.
    /// </summary>
    private sealed class Copy(IScript script, ScriptValues values)
    {
        public IScript Script { get; } = script;

        /// <summary>
        /// The values of its slot that it took up last.
        /// </summary>
        public ScriptValues Values { get; set; } = values;

        /// <summary>
        /// The frames between two updates, as this frame's <see cref="Update"/> found.
        /// </summary>
        public int IntervalFrames { get; set; } = 1;

        /// <summary>
        /// The frame of its last <see cref="IBehavior.LateUpdate"/>, or null before the first.
        /// </summary>
        public Frame? LastCalled { get; set; }

        /// <summary>
        /// Gets <paramref name="frame"/> with the time since <see cref="LastCalled"/> as its deltas.
        /// </summary>
        public Frame GetCallFrame(in Frame frame) =>
            LastCalled is { } last
                ? frame with
                {
                    AppDeltaSec = frame.AppSec - last.AppSec,
                    SimDeltaSec = frame.SimSec - last.SimSec,
                }
                : frame;
    }
}
