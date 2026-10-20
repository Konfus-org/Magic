using System.Collections.ObjectModel;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Magic.Attributes;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
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
/// longer names it, or is gone, ends. Nothing starts while a domain loads in place of the world
/// (<see cref="WorldLoading"/>), unless its class is <see cref="ImpatientAttribute"/>.
/// A system goes on the <see cref="IEcs"/>, through the Add that takes its kind; an <see cref="IBehavior"/> has its
/// hooks called in this gem's. A pipeline script is made the same way for no entity, and never asks for one. A script
/// read again, as the code it is in was built, has each live copy ended and a new one of its class started in its
/// place, for the same entity with the same values. A script is saved with the world as its values and the fields it
/// kept for itself, as JSON, and put back from them without being started, as it is as it was.
/// <para>
/// A behaviour far from the player's cameras, those tagged <see cref="Tag.Player"/>, is updated less often: Update and
/// LateUpdate run every frame within <see cref="ScriptingSettings.FullRateDistance"/> of the nearest, and up to n times as
/// far every nth frame, up to every <see cref="ScriptingSettings.MaxInterval"/>th, with the time since the last call as
/// its frame's delta. FixedUpdate is never held back, nor is an entity without a <see cref="Transform"/>, which is nowhere.
/// </para>
/// </summary>
internal sealed class ScriptingGem : IGem, IScripts, IAssetLoader<Script>, IDisposable
{
    /// <summary>
    /// Each of the <see cref="IEcs"/>'s Adds, one per kind of system: a class that implements the interface one takes
    /// is a system of that kind.
    /// </summary>
    private static readonly MethodInfo[] _adds = Array.FindAll(typeof(IEcs).GetMethods(),
        method => string.Equals(method.Name, nameof(IEcs.Add), StringComparison.Ordinal) && method.GetParameters().Length == 1);

    private static readonly Meter _meter = new("Magic.Gems.Scripting");
    private static readonly Counter<int> _calls = _meter.CreateCounter<int>("magic.gems.scripting.behavior.calls", "{call}", "Behaviour hooks called, tagged with the phase.");
    private static readonly Gauge<int> _behaviors = _meter.CreateGauge<int>("magic.gems.scripting.behaviors", "{behavior}", "Behaviours attached to live entities.");
    private static readonly string[] _phases = Enum.GetNames<Phase>();

    private readonly ScriptingSettings _settings;
    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly Assemblies _assemblies;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Core's, which Core ends after every gem.")]
    private readonly Threads _threads;
    private readonly IServiceProvider _services;
    private readonly SubscriptionList _watching = new();

    /// <summary>
    /// The scripts made, by the entity and the script asset of each, in the order they were made. Main thread.
    /// </summary>
    private readonly OrderedDictionary<(Entity Entity, Handle<Script> Script), Attached> _attached = [];

    /// <summary>
    /// The values given for scripts not started yet (<see cref="Attach"/>), by entity and script asset. Main thread.
    /// </summary>
    private readonly Dictionary<(Entity Entity, Handle<Script> Script), IReadOnlyDictionary<string, object?>> _pending = [];

    /// <summary>
    /// Every script asset named: as the prefab that attached it loaded it, or fetched when nothing gave values for it
    /// (<see cref="Fetch"/>), null while that load is on its way, and why not when it did not load, so the entities
    /// naming it go without it until it is attached again or the code is built again. The one place a script's class
    /// is kept, so a build replaces it once (<see cref="Restart"/>). Main thread.
    /// </summary>
    private readonly Dictionary<Handle<Script>, Result<Script>?> _loaded = [];

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
    /// Whether a domain is loading in place of the world, as last told (<see cref="WorldLoading"/>): a script that is
    /// not <see cref="ImpatientAttribute"/> waits while one is. Main thread.
    /// </summary>
    private bool _isLoading;

    public ScriptingGem(ScriptingSettings settings, IEcs ecs, Assets assets, Assemblies assemblies, Threads threads, Events events, IServiceProvider services)
    {
        _settings = settings;
        _ecs = ecs;
        _assets = assets;
        _assemblies = assemblies;
        _threads = threads;
        _services = services;
        _watching.Add(events.Watch<AssetReloaded<Script>>(reloaded => _threads.Post(EngineThread.Main, () => Restart(reloaded.Asset))));
        _watching.Add(events.Watch<WorldLoading>(loading => _isLoading = loading.IsLoading));
    }

    public void Dispose()
    {
        _watching.Dispose();

        // Scripts end on the main thread, the ECS's, which still runs what it is handed while the gems end.
        _threads.Post(EngineThread.Main, EndAll);
    }

    /// <summary>
    /// The <c>.cs</c> file at <paramref name="path"/> as a <see cref="Script"/>: the class named like the file, an
    /// <see cref="IScript"/>, found in the code loaded (<see cref="Assemblies"/>). The file's bytes are not read: its
    /// class is compiled with the project's code, or the gem's; why not, when no code loaded declares the class, or
    /// its <c>.meta</c> holds settings, which a script takes none of. Any thread.
    /// </summary>
    public ValueTask<Result<Script>> LoadAsync(string path, ReadOnlyMemory<byte> bytes, JsonElement settings, CancellationToken cancel)
    {
        if (settings.GetPropertyCount() != 0)
        {
            return new(Result.Failure<Script>($"its .meta holds settings, {settings.GetRawText()}, which a script file does not take."));
        }

        Result<Type> type = _assemblies.Get<IScript>(Path.GetFileNameWithoutExtension(path));
        return new(type.Ok ? Result.Success(new Script { Type = type.Value }) : Result.Failure<Script>(type.Error));
    }

    public Result<IScript> Make(Script script)
    {
        return MakeOf(script.Type, entity: null);
    }

    public void Attach(Entity entity, PrefabScript script)
    {
        if (!_ecs.IsAlive(entity))
        {
            return;
        }

        Handle<Script> handle = script.Script.Handle;
        Scripts scripts = _ecs.Has<Scripts>(entity) ? _ecs.Get<Scripts>(entity) : default;
        if (!scripts.Has(handle))
        {
            if (!scripts.Add(handle))
            {
                Debugging.Error($"Script {script.Script.Type.Name} is left off entity {entity.Id}: an entity has at most {Scripts.Capacity} scripts.");
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
        if (!_ecs.Has<Scripts>(entity))
        {
            return;
        }

        Scripts scripts = _ecs.Get<Scripts>(entity);
        foreach (Handle<Script> handle in scripts)
        {
            if (_attached.TryGetValue((entity, handle), out Attached? attached))
            {
                into.Add(new ScriptState(handle, StateOf(attached.Instance)));
            }
        }
    }

    public void Restore(Entity entity, ScriptState state)
    {
        if (!_ecs.IsAlive(entity))
        {
            return;
        }

        // The asset is loaded as any named one is, so it is held and told of when the code is built again; its class is
        // found now, as a load reads no file, since the world is put back in this call.
        Handle<Script> handle = state.Script;
        if (!_loaded.ContainsKey(handle))
        {
            Fetch(handle);
        }

        Result<Script> script = _loaded[handle] ?? Find(handle);
        if (script.Ok)
        {
            Keep(entity, script.Value, Put(entity, script.Value, state));
        }
        else
        {
            Debugging.Error($"Script {NameOf(handle)} is left off entity {entity.Id}: {script.Error}");
            _failed.Add((entity, handle));
        }
    }

    public void Update(in Frame frame)
    {
        Reconcile();
        FindCameras();
        Run(Phase.Update, frame);
    }

    public void FixedUpdate(in Frame frame)
    {
        Run(Phase.FixedUpdate, frame);
    }

    public void LateUpdate(in Frame frame)
    {
        Run(Phase.LateUpdate, frame);
    }

    /// <summary>
    /// The <see cref="IEcs"/>'s Add for each kind of system <paramref name="type"/> is, made for the components it
    /// names; none for a class that is no system.
    /// </summary>
    private static List<MethodInfo> AddsOf(Type type)
    {
        List<MethodInfo> adds = [];
        foreach (Type contract in type.GetInterfaces())
        {
            foreach (MethodInfo add in _adds)
            {
                Type taken = add.GetParameters()[0].ParameterType;
                if (taken == contract)
                {
                    adds.Add(add);
                }
                else if (add.IsGenericMethodDefinition && contract.IsGenericType && taken.IsGenericType
                    && taken.GetGenericTypeDefinition() == contract.GetGenericTypeDefinition())
                {
                    adds.Add(add.MakeGenericMethod(contract.GetGenericArguments()));
                }
            }
        }

        return adds;
    }

    /// <summary>
    /// Takes <paramref name="attached"/>'s systems off the ECS, and disposes it when it is disposable.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever a script throws as it ends, it is logged and the others still end.")]
    private static void End(Attached attached)
    {
        attached.Systems.Dispose();

        if (attached.Instance is not IDisposable disposable)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            Debugging.Error($"Script {attached.Instance.GetType().Name} on entity {attached.Entity.Id} threw as it ended: {ex}");
        }
    }

    /// <summary>
    /// A new <paramref name="type"/> for <paramref name="entity"/>, or for none: its one public constructor given the
    /// entity first, when there is one, then a service for each parameter, and a parameter's default when nothing
    /// provides it. Why not, when it has no such constructor, its first parameter is not the entity it is made for, it
    /// asks for an entity when it is made for none or anywhere but first, asks for what nothing provides, or throws.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever a script throws as it is made, it is not made, and the others are.")]
    private Result<IScript> MakeOf(Type type, Entity? entity)
    {
        ConstructorInfo[] constructors = type.GetConstructors();
        if (constructors.Length != 1)
        {
            return Result.Failure<IScript>($"it must have exactly one public constructor; it has {constructors.Length}.");
        }

        ParameterInfo[] parameters = constructors[0].GetParameters();
        if (entity is not null && parameters.Length > 0 && parameters[0].ParameterType != typeof(Entity))
        {
            return Result.Failure<IScript>("it has an invalid constructor: a script's first parameter is the Entity it is attached to, before the services it asks for.");
        }

        object?[] arguments = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            Type wanted = parameters[i].ParameterType;
            if (wanted == typeof(Entity))
            {
                if (i != 0 || entity is null)
                {
                    return Result.Failure<IScript>("its constructor asks for an Entity where none is given: a script takes the entity it is attached to first and once, and one made for no entity, such as a pipeline script, never takes one.");
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
                return Result.Failure<IScript>($"its constructor asks for a {wanted.Name}, which no gem the project lists provides.");
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
            return Result.Failure<IScript>($"it threw as it was made: {(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex)}");
        }
    }

    /// <summary>
    /// What <paramref name="ex"/> was: what a script threw, when reflection wrapped it.
    /// </summary>
    private static Exception Thrown(Exception ex)
    {
        return ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
    }

    /// <summary>
    /// <paramref name="type"/>'s public property with a getter and a setter that a file names <paramref name="key"/>,
    /// in Core's dialect: one of its values. Null when it has none.
    /// </summary>
    private static PropertyInfo? ValueNamed(Type type, string key)
    {
        foreach (PropertyInfo property in ValuesOf(type))
        {
            if (string.Equals(NameOf(property), key, StringComparison.Ordinal))
            {
                return property;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="type"/>'s public properties with a getter and a setter: its values, which a prefab sets.
    /// </summary>
    private static List<PropertyInfo> ValuesOf(Type type)
    {
        List<PropertyInfo> values = [];
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is { IsPublic: true } && property.SetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0)
            {
                values.Add(property);
            }
        }

        return values;
    }

    /// <summary>
    /// <paramref name="type"/>'s field saved as <paramref name="name"/> (<see cref="NameOf(FieldInfo)"/>): its own of
    /// that name, else the one the compiler made for a property or a primary constructor parameter of it. Null when
    /// it has none.
    /// </summary>
    private static FieldInfo? FieldNamed(Type type, string name)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        return type.GetField(name, Any) ?? type.GetField($"<{name}>k__BackingField", Any) ?? type.GetField($"<{name}>P", Any);
    }

    /// <summary>
    /// A value's name as a file gives it, in Core's dialect.
    /// </summary>
    private static string NameOf(PropertyInfo property)
    {
        return Json.Options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;
    }

    /// <summary>
    /// A field's name as it is saved: its own, or for one the compiler made, the name of the property or primary
    /// constructor parameter it is for, which is between the angle brackets.
    /// </summary>
    private static string NameOf(FieldInfo field)
    {
        int end = field.Name.IndexOf('>', StringComparison.Ordinal);
        return field.Name.StartsWith('<') && end > 1 ? field.Name[1..end] : field.Name;
    }

    /// <summary>
    /// A script asset's path, for messages; its id when no asset has it.
    /// </summary>
    private string NameOf(Handle<Script> script)
    {
        return _assets.Path(script) ?? script.Id.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The script asset <paramref name="handle"/> found now, its class in the code loaded as <see cref="LoadAsync"/>
    /// finds it, for a world put back, which does not wait for a load; why not, when no asset has its id or no code
    /// loaded declares the class.
    /// </summary>
    private Result<Script> Find(Handle<Script> handle)
    {
        string? path = _assets.Path(handle);
        if (path is null)
        {
            return Result.Failure<Script>("no asset has its id.");
        }

        Result<Type> type = _assemblies.Get<IScript>(Path.GetFileNameWithoutExtension(path));
        return type.Ok ? Result.Success(new Script { Handle = handle, Type = type.Value }) : Result.Failure<Script>(type.Error);
    }

    /// <summary>
    /// Whether a field of <paramref name="type"/> is no data of the script's own: a service its constructor was handed,
    /// or something made rather than kept, a delegate, a task or a disposable, which a copy put back makes again.
    /// </summary>
    private bool IsHanded(Type type)
    {
        return typeof(Delegate).IsAssignableFrom(type) || typeof(Task).IsAssignableFrom(type) || typeof(IDisposable).IsAssignableFrom(type)
            || (!type.IsValueType && _services.GetService(type) is not null);
    }

    /// <summary>
    /// Makes the scripts what each entity's <see cref="Scripts"/> says: ends a copy whose entity no longer names its
    /// script, or is gone, and starts one for each script named that has none, with the values given for it
    /// (<see cref="Attach"/>) or its defaults, unless the world is loading and the class is not
    /// <see cref="ImpatientAttribute"/>. An asset no prefab loaded is fetched first (<see cref="Fetch"/>), and the
    /// script starts once it has. The starts come after the query, as a start may spawn. Main thread, before the
    /// frame's behaviours run.
    /// </summary>
    private void Reconcile()
    {
        for (int i = 0; i < _attached.Count; i++)
        {
            Attached attached = _attached.GetAt(i).Value;
            if (!_ecs.IsAlive(attached.Entity) || !_ecs.Has<Scripts>(attached.Entity) || !_ecs.Get<Scripts>(attached.Entity).Has(attached.Script))
            {
                Drop(attached);
                i--;
            }
        }

        _starting.Clear();
        foreach ((ReadOnlySpan<Entity> entities, Span<Scripts> scripts) in _ecs.Query<Scripts>())
        {
            for (int i = 0; i < entities.Length; i++)
            {
                foreach (Handle<Script> handle in scripts[i])
                {
                    if (handle != Handle<Script>.None && !_attached.ContainsKey((entities[i], handle)) && !_failed.Contains((entities[i], handle)))
                    {
                        _starting.Add((entities[i], handle));
                    }
                }
            }
        }

        foreach ((Entity entity, Handle<Script> handle) in _starting)
        {
            if (!_loaded.TryGetValue(handle, out Result<Script>? known))
            {
                Fetch(handle);
                continue;
            }

            if (known is not { Ok: true, Value: { } script })
            {
                continue;
            }

            if (_isLoading && !script.Type.IsDefined(typeof(ImpatientAttribute), inherit: false))
            {
                continue;
            }

            IReadOnlyDictionary<string, object?> values = _pending.Remove((entity, handle), out IReadOnlyDictionary<string, object?>? given) ? given : ReadOnlyDictionary<string, object?>.Empty;
            Keep(entity, script, Start(entity, script, values));
        }

        // Values given for an entity that is gone are dropped; those for a script held back above stay for its start.
        foreach ((Entity Entity, Handle<Script> Script) waiting in _pending.Keys)
        {
            if (!_ecs.IsAlive(waiting.Entity))
            {
                _pending.Remove(waiting);
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
            _threads.Post(EngineThread.Main, () =>
            {
                _loaded[handle] = loaded;
                if (!loaded.Ok)
                {
                    Debugging.Error($"Script {NameOf(handle)} is left off the entities naming it: {loaded.Error}");
                }
            });
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
    /// <paramref name="values"/>, the ones the prefab holds for it, started, and added to the ECS as each kind of system
    /// it is. Why not, when it cannot be made or throws as it starts.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever a script throws as it starts, the entity goes without it, and the others are started.")]
    private Result<Attached> Start(Entity entity, Script script, IReadOnlyDictionary<string, object?> values)
    {
        Type type = script.Type;
        Result<IScript> made = MakeOf(type, entity);
        if (!made.Ok)
        {
            return Result.Failure<Attached>(made.Error);
        }

        try
        {
            foreach ((string name, object? value) in values)
            {
                if (type.GetProperty(name) is { } property)
                {
                    property.SetValue(made.Value, value);
                }
            }

            made.Value.Start();
        }
        catch (Exception ex)
        {
            return Result.Failure<Attached>($"it threw as it started: {Thrown(ex)}");
        }

        return Schedule(entity, script.Handle, values, made.Value);
    }

    /// <summary>
    /// <paramref name="entity"/>'s own copy of <paramref name="script"/> as <paramref name="state"/> saved it: a new
    /// one made for the entity, each saved value set back on its property or field, not started, and added to the ECS
    /// as each kind of system it is. A value its class no longer has, or that no longer fits, is warned about once and
    /// left. Why not, when it cannot be made. Its values among what was saved are what a copy started again on a build
    /// is given.
    /// </summary>
    private Result<Attached> Put(Entity entity, Script script, ScriptState state)
    {
        Type type = script.Type;
        Result<IScript> made = MakeOf(type, entity);
        if (!made.Ok)
        {
            return Result.Failure<Attached>(made.Error);
        }

        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach ((string name, JsonElement json) in state.Values)
        {
            if (ValueNamed(type, name) is { } property)
            {
                if (TryRead(type, name, json, property.PropertyType, out object? value))
                {
                    property.SetValue(made.Value, value);
                    values[property.Name] = value;
                }
            }
            else if (FieldNamed(type, name) is { } field)
            {
                if (TryRead(type, name, json, field.FieldType, out object? value))
                {
                    field.SetValue(made.Value, value);
                }
            }
            else if (_unkept.Add($"{type.Name}.{name}"))
            {
                Debugging.Warn($"Script {type.Name}'s saved {name} is not put back, as its class no longer has it.");
            }
        }

        return Schedule(entity, script.Handle, values, made.Value);
    }

    /// <summary>
    /// Adds <paramref name="made"/> to the ECS as each kind of system it is, and hands back the whole, to run until
    /// its entity goes; why not, when adding one throws.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever adding a script's system throws, the entity goes without the script, and the others are started.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The script owns its systems, which end with it; when it is not added, they end here.")]
    private Result<Attached> Schedule(Entity entity, Handle<Script> script, IReadOnlyDictionary<string, object?> values, IScript made)
    {
        SubscriptionList systems = new();
        try
        {
            foreach (MethodInfo add in AddsOf(made.GetType()))
            {
                if (add.Invoke(_ecs, [made]) is Subscription system)
                {
                    systems.Add(system);
                }
            }

            return Result.Success(new Attached(entity, script, values, made, systems));
        }
        catch (Exception ex)
        {
            systems.Dispose();
            return Result.Failure<Attached>($"it threw as it started: {Thrown(ex)}");
        }
    }

    /// <summary>
    /// <paramref name="script"/> as it is now, as JSON by name, for <see cref="Save"/>: its values
    /// (<see cref="ValuesOf"/>) as a file names them, and its other fields by name, but those that are no data of its
    /// own (<see cref="IsHanded"/>) and those JSON cannot hold, warned about once.
    /// </summary>
    private Dictionary<string, JsonElement> StateOf(object script)
    {
        Type type = script.GetType();
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        HashSet<string> backing = new(StringComparer.Ordinal);
        foreach (PropertyInfo property in ValuesOf(type))
        {
            backing.Add($"<{property.Name}>k__BackingField");
            Write(values, type, NameOf(property), property.PropertyType, property.GetValue(script));
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!backing.Contains(field.Name) && !IsHanded(field.FieldType))
            {
                Write(values, type, NameOf(field), field.FieldType, field.GetValue(script));
            }
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
        else if (_unkept.Add($"{type.Name}.{name}"))
        {
            Debugging.Warn($"Script {type.Name}'s {name} is not saved, so a copy put back starts over without it: {json.Error}");
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

        if (_unkept.Add($"{type.Name}.{name}"))
        {
            Debugging.Warn($"Script {type.Name}'s saved {name} is not put back, as it no longer fits: {read.Error}");
        }

        value = null;
        return false;
    }

    /// <summary>
    /// <paramref name="script"/> was read again, as the code it is in was built: each live copy of it ends, and a new
    /// one of its class as it is now starts in its place, for the same entity with the same values, so an edit shows
    /// without the entity being spawned again. What a copy kept in its own fields starts over. One read again as the
    /// class it was, as when its file is saved before it is built, runs on. Main thread.
    /// </summary>
    private void Restart(Script script)
    {
        if (_loaded.ContainsKey(script.Handle))
        {
            _loaded[script.Handle] = Result.Success(script);
        }

        List<Attached> restarting = [];
        foreach (Attached attached in _attached.Values)
        {
            if (attached.Script == script.Handle && attached.Instance.GetType() != script.Type)
            {
                restarting.Add(attached);
            }
        }

        foreach (Attached attached in restarting)
        {
            Drop(attached);
            if (_ecs.IsAlive(attached.Entity))
            {
                Keep(attached.Entity, script, Start(attached.Entity, script, attached.Values));
            }
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
                if (tags[i].Has(Tag.Player))
                {
                    _cameras.Add(transforms[i].Position);
                }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="entity"/>'s behaviours are called in frame <paramref name="index"/>'s Update and
    /// LateUpdate: every frame within <see cref="ScriptingSettings.FullRateDistance"/> of the nearest of the player's
    /// cameras, and up to n times as far every nth, as what is twice as far moves half as far on screen, up to every
    /// <see cref="ScriptingSettings.MaxInterval"/>th. Entities take turns by their id, so the far ones do not all fall on
    /// one frame. Every frame with no player camera, or for an entity without a <see cref="Transform"/>.
    /// </summary>
    private bool IsDue(Entity entity, ulong index)
    {
        if (_cameras.Count == 0 || !_ecs.Has<Transform>(entity))
        {
            return true;
        }

        Vector3 position = _ecs.Get<Transform>(entity).Position;
        float nearest = float.PositiveInfinity;
        foreach (Vector3 camera in _cameras)
        {
            nearest = MathF.Min(nearest, (position - camera).LengthSquared());
        }

        // Not a number when there is no distance to divide (a camera's own script, with a full-rate distance of 0).
        float interval = MathF.Min(MathF.Ceiling(MathF.Sqrt(nearest) / _settings.FullRateDistance), _settings.MaxInterval);
        return interval < 2 || float.IsNaN(interval) || (index + (ulong)entity.Id) % (ulong)interval == 0;
    }

    /// <summary>
    /// Calls each behaviour's hook for <paramref name="phase"/>, in the order they were made: FixedUpdate every step, and
    /// Update and LateUpdate when the behaviour is due, decided once a frame in Update (<see cref="IsDue"/>), with the
    /// time since the behaviour's last call as the frame's delta. A script whose entity is gone ends first; a behaviour
    /// that throws is logged and ends, and the others go on.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever a behaviour throws, it is logged and the behaviour ends; the others go on.")]
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

            if (attached.Behavior is not { } behavior)
            {
                continue;
            }

            try
            {
                if (phase == Phase.Update)
                {
                    attached.IsDue = IsDue(attached.Entity, frame.Index);
                    attached.UpdateWaited += frame.DeltaSeconds;
                    if (attached.IsDue)
                    {
                        behavior.Update(frame with { DeltaSeconds = attached.UpdateWaited });
                        calls++;
                        attached.UpdateWaited = 0;
                    }
                }
                else if (phase == Phase.FixedUpdate)
                {
                    behavior.FixedUpdate(frame);
                    calls++;
                }
                else
                {
                    attached.LateUpdateWaited += frame.DeltaSeconds;
                    if (attached.IsDue)
                    {
                        behavior.LateUpdate(frame with { DeltaSeconds = attached.LateUpdateWaited });
                        calls++;
                        attached.LateUpdateWaited = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Debugging.Error($"Script {behavior.GetType().Name} on entity {attached.Entity.Id} threw in {phase} and was taken off: {ex}");
                _failed.Add((attached.Entity, attached.Script));
                Drop(attached);
                i--;
            }
        }

        _calls.Add(calls, new KeyValuePair<string, object?>("magic.gems.scripting.phase", _phases[(int)phase]));
        _behaviors.Record(_attached.Count);
    }

    /// <summary>
    /// Ends every script, as the gem ends. On the main thread.
    /// </summary>
    private void EndAll()
    {
        foreach (Attached attached in _attached.Values)
        {
            End(attached);
        }

        _attached.Clear();
        _pending.Clear();
    }

    /// <summary>
    /// A script made for <see cref="Entity"/>: which script asset, the values it was given, which a copy started in its
    /// place on a build is given too, the copy itself, and its places on the ECS, one for each kind of system it is.
    /// </summary>
    private sealed record Attached(Entity Entity, Handle<Script> Script, IReadOnlyDictionary<string, object?> Values, object Instance, SubscriptionList Systems)
    {
        /// <summary>
        /// The script as a behaviour, when it is one: the one kind whose hooks this gem calls.
        /// </summary>
        public IBehavior? Behavior { get; } = Instance as IBehavior;

        /// <summary>
        /// Whether its Update and LateUpdate are called this frame, decided in Update; a script attached after that runs
        /// its first LateUpdate.
        /// </summary>
        public bool IsDue { get; set; } = true;

        /// <summary>
        /// Seconds since its Update was last called, which the next call is handed as its frame's delta.
        /// </summary>
        public float UpdateWaited { get; set; }

        /// <summary>
        /// As <see cref="UpdateWaited"/>, for LateUpdate.
        /// </summary>
        public float LateUpdateWaited { get; set; }
    }
}
