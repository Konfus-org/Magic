using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Magic.Attributes;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Scripting;

/// <summary>
/// Reads, makes and runs scripts (<see cref="IScript"/>): provides <see cref="IScripts"/>, and reads a <c>.cs</c>
/// file as the class named like it in the code loaded (<see cref="Script"/>, <see cref="LoadAsync"/>). Each entity's
/// <see cref="Scripts"/> says which it runs: every frame (<see cref="Reconcile"/>) a copy is made for each script an
/// entity names and has no copy of, its constructor given the entity first and then the services it asks for, given
/// the values the prefab holds for it (<see cref="Attach"/>) or its defaults, then started; and a copy whose entity no
/// longer names it, or is gone, ends. While a domain loads in place of the world (<see cref="WorldStream"/>), a script
/// starts only as the new domain is readied (<see cref="StreamState.Readying"/>), so what it spawns is readied with the
/// domain, and its hooks and systems wait until the world runs again (<see cref="StreamState.Loaded"/>), so the frame
/// that first shows the world draws it as it loaded. One whose class is <see cref="ImpatientAttribute"/> starts and
/// runs while the loading domain shows instead, but one made as the new domain is readied waits all the same, as it
/// belongs to the world not shown yet; as does one put back with a saved world.
/// A system goes on the <see cref="IEcs"/>, through the Add that takes its kind; an <see cref="IBehavior"/> has its
/// hooks called in this gem's. A pipeline script is made the same way for no entity, and never asks for one. A script
/// read again, as the code it is in was built, has each live copy ended and a new one of its class started in its
/// place, for the same entity with the same values. A script is saved with the world as its values and the fields it
/// kept for itself, as JSON, and put back from them without being started, as it is as it was.
/// <para>
/// A behaviour far from the player's cameras, those tagged <see cref="Tag.Player"/>, is updated less often: Update and
/// LateUpdate run every frame within <see cref="ScriptingSettings.FullRateDistance"/> of the nearest, and up to n times
/// as far every nth frame, up to every <see cref="ScriptingSettings.MaxInterval"/>th, with the time since the last call
/// as its frame's delta. FixedUpdate is never held back, nor is an entity without a <see cref="Transform"/>, which is
/// nowhere.
/// </para>
/// <para>
/// While the world is paused (<see cref="Frame.IsPaused"/>), a behaviour's hooks wait, unless its class is
/// <see cref="UnpausableAttribute"/> or its entity is tagged <see cref="Tag.Unpausable"/>; scripts still start.
/// </para>
/// </summary>
internal sealed class ScriptingGem : IGem, IScripts, IAssetLoader<Script>, IPreloader, IDisposable
{
    /// <summary>
    /// Each of the <see cref="IEcs"/>'s Register methods, one per kind of system, with the type it takes: a class that
    /// implements that interface is a system of that kind.
    /// </summary>
    private static readonly (MethodInfo Register, Type Taken)[] _registers = Array.ConvertAll(
        Array.FindAll(
            typeof(IEcs).GetMethods(),
            method => string.Equals(method.Name, nameof(IEcs.Register), StringComparison.Ordinal)
                && method.GetParameters().Length == 1),
        method => (method, method.GetParameters()[0].ParameterType));

    private static readonly Meter _meter = new("Magic.Gems.Scripting");
    private static readonly Counter<int> _calls = _meter.CreateCounter<int>(
        "magic.gems.scripting.behavior.calls",
        "{call}",
        "Behaviour hooks called, tagged with the phase.");
    private static readonly Gauge<int> _behaviors = _meter.CreateGauge<int>(
        "magic.gems.scripting.behaviors",
        "{behavior}",
        "Scripts attached to live entities, behaviours or not.");
    private static readonly string[] _phases = Enum.GetNames<Phase>();

    private readonly ScriptingSettings _settings;
    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly Assemblies _assemblies;
    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "Core's, which Core ends after every gem.")]
    private readonly IServiceProvider _services;
    private readonly SubscriptionList _watching = new();

    /// <summary>
    /// The scripts made, by the entity and the script asset of each, in the order they were made. Main thread.
    /// </summary>
    private readonly OrderedDictionary<(Entity Entity, Handle<Script> Script), Attached> _attached = [];

    /// <summary>
    /// The values given for scripts not started yet (<see cref="Attach"/>), by entity and script asset. Main thread.
    /// </summary>
    private readonly Dictionary<(Entity Entity, Handle<Script> Script), IReadOnlyDictionary<string, object?>> _pending
        = [];

    /// <summary>
    /// Every script asset named: as the prefab that attached it loaded it, or fetched when nothing gave values for it
    /// (<see cref="Fetch"/>), null while that load is on its way, and why not when it did not load, so the entities
    /// naming it go without it until it is attached again or the code is built again. The one place a script's class
    /// is kept, so a build replaces it once (<see cref="Restart"/>). Main thread.
    /// </summary>
    private readonly Dictionary<Handle<Script>, Result<Script>?> _loaded = [];

    /// <summary>
    /// Scripts fetched on workers, loaded or not, for the main thread to take into <see cref="_loaded"/> as Update
    /// starts.
    /// </summary>
    private readonly Channel<(Handle<Script> Handle, Result<Script> Loaded)> _fetched =
        Channel.CreateUnbounded<(Handle<Script> Handle, Result<Script> Loaded)>(
            new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Scripts that could not be started, or whose asset did not load, each said once, so an entity naming one is not
    /// tried again every frame. Main thread.
    /// </summary>
    private readonly HashSet<(Entity Entity, Handle<Script> Script)> _failed = [];

    /// <summary>
    /// Scripts named with no copy, found this frame and started once the query is over, as a start may spawn. Main
    /// thread.
    /// </summary>
    private readonly List<(Entity Entity, Handle<Script> Script)> _starting = [];

    /// <summary>
    /// Where the player's cameras are, as of this frame's Update. Main thread.
    /// </summary>
    private readonly List<Vector3> _cameras = [];

    /// <summary>
    /// The members of scripts that could not be saved or put back, as <c>Class.member</c>, each warned about once.
    /// Main thread.
    /// </summary>
    private readonly HashSet<string> _unkept = new(StringComparer.Ordinal);

    /// <summary>
    /// What streams into the world, as last told (<see cref="WorldStream"/>), heard as each frame begins: this gem is
    /// one the world is built on, so cannot take the world to read it. Main thread.
    /// </summary>
    private WorldStream _stream = WorldStream.None;

    public ScriptingGem(
        ScriptingSettings settings,
        IEcs ecs,
        Assets assets,
        Assemblies assemblies,
        Events events,
        IServiceProvider services)
    {
        _settings = settings;
        _ecs = ecs;
        _assets = assets;
        _assemblies = assemblies;
        _services = services;
        _watching.Add(events.Watch<AssetReloaded<Script>>(reloaded => Restart(reloaded.Asset)));
        _watching.Add(events.Watch<WorldStream>(FollowStream));
    }

    public void Dispose() => _watching.Dispose();

    /// <summary>
    /// The <c>.cs</c> file at <paramref name="path"/> as a <see cref="Script"/>: the class named like the file, an
    /// <see cref="IScript"/>, found in the code loaded (<see cref="Assemblies"/>). The file's bytes are not read: its
    /// class is compiled with the project's code, or the gem's; why not, when no code loaded declares the class, or
    /// its <c>.meta</c> holds settings, which a script takes none of. Any thread.
    /// </summary>
    public ValueTask<Result<Script>> LoadAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        JsonElement settings,
        CancellationToken cancel)
    {
        if (settings.GetPropertyCount() != 0)
        {
            return new(Result.Failure<Script>(
                $"its .meta holds settings, {settings.GetRawText()}, which a script file does not take."));
        }

        return new(TryGetClass(path, Handle<Script>.None));
    }

    public Result<IScript> Make(Script script) => MakeOf(script.Type, entity: null);

    public void Attach(Entity entity, PrefabScript script)
    {
        if (!_ecs.IsAlive(entity)) return;

        Handle<Script> handle = script.Script.Handle;
        Scripts scripts = _ecs.Has<Scripts>(entity) ? _ecs.Get<Scripts>(entity) : default;
        if (!scripts.Has(handle))
        {
            if (!scripts.Add(handle))
            {
                Debugging.Error($"Script {script.Script.Type.Name} is left off entity {entity.Id}: "
                    + $"an entity has at most {Scripts.Capacity} scripts.");

                return;
            }

            _ecs.Set(entity, in scripts);
        }

        _loaded[handle] = Result.Success(script.Script);
        _pending[(entity, handle)] = script.Values;
        _failed.Remove((entity, handle));
    }

    public void Save(Entity entity, ICollection<ScriptState> into)
    {
        if (!_ecs.Has<Scripts>(entity)) return;

        Scripts scripts = _ecs.Get<Scripts>(entity);
        foreach (Handle<Script> handle in scripts)
        {
            if (_attached.TryGetValue((entity, handle), out Attached? attached))
                into.Add(new ScriptState(handle, GetState(attached.Instance)));
        }
    }

    public void Restore(Entity entity, ScriptState state)
    {
        if (!_ecs.IsAlive(entity)) return;

        // The asset is loaded as any named one is, so it is held and told of when the code is built again; its class is
        // found now, as a load reads no file, since the world is put back in this call.
        Handle<Script> handle = state.Script;
        if (!_loaded.TryGetValue(handle, out Result<Script>? known)) Fetch(handle);

        Result<Script> script = known ?? Find(handle);
        if (script.Ok)
        {
            Keep(entity, script.Value, Put(entity, script.Value, state));
        }
        else
        {
            Debugging.Error($"Script {GetName(handle)} is left off entity {entity.Id}: {script.Error}");
            _failed.Add((entity, handle));
        }
    }

    /// <summary>
    /// Has everything at once: a prefab's scripts are read with it, so all a domain's scripts need is to start as it is
    /// readied (<see cref="IsReady"/>).
    /// </summary>
    public float GetProgress(in WorldStream stream) => 1;

    /// <summary>
    /// Ready once no script on any entity is left to start, those the started ones attached among them, and none
    /// waits for its asset: while the world is readied, each Update starts them all, held, so what they spawn as they
    /// start is readied with it, and their hooks and systems wait until the world runs.
    /// </summary>
    public bool IsReady(in WorldStream stream)
    {
        foreach ((ReadOnlySpan<Entity> entities, Span<Scripts> scripts) in _ecs.Query<Scripts>())
        {
            for (int i = 0; i < entities.Length; i++)
            {
                foreach (Handle<Script> handle in scripts[i])
                {
                    // One whose asset did not load never starts; any other, in, coming or not yet asked for, will.
                    if (IsUnstarted(entities[i], handle)
                        && (!_loaded.TryGetValue(handle, out Result<Script>? known) || known is not { Ok: false }))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    public void Update(in Frame frame)
    {
        // The script assets fetched on workers since the last frame, each that did not load said once.
        while (_fetched.Reader.TryRead(out (Handle<Script> Handle, Result<Script> Loaded) fetched))
        {
            _loaded[fetched.Handle] = fetched.Loaded;
            if (!fetched.Loaded.Ok)
            {
                Debugging.Error(
                    $"Script {GetName(fetched.Handle)} is left off the entities naming it: {fetched.Loaded.Error}");
            }
        }

        Reconcile();
        FindCameras();
        Run(Phase.Update, frame);
    }

    public void FixedUpdate(in Frame frame) => Run(Phase.FixedUpdate, frame);

    public void LateUpdate(in Frame frame) => Run(Phase.LateUpdate, frame);

    /// <summary>
    /// Ends every script, as the run ends. On the main thread, the ECS's.
    /// </summary>
    public void Stop()
    {
        foreach (Attached attached in _attached.Values)
        {
            End(attached);
        }

        _attached.Clear();
        _pending.Clear();
    }

    /// <summary>
    /// The <see cref="IEcs"/>'s Register for each kind of system <paramref name="type"/> is, made for the components
    /// it names; none for a class that is no system.
    /// </summary>
    private static List<MethodInfo> GetRegisters(Type type)
    {
        // Each interface the class has, matched with the Register that takes it, or for a generic system interface,
        // with the generic Register made for the same component types.
        List<MethodInfo> registers = [];
        foreach (Type contract in type.GetInterfaces())
        {
            foreach ((MethodInfo register, Type taken) in _registers)
            {
                if (taken == contract)
                {
                    registers.Add(register);
                }
                else if (register.IsGenericMethodDefinition && contract.IsGenericType && taken.IsGenericType
                    && taken.GetGenericTypeDefinition() == contract.GetGenericTypeDefinition())
                {
                    registers.Add(register.MakeGenericMethod(contract.GetGenericArguments()));
                }
            }
        }

        return registers;
    }

    /// <summary>
    /// Takes <paramref name="attached"/>'s systems off the ECS, and disposes it when it is disposable.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a script throws as it ends, it is logged and the others still end.")]
    private static void End(Attached attached)
    {
        attached.Systems.Dispose();

        if (attached.Instance is not IDisposable disposable) return;

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            Debugging.Error(
                $"Script {attached.Instance.GetType().Name} on entity {attached.Entity.Id} threw as it ended: {ex}");
        }
    }

    /// <summary>
    /// Whether scripts of <paramref name="type"/> start and run while a domain loads in place of the world.
    /// </summary>
    private static bool IsImpatient(Type type) => type.IsDefined(typeof(ImpatientAttribute), inherit: false);

    /// <summary>
    /// Whether scripts of <paramref name="type"/> run while the world is paused.
    /// </summary>
    private static bool IsUnpausable(Type type) => type.IsDefined(typeof(UnpausableAttribute), inherit: false);

    /// <summary>
    /// What <paramref name="ex"/> was: what a script threw, when reflection wrapped it.
    /// </summary>
    private static Exception Unwrap(Exception ex)
        => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    /// <summary>
    /// <paramref name="type"/>'s public property with a getter and a setter that a file names <paramref name="key"/>,
    /// in Core's dialect: one of its values. Null when it has none.
    /// </summary>
    private static PropertyInfo? FindValue(Type type, string key)
    {
        foreach (PropertyInfo property in GetValues(type))
        {
            if (string.Equals(GetName(property), key, StringComparison.Ordinal)) return property;
        }

        return null;
    }

    /// <summary>
    /// <paramref name="type"/>'s public properties with a getter and a setter: its values, which a prefab sets.
    /// </summary>
    private static List<PropertyInfo> GetValues(Type type)
    {
        List<PropertyInfo> values = [];
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is { IsPublic: true } && property.SetMethod is { IsPublic: true }
                && property.GetIndexParameters().Length == 0)
            {
                values.Add(property);
            }
        }

        return values;
    }

    /// <summary>
    /// <paramref name="type"/>'s field saved as <paramref name="name"/> (<see cref="GetName(FieldInfo)"/>): its own of
    /// that name, else the one the compiler made for a property or a primary constructor parameter of it. Null when
    /// it has none.
    /// </summary>
    private static FieldInfo? FindField(Type type, string name)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        return type.GetField(name, Any)
            ?? type.GetField($"<{name}>k__BackingField", Any) ?? type.GetField($"<{name}>P", Any);
    }

    /// <summary>
    /// A value's name as a file gives it, in Core's dialect.
    /// </summary>
    private static string GetName(PropertyInfo property) => JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    /// <summary>
    /// A field's name as it is saved: its own, or for one the compiler made, the name of the property or primary
    /// constructor parameter it is for, which is between the angle brackets.
    /// </summary>
    private static string GetName(FieldInfo field)
    {
        int end = field.Name.IndexOf('>', StringComparison.Ordinal);

        return field.Name.StartsWith('<') && end > 1 ? field.Name[1..end] : field.Name;
    }

    /// <summary>
    /// <paramref name="frame"/> as <paramref name="attached"/> is called with it: its deltas the time on each clock since
    /// the frame it was last called in, so a behaviour called less often, far from the player, moves as far; the frame's
    /// own before its first call.
    /// </summary>
    private static Frame GetCallFrame(Attached attached, in Frame frame)
    {
        if (attached.LastCalled is not { } last) return frame;

        return frame with
        {
            WorldDeltaSec = (float)(frame.WorldSec - last.WorldSec),
            RealDeltaSec = (float)(frame.RealSec - last.RealSec),
        };
    }

    /// <summary>
    /// A new <paramref name="type"/> for <paramref name="entity"/>, or for none: its one public constructor given the
    /// entity first, when it asks for it, then a service for each parameter, and a parameter's default when nothing
    /// provides it. A script that needs only services, as one that answers keys does, asks for no entity. Why not, when
    /// it has no such constructor, asks for an entity when it is made for none or anywhere but first, asks for what
    /// nothing provides, or throws.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a script throws as it is made, it is not made, and the others are.")]
    private Result<IScript> MakeOf(Type type, Entity? entity)
    {
        ConstructorInfo[] constructors = type.GetConstructors();

        if (constructors.Length != 1)
        {
            return Result.Failure<IScript>(
                $"it must have exactly one public constructor; it has {constructors.Length}.");
        }

        ParameterInfo[] parameters = constructors[0].GetParameters();
        object?[] arguments = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            Type wanted = parameters[i].ParameterType;
            if (wanted == typeof(Entity))
            {
                if (i != 0 || entity is null)
                {
                    return Result.Failure<IScript>("its constructor asks for an Entity where none is given: "
                        + "a script takes the entity it is attached to first and once, and one made for no entity, "
                        + "such as a pipeline script, never takes one.");
                }

                arguments[i] = entity.Value;
            }
            else if (_services.GetService(wanted) is { } service)
            {
                arguments[i] = service;
            }
            else if (parameters[i].HasDefaultValue)
            {
                arguments[i] = parameters[i].DefaultValue;
            }
            else
            {
                return Result.Failure<IScript>(
                    $"its constructor asks for a {wanted.Name}, which no gem the project lists provides.");
            }
        }

        try
        {
            return constructors[0].Invoke(arguments) is IScript made
                ? Result.Success(made)
                : Result.Failure<IScript>("it is no IScript.");
        }
        catch (Exception ex)
        {
            return Result.Failure<IScript>($"it threw as it was made: {Unwrap(ex)}");
        }
    }

    /// <summary>
    /// A script asset's path, for messages; its id when no asset has it.
    /// </summary>
    private string GetName(Handle<Script> script)
        => _assets.GetPath(script) ?? script.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The <c>.cs</c> file at <paramref name="path"/> as the <see cref="Script"/> with <paramref name="handle"/>: the
    /// class named like the file, an <see cref="IScript"/>, found in the code loaded (<see cref="Assemblies"/>); why
    /// not, when no code loaded declares it, or two classes have the name.
    /// </summary>
    private Result<Script> TryGetClass(string path, Handle<Script> handle)
    {
        Result<Type> type = _assemblies.TryGet<IScript>(Path.GetFileNameWithoutExtension(path));

        return type.Ok
            ? Result.Success(new Script { Handle = handle, Type = type.Value })
            : Result.Failure<Script>(type.Error);
    }

    /// <summary>
    /// The script asset <paramref name="handle"/> found now, its class in the code loaded as <see cref="LoadAsync"/>
    /// finds it, for a world put back, which does not wait for a load; why not, when no asset has its id or no code
    /// loaded declares the class.
    /// </summary>
    private Result<Script> Find(Handle<Script> handle)
    {
        string? path = _assets.GetPath(handle);

        if (path is null) return Result.Failure<Script>("no asset has its id.");

        return TryGetClass(path, handle);
    }

    /// <summary>
    /// Whether a field of <paramref name="type"/> is no data of the script's own: a service its constructor was handed,
    /// or something made rather than kept, a delegate, a task or a disposable, which a copy put back makes again.
    /// </summary>
    private bool IsHanded(Type type)
    {
        return typeof(Delegate).IsAssignableFrom(type) || typeof(Task).IsAssignableFrom(type)
            || typeof(IDisposable).IsAssignableFrom(type)
            || (!type.IsValueType && _services.GetService(type) is not null);
    }

    /// <summary>
    /// Makes the scripts what each entity's <see cref="Scripts"/> says: ends a copy whose entity no longer names its
    /// script, or is gone, and starts one for each script named that has none, with the values given for it
    /// (<see cref="Attach"/>) or its defaults, unless the world is loading, and not being readied, and the class is not
    /// <see cref="ImpatientAttribute"/>. An asset no prefab loaded is fetched first (<see cref="Fetch"/>), and the
    /// script starts once it has. The starts come after the query, as a start may spawn. A readying ends once nothing
    /// is left to start. Main thread, before the frame's behaviours run.
    /// </summary>
    private void Reconcile()
    {
        // Copies whose entity is gone, or no longer names their script, end; ending one takes it out of the list, so
        // the index steps back over it.
        for (int i = 0; i < _attached.Count; i++)
        {
            Attached attached = _attached.GetAt(i).Value;
            if (!_ecs.IsAlive(attached.Entity) || !_ecs.Has<Scripts>(attached.Entity)
                || !_ecs.Get<Scripts>(attached.Entity).Has(attached.Script))
            {
                Drop(attached);
                i--;
            }
        }

        // The scripts named with no copy, gathered now and started once the query is over, as a start may spawn.
        _starting.Clear();
        foreach ((ReadOnlySpan<Entity> entities, Span<Scripts> scripts) in _ecs.Query<Scripts>())
        {
            for (int i = 0; i < entities.Length; i++)
            {
                foreach (Handle<Script> handle in scripts[i])
                {
                    if (IsUnstarted(entities[i], handle)) _starting.Add((entities[i], handle));
                }
            }
        }

        // Each starts once its asset is there, fetched first when no prefab loaded it; while a domain loads in place of
        // the world, only an impatient one starts, until the new domain is readied.
        foreach ((Entity entity, Handle<Script> handle) in _starting)
        {
            if (!_loaded.TryGetValue(handle, out Result<Script>? known))
            {
                Fetch(handle);

                continue;
            }

            if (known is not { Ok: true, Value: { } script }) continue;

            if (_stream.State == StreamState.Loading && !IsImpatient(script.Type)) continue;

            IReadOnlyDictionary<string, object?> values =
                _pending.Remove((entity, handle), out IReadOnlyDictionary<string, object?>? given)
                    ? given
                    : ReadOnlyDictionary<string, object?>.Empty;
            Keep(entity, script, Start(entity, script, values));
        }

        // Values given for an entity that is gone are dropped; those for a script held back above stay for its start.
        foreach ((Entity Entity, Handle<Script> Script) waiting in _pending.Keys)
        {
            if (!_ecs.IsAlive(waiting.Entity)) _pending.Remove(waiting);
        }
    }

    /// <summary>
    /// What streams into the world changed (<see cref="WorldStream"/>): once the world runs again, the frame after it
    /// was first shown, the scripts held while it loaded run, their systems added to the ECS, from this frame on. One
    /// whose system cannot be added is an error, and its entity goes without it.
    /// </summary>
    private void FollowStream(WorldStream stream)
    {
        _stream = stream;

        if (stream.State != StreamState.Loaded) return;

        for (int i = 0; i < _attached.Count; i++)
        {
            Attached attached = _attached.GetAt(i).Value;

            if (!attached.IsHeld) continue;

            attached.IsHeld = false;
            if (TryRegisterSystems(attached) is { Ok: false } failed)
            {
                Debugging.Error($"Script {attached.Instance.GetType().Name} is left off entity {attached.Entity.Id}: "
                    + $"{failed.Error}");
                _failed.Add((attached.Entity, attached.Script));
                Drop(attached);
                i--;
            }
        }
    }

    /// <summary>
    /// Loads the script asset <paramref name="handle"/>, which an entity names and no prefab loaded, into
    /// <see cref="_loaded"/>; one that does not load is an error, said once, and the entities naming it go without it.
    /// </summary>
    private void Fetch(Handle<Script> handle)
    {
        _loaded[handle] = null;
        LoadAsync().FireAndForget();

        async Task LoadAsync()
        {
            Result<Script> loaded = await _assets.LoadAsync(handle, CancellationToken.None).ConfigureAwait(false);
            _fetched.Writer.TryWrite((handle, loaded));
        }
    }

    /// <summary>
    /// Runs <paramref name="attached"/>, when it was made; else logs why <paramref name="script"/>'s class is left off
    /// <paramref name="entity"/>, and leaves it off until it is attached again.
    /// </summary>
    private void Keep(Entity entity, Script script, Result<Attached> attached)
    {
        if (!attached.Ok)
        {
            Debugging.Error($"Script {script.Type.Name} is left off entity {entity.Id}: {attached.Error}");
            _failed.Add((entity, script.Handle));

            return;
        }

        _attached[(entity, script.Handle)] = attached.Value;
    }

    /// <summary>
    /// Ends <paramref name="attached"/> and forgets it.
    /// </summary>
    private void Drop(Attached attached)
    {
        End(attached);
        _attached.Remove((attached.Entity, attached.Script));
    }

    /// <summary>
    /// <paramref name="entity"/>'s own copy of <paramref name="script"/>: a new one made for the entity, given each of
    /// <paramref name="values"/>, the ones the prefab holds for it, started, and added to the ECS as each kind of
    /// system it is. Why not, when it cannot be made or throws as it starts.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a script throws as it starts, the entity goes without it, "
            + "and the others are started.")]
    private Result<Attached> Start(Entity entity, Script script, IReadOnlyDictionary<string, object?> values)
    {
        Type type = script.Type;
        Result<IScript> made = MakeOf(type, entity);

        if (!made.Ok) return Result.Failure<Attached>(made.Error);

        try
        {
            foreach ((string name, object? value) in values)
            {
                if (type.GetProperty(name) is { } property) property.SetValue(made.Value, value);
            }

            made.Value.Start();
        }
        catch (Exception ex)
        {
            return Result.Failure<Attached>($"it threw as it started: {Unwrap(ex)}");
        }

        // Held while the world loads, unless its class is impatient and the new domain is not yet being readied.
        bool isHeld = _stream.IsHeld || (_stream.State == StreamState.Loading && !IsImpatient(type));

        return Schedule(entity, script.Handle, values, made.Value, isHeld);
    }

    /// <summary>
    /// <paramref name="entity"/>'s own copy of <paramref name="script"/> as <paramref name="state"/> saved it: a new
    /// one made for the entity, each saved value set back on its property or field, not started, and held until the
    /// world put back with it runs, then added to the ECS as each kind of system it is. A value its class no longer has,
    /// or that no longer fits, is warned about once and left. Why not, when it cannot be made. Its values among what
    /// was saved are what a copy started again on a build is given.
    /// </summary>
    private Result<Attached> Put(Entity entity, Script script, ScriptState state)
    {
        Type type = script.Type;
        Result<IScript> made = MakeOf(type, entity);

        if (!made.Ok) return Result.Failure<Attached>(made.Error);

        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach ((string name, JsonElement json) in state.Values)
        {
            if (FindValue(type, name) is { } property)
            {
                if (TryRead(type, name, json, property.PropertyType, out object? value))
                {
                    property.SetValue(made.Value, value);
                    values[property.Name] = value;
                }
            }
            else if (FindField(type, name) is { } field)
            {
                if (TryRead(type, name, json, field.FieldType, out object? value)) field.SetValue(made.Value, value);
            }
            else
            {
                WarnOnce(
                    type,
                    name,
                    $"Script {type.Name}'s saved {name} is not put back, as its class no longer has it.");
            }
        }

        // A world put back is readied from the call that puts it back, before that is told: its scripts wait with it.
        return Schedule(entity, script.Handle, values, made.Value, isHeld: true);
    }

    /// <summary>
    /// Hands back <paramref name="made"/> whole, to run until its entity goes, added to the ECS as each kind of system
    /// it is; held instead when it <paramref name="isHeld"/>, its systems added once the world runs again
    /// (<see cref="FollowStream"/>). Why not, when adding one throws.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The script owns its systems, which end with it; when it is not added, they end as it fails.")]
    private Result<Attached> Schedule(
        Entity entity,
        Handle<Script> script,
        IReadOnlyDictionary<string, object?> values,
        IScript made,
        bool isHeld)
    {
        Attached attached = new(entity, script, values, made, new SubscriptionList()) { IsHeld = isHeld };

        return attached.IsHeld ? Result.Success(attached) : TryRegisterSystems(attached);
    }

    /// <summary>
    /// Registers <paramref name="attached"/>'s script with the ECS as each kind of system it is, into its systems; why
    /// not, when registering one throws, none of them left registered.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever registering a script's system throws, the entity goes without the script, "
            + "and the others are started.")]
    private Result<Attached> TryRegisterSystems(Attached attached)
    {
        try
        {
            foreach (MethodInfo register in GetRegisters(attached.Instance.GetType()))
            {
                if (register.Invoke(_ecs, [attached.Instance]) is Subscription system) attached.Systems.Add(system);
            }

            return Result.Success(attached);
        }
        catch (Exception ex)
        {
            attached.Systems.Dispose();

            return Result.Failure<Attached>($"it threw as it started: {Unwrap(ex)}");
        }
    }

    /// <summary>
    /// <paramref name="script"/> as it is now, as JSON by name, for <see cref="Save"/>: its values
    /// (<see cref="GetValues"/>) as a file names them, and its other fields by name, but those that are no data of its
    /// own (<see cref="IsHanded"/>) and those JSON cannot hold, warned about once.
    /// </summary>
    private Dictionary<string, JsonElement> GetState(IScript script)
    {
        Type type = script.GetType();
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        HashSet<string> backing = new(StringComparer.Ordinal);

        // Its values first, each one's backing field noted so the fields below do not save it twice.
        foreach (PropertyInfo property in GetValues(type))
        {
            backing.Add($"<{property.Name}>k__BackingField");
            Write(values, type, GetName(property), property.PropertyType, property.GetValue(script));
        }

        foreach (FieldInfo field in type.GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!backing.Contains(field.Name) && !IsHanded(field.FieldType))
                Write(values, type, GetName(field), field.FieldType, field.GetValue(script));
        }

        return values;
    }

    /// <summary>
    /// Writes <paramref name="value"/>, a <paramref name="type"/>'s member <paramref name="name"/> of
    /// <paramref name="valueType"/>, into <paramref name="values"/> as JSON; a value JSON cannot hold is warned about
    /// once and left out.
    /// </summary>
    private void Write(Dictionary<string, JsonElement> values, Type type, string name, Type valueType, object? value)
    {
        Result<JsonElement> json = Json.Write(value, valueType);
        if (json.Ok)
        {
            values[name] = json.Value;
        }
        else
        {
            WarnOnce(
                type,
                name,
                $"Script {type.Name}'s {name} is not saved, so a copy put back starts over without it: {json.Error}");
        }
    }

    /// <summary>
    /// Reads <paramref name="json"/>, a saved value of <paramref name="type"/>'s member <paramref name="name"/>, as
    /// <paramref name="valueType"/>; false, warned about once, when it no longer fits.
    /// </summary>
    private bool TryRead(Type type, string name, JsonElement json, Type valueType, out object? value)
    {
        if (json.ValueKind == JsonValueKind.Null)
        {
            // A reference the script had none of: what it is put back as.
            value = null;

            return true;
        }

        Result<object> read = Json.Read(json, valueType);
        if (read.Ok)
        {
            value = read.Value;

            return true;
        }

        WarnOnce(type, name, $"Script {type.Name}'s saved {name} is not put back, as it no longer fits: {read.Error}");
        value = null;

        return false;
    }

    /// <summary>
    /// Warns <paramref name="message"/> once for <paramref name="type"/>'s member <paramref name="name"/>, which could
    /// not be saved or put back, so a world saved or put back again says it no more.
    /// </summary>
    private void WarnOnce(Type type, string name, string message)
    {
        if (_unkept.Add($"{type.Name}.{name}")) Debugging.Warn(message);
    }

    /// <summary>
    /// <paramref name="script"/> was read again, as the code it is in was built: each live copy of it ends, and a new
    /// one of its class as it is now starts in its place, for the same entity with the same values, so an edit shows
    /// without the entity being spawned again. What a copy kept in its own fields starts over. One read again as the
    /// class it was, as when its file is saved before it is built, runs on. Main thread.
    /// </summary>
    private void Restart(Script script)
    {
        if (_loaded.ContainsKey(script.Handle)) _loaded[script.Handle] = Result.Success(script);

        // Gathered first, as ending a copy takes it out of the scripts being walked.
        List<Attached> restarting = [];
        foreach (Attached attached in _attached.Values)
        {
            if (attached.Script == script.Handle && attached.Instance.GetType() != script.Type)
                restarting.Add(attached);
        }

        foreach (Attached attached in restarting)
        {
            Drop(attached);
            if (_ecs.IsAlive(attached.Entity))
                Keep(attached.Entity, script, Start(attached.Entity, script, attached.Values));
        }
    }

    /// <summary>
    /// Finds where the player's cameras are, those tagged <see cref="Tag.Player"/>, for this frame's behaviours to be
    /// paced by: a camera drawing into a mirror or a minimap is not where the player looks from.
    /// </summary>
    private void FindCameras()
    {
        _cameras.Clear();
        foreach ((_, Span<Transform> transforms, _, Span<Tags> tags) in _ecs.Query<Transform, Camera, Tags>())
        {
            for (int i = 0; i < transforms.Length; i++)
            {
                if (tags[i].Has(Tag.Player)) _cameras.Add(transforms[i].Position);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="entity"/>'s behaviours are called in frame <paramref name="index"/>'s Update and
    /// LateUpdate: every frame within <see cref="ScriptingSettings.FullRateDistance"/> of the nearest of the player's
    /// cameras, and up to n times as far every nth, as what is twice as far moves half as far on screen, up to every
    /// <see cref="ScriptingSettings.MaxInterval"/>th. Entities take turns by their id, so the far ones do not all fall
    /// on one frame. Every frame with no player camera, or for an entity without a <see cref="Transform"/>.
    /// </summary>
    private bool IsDue(Entity entity, ulong index)
    {
        if (_cameras.Count == 0 || !_ecs.Has<Transform>(entity)) return true;

        Vector3 position = _ecs.Get<Transform>(entity).Position;
        float nearest = float.PositiveInfinity;
        foreach (Vector3 camera in _cameras)
        {
            nearest = MathF.Min(nearest, (position - camera).LengthSquared());
        }

        // Not a number when there is no distance to divide (a camera's own script, with a full-rate distance of 0).
        float interval = MathF.Min(
            MathF.Ceiling(MathF.Sqrt(nearest) / _settings.FullRateDistance),
            _settings.MaxInterval);

        return interval < 2 || float.IsNaN(interval) || (index + (ulong)entity.Id) % (ulong)interval == 0;
    }

    /// <summary>
    /// Calls the hook for <paramref name="phase"/> of each behaviour that runs this frame (<see cref="IsRunning"/>), in
    /// the order they were made (<see cref="TryCall"/>). A script whose entity is gone ends first; a behaviour that
    /// throws is logged and ends, and the others go on.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a behaviour throws, it is logged and the behaviour ends; the others go on.")]
    private void Run(Phase phase, in Frame frame)
    {
        int calls = 0;
        for (int i = 0; i < _attached.Count; i++)
        {
            Attached attached = _attached.GetAt(i).Value;
            if (!_ecs.IsAlive(attached.Entity))
            {
                Drop(attached);
                i--;

                continue;
            }

            if (!IsRunning(attached, frame) || attached.Behavior is not { } behavior) continue;

            try
            {
                if (TryCall(attached, behavior, phase, frame)) calls++;
            }
            catch (Exception ex)
            {
                Debugging.Error($"Script {behavior.GetType().Name} on entity {attached.Entity.Id} threw in {phase} "
                    + $"and was taken off: {ex}");
                _failed.Add((attached.Entity, attached.Script));
                Drop(attached);
                i--;
            }
        }

        _calls.Add(calls, new KeyValuePair<string, object?>("magic.gems.scripting.phase", _phases[(int)phase]));
        _behaviors.Record(_attached.Count);
    }

    /// <summary>
    /// The behaviour's hook for <paramref name="phase"/>: FixedUpdate every step; Update and LateUpdate only on a frame
    /// the behaviour is due, decided in Update (<see cref="IsDue"/>), with the time since its last call as the deltas
    /// (<see cref="GetCallFrame"/>), LateUpdate, the last of the two, then marking it called. Whether it ran.
    /// </summary>
    private bool TryCall(Attached attached, IBehavior behavior, Phase phase, in Frame frame)
    {
        if (phase == Phase.FixedUpdate)
        {
            behavior.FixedUpdate(frame);

            return true;
        }

        if (phase == Phase.Update) attached.IsDue = IsDue(attached.Entity, frame.Index);

        if (!attached.IsDue) return false;

        if (phase == Phase.Update)
        {
            behavior.Update(GetCallFrame(attached, frame));

            return true;
        }

        behavior.LateUpdate(GetCallFrame(attached, frame));
        attached.LastCalled = frame;

        return true;
    }

    /// <summary>
    /// Whether <paramref name="attached"/>'s hooks run this frame: not while it is held for the world loading, and while
    /// the world is paused only when its class is <see cref="UnpausableAttribute"/> or its entity is tagged
    /// <see cref="Tag.Unpausable"/>.
    /// </summary>
    private bool IsRunning(Attached attached, in Frame frame)
    {
        if (attached.IsHeld) return false;

        return !frame.IsPaused
            || IsUnpausable(attached.Instance.GetType())
            || (_ecs.Has<Tags>(attached.Entity) && _ecs.Get<Tags>(attached.Entity).Has(Tag.Unpausable));
    }

    /// <summary>
    /// Whether the entity's script has yet to start: named, not attached and not failed.
    /// </summary>
    private bool IsUnstarted(Entity entity, Handle<Script> handle)
        => handle != Handle<Script>.None
            && !_attached.ContainsKey((entity, handle))
            && !_failed.Contains((entity, handle));

    /// <summary>
    /// A script made for <see cref="Entity"/>: which script asset, the values it was given, which a copy started in its
    /// place on a build is given too, the copy itself, and its places on the ECS, one for each kind of system it is.
    /// </summary>
    private sealed record Attached(
        Entity Entity,
        Handle<Script> Script,
        IReadOnlyDictionary<string, object?> Values,
        IScript Instance,
        SubscriptionList Systems)
    {
        /// <summary>
        /// The script as a behaviour, when it is one: the one kind whose hooks this gem calls.
        /// </summary>
        internal IBehavior? Behavior { get; } = Instance as IBehavior;

        /// <summary>
        /// Whether its Update and LateUpdate are called this frame, decided in Update; a script attached after that
        /// runs its first LateUpdate.
        /// </summary>
        internal bool IsDue { get; set; } = true;

        /// <summary>
        /// Started while the world loaded, or put back with it, it waits until the world runs again: its hooks are not
        /// called and its systems are not on the ECS (<see cref="FollowStream"/>).
        /// </summary>
        internal bool IsHeld { get; set; }

        /// <summary>
        /// The frame its Update and LateUpdate were last called in, which its next call's deltas are measured from
        /// (<see cref="GetCallFrame"/>); none before its first.
        /// </summary>
        internal Frame? LastCalled { get; set; }
    }
}
