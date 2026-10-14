using System.Diagnostics.CodeAnalysis;
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
/// (<see cref="IAssetLoader{T}"/> of <see cref="Script"/>) as the class of the file's name that a loaded gem compiled. A
/// script is made with the services its constructor asks for; an entity is given its own copy of one, its property values
/// copied over, then started on it as it is attached, which the <see cref="World"/> does once whatever spawned with the
/// entity is all there. A system goes on the
/// <see cref="IEcs"/>, through the Add that takes its kind; an <see cref="IBehavior"/> has its hooks called in this gem's.
/// Each ends with its entity.
/// <para>
/// A behaviour far from the player's cameras, those tagged <see cref="Tag.Player"/>, is updated less often: Update and
/// LateUpdate run every frame within <see cref="ScriptingSettings.FullRateDistance"/> of the nearest, and up to n times as
/// far every nth frame, up to every <see cref="ScriptingSettings.MaxInterval"/>th, with the time since the last call as
/// its frame's delta. FixedUpdate is never held back, nor is an entity without a <see cref="Transform"/>, which is nowhere.
/// </para>
/// </summary>
internal sealed class ScriptingGem(ScriptingSettings settings, IEcs ecs, Threads threads, IServiceProvider services) : IGem, IScripts, IAssetLoader<Script>, IDisposable
{
    /// <summary>
    /// Each of the <see cref="IEcs"/>'s Adds, one per kind of system: a class that implements the interface one takes
    /// is a system of that kind.
    /// </summary>
    private static readonly MethodInfo[] _adds = Array.FindAll(typeof(IEcs).GetMethods(),
        method => string.Equals(method.Name, nameof(IEcs.Add), StringComparison.Ordinal) && method.GetParameters().Length == 1);

    private readonly ScriptingSettings _settings = settings;
    private readonly IEcs _ecs = ecs;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Core's, which Core ends after every gem.")]
    private readonly Threads _threads = threads;
    private readonly IServiceProvider _services = services;

    /// <summary>
    /// Every class a script can be, by its name, found in the loaded gems the first time a script loads. Read on workers.
    /// </summary>
    private readonly Lazy<Dictionary<string, List<Type>>> _classes = new(FindClasses);

    /// <summary>
    /// The scripts made, in the order they were made. Main thread.
    /// </summary>
    private readonly List<Attached> _attached = [];

    /// <summary>
    /// Where the player's cameras are, as of this frame's Update. Main thread.
    /// </summary>
    private readonly List<Vector3D> _cameras = [];

    public void Dispose()
    {
        // Scripts end on the main thread, the ECS's, which still runs what it is handed while the gems end.
        _threads.Post(EngineThread.Main, EndAll);
    }

    public ValueTask<Result<Script>> LoadAsync(string path, ReadOnlyMemory<byte> bytes, JsonElement settings, CancellationToken cancel)
    {
        return new(Load(path, settings));
    }

    public Result<IScript> Make(Script script)
    {
        return MakeOf(script.Class);
    }

    public void Attach(Entity entity, IScript script)
    {
        StartOn(entity, script);
    }

    private Result<Script> Load(string path, JsonElement settings)
    {
        if (settings.GetPropertyCount() != 0)
        {
            return Result.Failure<Script>($"its .meta holds settings, {settings.GetRawText()}, which a script does not take.");
        }

        string name = Path.GetFileNameWithoutExtension(path);
        if (!_classes.Value.TryGetValue(name, out List<Type>? found))
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
    /// Every class a script can be, by its name: a top-level class, neither abstract nor generic, that is an
    /// <see cref="IScript"/>, in an assembly compiled against Magic, as a gem is. Every gem is loaded before any is made,
    /// so all of them are here by the time a script loads.
    /// </summary>
    private static Dictionary<string, List<Type>> FindClasses()
    {
        string? magic = typeof(IScripts).Assembly.GetName().Name;
        Dictionary<string, List<Type>> classes = new(StringComparer.Ordinal);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!Array.Exists(assembly.GetReferencedAssemblies(), referenced => string.Equals(referenced.Name, magic, StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (Type type in assembly.GetTypes())
            {
                if (!type.IsClass || type.IsAbstract || type.IsNested || type.ContainsGenericParameters || !type.IsAssignableTo(typeof(IScript)))
                {
                    continue;
                }

                if (!classes.TryGetValue(type.Name, out List<Type>? named))
                {
                    named = [];
                    classes[type.Name] = named;
                }

                named.Add(type);
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
    /// A new <paramref name="type"/>: its one public constructor given a service for each parameter, and a parameter's
    /// default when nothing provides it. Why not, when it has no such constructor, asks for its entity, which it is
    /// given in <see cref="IScript.Start"/>, asks for what nothing provides, or throws.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever a script throws as it is made, it is not made, and the others are.")]
    private Result<IScript> MakeOf(Type type)
    {
        ConstructorInfo[] constructors = type.GetConstructors();
        if (constructors.Length != 1)
        {
            return Result.Failure<IScript>($"it must have exactly one public constructor; it has {constructors.Length}.");
        }

        ParameterInfo[] parameters = constructors[0].GetParameters();
        object?[] arguments = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            Type wanted = parameters[i].ParameterType;
            if (wanted == typeof(Entity))
            {
                return Result.Failure<IScript>("its constructor asks for its Entity, which a script is given in Start, as it is made before there is one.");
            }

            if (_services.GetService(wanted) is { } service)
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
    /// Starts <paramref name="entity"/>'s own copy of <paramref name="script"/> and runs it, while the entity lives. One
    /// that cannot be made or throws as it starts is an error, and the entity goes without it.
    /// </summary>
    private void StartOn(Entity entity, IScript script)
    {
        if (!_ecs.IsAlive(entity))
        {
            return;
        }

        Result<Attached> attached = Start(entity, script);
        if (!attached.Ok)
        {
            Debugging.Error($"Script {script.GetType().Name} is left off entity {entity.Id}: {attached.Error}");
            return;
        }

        _attached.Add(attached.Value);
    }

    /// <summary>
    /// <paramref name="entity"/>'s own copy of <paramref name="script"/>: a new one made, given each of the script's
    /// public property values, started on the entity, and added to the ECS as each kind of system it is. Why not, when it
    /// cannot be made or throws as it starts.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever a script throws as it starts, the entity goes without it, and the others are started.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The script started owns its systems, which end with it; when it does not start, they end here.")]
    private Result<Attached> Start(Entity entity, IScript script)
    {
        Type type = script.GetType();
        Result<IScript> made = MakeOf(type);
        if (!made.Ok)
        {
            return Result.Failure<Attached>(made.Error);
        }

        SubscriptionList systems = new();
        try
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetMethod is { IsPublic: true } && property.SetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0)
                {
                    property.SetValue(made.Value, property.GetValue(script));
                }
            }

            made.Value.Start(entity);
            foreach (MethodInfo add in AddsOf(type))
            {
                if (add.Invoke(_ecs, [made.Value]) is Subscription system)
                {
                    systems.Add(system);
                }
            }

            return Result.Success(new Attached(entity, made.Value, systems));
        }
        catch (Exception ex)
        {
            systems.Dispose();
            return Result.Failure<Attached>($"it threw as it started: {(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex)}");
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
    /// Update and LateUpdate when <see cref="IsDue"/>, with the time since the behaviour's last call as the frame's delta.
    /// A script whose entity is gone ends first; a behaviour that throws is logged and ends, and the others go on.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever a behaviour throws, it is logged and the behaviour ends; the others go on.")]
    private void Run(Phase phase, in Frame frame)
    {
        for (int i = 0; i < _attached.Count; i++)
        {
            Attached attached = _attached[i];
            if (!_ecs.IsAlive(attached.Entity))
            {
                End(attached);
                _attached.RemoveAt(i--);
                continue;
            }

            if (attached.Instance is not IBehavior behavior)
            {
                continue;
            }

            try
            {
                if (phase == Phase.Update)
                {
                    attached.UpdateWaited += frame.DeltaSeconds;
                    if (IsDue(attached.Entity, frame.Index))
                    {
                        behavior.Update(frame with { DeltaSeconds = attached.UpdateWaited });
                        attached.UpdateWaited = 0;
                    }
                }
                else if (phase == Phase.FixedUpdate)
                {
                    behavior.FixedUpdate(frame);
                }
                else
                {
                    attached.LateUpdateWaited += frame.DeltaSeconds;
                    if (IsDue(attached.Entity, frame.Index))
                    {
                        behavior.LateUpdate(frame with { DeltaSeconds = attached.LateUpdateWaited });
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
    /// A script made for <see cref="Entity"/>, and its places on the ECS, one for each kind of system it is.
    /// </summary>
    private sealed record Attached(Entity Entity, object Instance, SubscriptionList Systems)
    {
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
