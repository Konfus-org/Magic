using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Magic.Contexts.Assets;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Scripting;

/// <summary>
/// Makes and runs scripts (<see cref="IScript"/>): provides <see cref="IScripts"/>, and reads <c>.cs</c> files
/// (<see cref="IAssetLoader{T}"/> of <see cref="Script"/>) as the class of the file's name that Magic or a loaded gem
/// compiled. An entity is given its own copy of a script as it is attached, which the <see cref="World"/> does once
/// whatever spawned with the entity is all there: made for the entity, its constructor given it first and then the
/// services it asks for, given the values the prefab holds for it, then started. A system goes on the
/// <see cref="IEcs"/>, through the Add that takes its kind; an <see cref="IBehavior"/> has its hooks called in this gem's.
/// Each ends with its entity. A pipeline script is made the same way for no entity, and never asks for one. A script
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
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Core's, which Core ends after every gem.")]
    private readonly Threads _threads;
    private readonly IServiceProvider _services;
    private readonly Subscription _reloads;

    /// <summary>
    /// The scripts made, in the order they were made. Main thread.
    /// </summary>
    private readonly List<Attached> _attached = [];

    /// <summary>
    /// Where the player's cameras are, as of this frame's Update. Main thread.
    /// </summary>
    private readonly List<Vector3D> _cameras = [];

    /// <summary>
    /// The members of scripts that could not be saved or put back, as <c>Class.member</c>, each warned about once.
    /// Main thread.
    /// </summary>
    private readonly HashSet<string> _unkept = new(StringComparer.Ordinal);

    public ScriptingGem(ScriptingSettings settings, IEcs ecs, Threads threads, Events events, IServiceProvider services)
    {
        _settings = settings;
        _ecs = ecs;
        _threads = threads;
        _services = services;
        _reloads = events.Watch<AssetReloaded<Script>>(reloaded => _threads.Post(EngineThread.Main, () => Restart(reloaded.Asset)));
    }

    public void Dispose()
    {
        _reloads.Dispose();

        // Scripts end on the main thread, the ECS's, which still runs what it is handed while the gems end.
        _threads.Post(EngineThread.Main, EndAll);
    }

    public ValueTask<Result<Script>> LoadAsync(string path, ReadOnlyMemory<byte> bytes, JsonElement settings, CancellationToken cancel)
    {
        return new(Load(path, settings));
    }

    public Result<IScript> Make(Script script)
    {
        return MakeOf(script.Class, entity: null);
    }

    public void Attach(Entity entity, PrefabScript script)
    {
        StartOn(entity, script);
    }

    public void Save(Entity entity, ICollection<ScriptState> into)
    {
        foreach (Attached attached in _attached)
        {
            if (attached.Entity == entity)
            {
                into.Add(new ScriptState(attached.Script.Script.Handle, StateOf(attached.Instance)));
            }
        }
    }

    public void Restore(Entity entity, Script script, ScriptState state)
    {
        if (_ecs.IsAlive(entity))
        {
            Keep(entity, script.Class, Put(entity, script, state));
        }
    }

    private static Result<Script> Load(string path, JsonElement settings)
    {
        if (settings.GetPropertyCount() != 0)
        {
            return Result.Failure<Script>($"its .meta holds settings, {settings.GetRawText()}, which a script does not take.");
        }

        string name = Path.GetFileNameWithoutExtension(path);
        List<Type> found = ClassesNamed(name);
        if (found.Count == 0)
        {
            return Result.Failure<Script>($"no code loaded has a class {name} that is an IBehavior or a system: the project's code, or the gem that compiles it, is to be built, or the gem listed.");
        }

        if (found.Count > 1)
        {
            return Result.Failure<Script>($"{found.Count} classes are named {name} ({string.Join(", ", found.ConvertAll(type => type.FullName))}); a script's class has a name of its own.");
        }

        return Result.Success(new Script { Class = found[0] });
    }

    public void Update(in Frame frame)
    {
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
    /// The classes a script named <paramref name="name"/> can be: each top-level class of that name, neither abstract
    /// nor generic, that is an <see cref="IScript"/>, in the code loaded (<see cref="Code.Types"/>): Core's, the
    /// project's as it is built now, and each gem's. Every gem is loaded before any is made, so all of them are there
    /// by the time a script loads; one that did not load, which Core reports, has no classes there.
    /// </summary>
    private static List<Type> ClassesNamed(string name)
    {
        List<Type> classes = [];
        foreach (Type type in Code.Types)
        {
            if (string.Equals(type.Name, name, StringComparison.Ordinal) && type.IsClass && !type.IsAbstract && !type.IsNested
                && !type.ContainsGenericParameters && type.IsAssignableTo(typeof(IScript)))
            {
                classes.Add(type);
            }
        }

        return classes;
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
    /// Whether a field of <paramref name="type"/> is no data of the script's own: a service its constructor was handed,
    /// or something made rather than kept, a delegate, a task or a disposable, which a copy put back makes again.
    /// </summary>
    private bool IsHanded(Type type)
    {
        return typeof(Delegate).IsAssignableFrom(type) || typeof(Task).IsAssignableFrom(type) || typeof(IDisposable).IsAssignableFrom(type)
            || (!type.IsValueType && _services.GetService(type) is not null);
    }

    /// <summary>
    /// Starts <paramref name="entity"/>'s own copy of <paramref name="script"/> and runs it, while the entity lives. One
    /// that cannot be made or throws as it starts is an error, and the entity goes without it.
    /// </summary>
    private void StartOn(Entity entity, PrefabScript script)
    {
        if (_ecs.IsAlive(entity))
        {
            Keep(entity, script.Script.Class, Start(entity, script));
        }
    }

    /// <summary>
    /// Runs <paramref name="attached"/>, when it was made; else logs why a <paramref name="type"/> is left off
    /// <paramref name="entity"/>.
    /// </summary>
    private void Keep(Entity entity, Type type, Result<Attached> attached)
    {
        if (!attached.Ok)
        {
            Debugging.Error($"Script {type.Name} is left off entity {entity.Id}: {attached.Error}");
            return;
        }

        _attached.Add(attached.Value);
    }

    /// <summary>
    /// <paramref name="entity"/>'s own copy of <paramref name="script"/>: a new one made for the entity, given each of
    /// the values the prefab holds for it, started, and added to the ECS as each kind of system it is. Why not, when it
    /// cannot be made or throws as it starts.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever a script throws as it starts, the entity goes without it, and the others are started.")]
    private Result<Attached> Start(Entity entity, PrefabScript script)
    {
        Type type = script.Script.Class;
        Result<IScript> made = MakeOf(type, entity);
        if (!made.Ok)
        {
            return Result.Failure<Attached>(made.Error);
        }

        try
        {
            foreach ((string name, object? value) in script.Values)
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

        return Schedule(entity, script, made.Value);
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
        Type type = script.Class;
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

        return Schedule(entity, new PrefabScript(script, values), made.Value);
    }

    /// <summary>
    /// Adds <paramref name="made"/> to the ECS as each kind of system it is, and hands back the whole, to run until
    /// its entity goes; why not, when adding one throws.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever adding a script's system throws, the entity goes without the script, and the others are started.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The script owns its systems, which end with it; when it is not added, they end here.")]
    private Result<Attached> Schedule(Entity entity, PrefabScript script, IScript made)
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

            return Result.Success(new Attached(entity, script, made, systems));
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
        try
        {
            values[name] = JsonSerializer.SerializeToElement(value, valueType, Json.Options);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            if (_unkept.Add($"{type.Name}.{name}"))
            {
                Debugging.Warn($"Script {type.Name}'s {name} is not saved, so a copy put back starts over without it: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="json"/>, a saved value of <paramref name="type"/>'s member <paramref name="name"/>, as
    /// <paramref name="valueType"/>; false, warned about once, when it no longer fits.
    /// </summary>
    private bool TryRead(Type type, string name, JsonElement json, Type valueType, out object? value)
    {
        try
        {
            value = json.Deserialize(valueType, Json.Options);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            if (_unkept.Add($"{type.Name}.{name}"))
            {
                Debugging.Warn($"Script {type.Name}'s saved {name} is not put back, as it no longer fits: {ex.Message}");
            }

            value = null;
            return false;
        }
    }

    /// <summary>
    /// <paramref name="script"/> was read again, as the code it is in was built: each live copy of it ends, and a new
    /// one of its class as it is now starts in its place, for the same entity with the same values, so an edit shows
    /// without the entity being spawned again. What a copy kept in its own fields starts over. One read again as the
    /// class it was, as when its file is saved before it is built, runs on. Main thread.
    /// </summary>
    private void Restart(Script script)
    {
        List<Attached> restarting = _attached.FindAll(attached => attached.Script.Script.Handle == script.Handle && attached.Script.Script.Class != script.Class);
        foreach (Attached attached in restarting)
        {
            End(attached);
            _attached.Remove(attached);
            StartOn(attached.Entity, attached.Script with { Script = script });
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

        Vector3D position = _ecs.Get<Transform>(entity).Position;
        float nearest = float.PositiveInfinity;
        foreach (Vector3D camera in _cameras)
        {
            nearest = MathF.Min(nearest, (position - camera).ToVector3().LengthSquared());
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
            Attached attached = _attached[i];
            if (!_ecs.IsAlive(attached.Entity))
            {
                End(attached);
                _attached.RemoveAt(i--);
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
                End(attached);
                _attached.RemoveAt(i--);
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
        foreach (Attached attached in _attached)
        {
            End(attached);
        }

        _attached.Clear();
    }

    /// <summary>
    /// A script made for <see cref="Entity"/>, what it was made from, and its places on the ECS, one for each kind of
    /// system it is.
    /// </summary>
    private sealed record Attached(Entity Entity, PrefabScript Script, object Instance, SubscriptionList Systems)
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
