using DryIoc;
using Magic.Attributes;
using Magic.Contexts;
using Magic.Interfaces;
using Magic.Utils;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Magic.Services;

/// <summary>
/// Per-assembly gem mechanics: recognise a gem dll, inspect it, construct and register what it declares,
/// and tear all of that down again. Ordering across gems is <see cref="GemManager"/>'s job.
/// </summary>
internal sealed class GemLoader
{
    private static readonly string HostAssemblyName = typeof(GemAttribute).Assembly.GetName().Name!;

    private readonly IContainer _container;

    public GemLoader(IContainer container)
    {
        _container = container;
    }

    /// <summary>
    /// Cheap check, without loading anything, that a file is a managed assembly referencing the host.
    /// Gems land in a flat folder next to their own dependencies (and native dlls), and none of those
    /// reference the host.
    /// </summary>
    internal static bool ReferencesHost(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader pe = new(stream);
            if (!pe.HasMetadata)
                return false; // native dll
            MetadataReader metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
                return false;
            foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
            {
                if (metadata.GetString(metadata.GetAssemblyReference(handle).Name) == HostAssemblyName)
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException)
        {
            // Not a PE file, or still being written by whoever is copying it in; a later watcher event will retry.
            return false;
        }
    }

    /// <summary>
    /// Loads the assembly into its own collectible context and works out what it declares, without
    /// constructing anything. Returns null (with the context unloaded) when it is not a usable gem.
    /// </summary>
    internal GemManifest? TryInspect(string path)
    {
        GemAssemblyContext alc = new(path);
        try
        {
            Assembly assembly = alc.LoadFromAssemblyPath(path);
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.OfType<Type>().ToArray();
            }

            Type[] gemTypes = types.Where(t => t.IsDefined(typeof(GemAttribute), inherit: false)).ToArray();
            if (gemTypes.Length == 0)
            {
                alc.Unload(); // A helper library that happens to reference the host; not a gem.
                return null;
            }
            if (gemTypes.Length > 1)
            {
                Log.Warn($"Skipping {path}: more than one class is marked [Gem] ({string.Join(", ", gemTypes.Select(t => t.FullName))}).");
                alc.Unload();
                return null;
            }

            Type gemType = gemTypes[0];
            GemAttribute meta = gemType.GetCustomAttribute<GemAttribute>()!;

            List<PartInfo> parts = [];
            foreach (Type type in types)
            {
                bool isGemClass = type == gemType;
                if (!isGemClass && !type.IsDefined(typeof(GemExportAttribute), inherit: false))
                    continue;
                PartInfo? part = BuildPart(type, assembly, meta.Name, isGemClass);
                if (part is null)
                {
                    alc.Unload();
                    return null;
                }
                parts.Add(part);
            }

            HashSet<Type> provides = parts.SelectMany(p => p.Contracts).ToHashSet();
            HashSet<Type> requires = parts.SelectMany(p => p.Requires).Where(t => !provides.Contains(t)).ToHashSet();

            // Order the gem's own parts: the gem class first, unless it needs one of its own exports.
            bool partFailed = false;
            List<PartInfo> ordered = GemOrdering.TopoSort(
                parts,
                provides: p => p.Contracts,
                requires: p => p.Requires,
                hostProvides: t => !provides.Contains(t), // anything not exported by this gem is external, no edge
                priority: p => (p.IsGemClass ? 0 : 1, p.Type.Name),
                skip: (p, why) =>
                {
                    Log.Warn($"Skipping gem {meta.Name}: cannot construct {p.Type.FullName} because {why}.");
                    partFailed = true;
                });
            if (partFailed)
            {
                alc.Unload();
                return null;
            }

            return new GemManifest
            {
                Path = path,
                Alc = alc,
                Meta = meta,
                Parts = ordered,
                Provides = provides,
                Requires = requires,
                OnReloading = FindHook<OnGemReloadingAttribute>(gemType, meta.Name, "byte[] Method()",
                    m => m.ReturnType == typeof(byte[]) && m.GetParameters().Length == 0),
                OnReloaded = FindHook<OnGemReloadedAttribute>(gemType, meta.Name, "void Method(byte[] state)",
                    m => m.ReturnType == typeof(void) && m.GetParameters() is [{ ParameterType: var t }] && t == typeof(byte[])),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Skipping {path}: could not inspect it. {ex}");
            alc.Unload();
            return null;
        }
    }

    /// <summary>
    /// Builds every part of the gem in order, registers its exports and, for a reload, restores the state
    /// captured by <see cref="CaptureState"/>. Returns null (with everything torn down) on any failure.
    /// </summary>
    internal GemContext? Construct(GemManifest manifest, byte[]? state)
    {
        // A dependency may have failed to construct earlier in this batch.
        Type? missing = manifest.Requires.FirstOrDefault(t => !_container.IsRegistered(t));
        if (missing is not null)
        {
            Log.Warn($"Skipping gem {manifest.Meta.Name}: nothing provides {missing}.");
            manifest.Alc.Unload();
            return null;
        }

        GemContext context = new(manifest);
        try
        {
            foreach (PartInfo part in manifest.Parts)
            {
                object instance = Instantiate(part);
                context.Exports.Add(instance);
                if (part.IsGemClass)
                    context.Instance = instance;

                foreach (Type contract in part.Contracts)
                {
                    _container.ProvideAs(contract, instance);
                    context.Registrations.Add(contract);
                }

                if (part.Contracts.Contains(typeof(ILogger)))
                {
                    ILogger logger = (ILogger)instance;
                    context.Loggers.Add(logger);
                    Log.Register(logger);
                }
            }

            if (state is not null && context.OnReloaded is not null)
            {
                try
                {
                    context.OnReloaded.Invoke(context.Instance, [state]);
                }
                catch (TargetInvocationException ex)
                {
                    Log.Warn($"Gem {context.Name}: restoring reload state failed. {ex.InnerException}");
                }
            }

            return context;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Log the text only: holding on to the exception would pin the gem's types.
            Log.Warn($"Failed to load gem {manifest.Meta.Name}: {(ex as TargetInvocationException)?.InnerException ?? ex}");
            Unload(context);
            return null;
        }
    }

    /// <summary>Asks the gem for the state to carry across a hot reload, if it declared a hook for it.</summary>
    internal byte[]? CaptureState(GemContext context)
    {
        if (context.OnReloading is null || context.Instance is null)
            return null;
        try
        {
            return (byte[]?)context.OnReloading.Invoke(context.Instance, null);
        }
        catch (TargetInvocationException ex)
        {
            Log.Warn($"Gem {context.Name}: capturing reload state failed. {ex.InnerException}");
            return null;
        }
    }

    /// <summary>
    /// Unregisters everything the gem provided, disposes what the loader built (gem class first, then its
    /// exports in reverse order) and unloads the assembly. Loggers are flushed before they go away.
    /// </summary>
    internal void Unload(GemContext context)
    {
        foreach (Type contract in context.Registrations)
        {
            _container.Unregister(contract, null, FactoryType.Service, null);
            // Drop the compiled resolve delegates too, or they keep the gem's types (and so its assembly) alive
            _container.ClearCache(contract, FactoryType.Service, null);
        }
        context.Registrations.Clear();

        if (context.Loggers.Count > 0)
        {
            Log.Flush();
            foreach (ILogger logger in context.Loggers)
                Log.Unregister(logger);
            context.Loggers.Clear();
        }

        // Gem class first so it can rely on its exports still being alive, then exports newest first.
        List<object> toDispose = context.Exports.AsEnumerable().Reverse().ToList();
        if (context.Instance is not null)
        {
            toDispose.Remove(context.Instance);
            toDispose.Insert(0, context.Instance);
        }
        foreach (object instance in toDispose)
        {
            if (instance is not IDisposable disposable)
                continue;
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Gem {context.Name}: disposing {instance.GetType().FullName} failed. {ex}");
            }
        }

        context.Instance = null;
        context.Exports.Clear();
        context.OnReloading = null;
        context.OnReloaded = null;
        context.Alc.Unload();
    }

    private object Instantiate(PartInfo part)
    {
        ParameterInfo[] parameters = part.Ctor.GetParameters();
        object?[] args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
            args[i] = _container.Resolve(parameters[i].ParameterType, IfUnresolved.Throw);

        object instance = part.Ctor.Invoke(args);

        foreach ((PropertyInfo property, bool required) in part.Imports)
        {
            object? value = _container.Resolve(
                property.PropertyType,
                required ? IfUnresolved.Throw : IfUnresolved.ReturnDefaultIfNotRegistered);
            if (value is not null)
                property.SetValue(instance, value);
        }

        return instance;
    }

    private static PartInfo? BuildPart(Type type, Assembly gemAssembly, string gemName, bool isGemClass)
    {
        string role = isGemClass ? "gem class" : "export";
        if (type.IsAbstract || type.IsGenericTypeDefinition)
        {
            Log.Warn($"Skipping gem {gemName}: {role} {type.FullName} must be a concrete, non-generic class.");
            return null;
        }

        ConstructorInfo[] ctors = type.GetConstructors();
        if (ctors.Length == 0)
        {
            Log.Warn($"Skipping gem {gemName}: {role} {type.FullName} has no public constructor.");
            return null;
        }
        int most = ctors.Max(c => c.GetParameters().Length);
        ConstructorInfo[] candidates = ctors.Where(c => c.GetParameters().Length == most).ToArray();
        if (candidates.Length > 1)
            Log.Warn($"Gem {gemName}: {type.FullName} has several constructors with {most} parameters; using the first one.");
        ConstructorInfo ctor = candidates[0];

        Type[] contracts = [];
        GemExportAttribute? export = type.GetCustomAttribute<GemExportAttribute>();
        if (export is not null)
        {
            contracts = export.Contracts.Length > 0
                ? export.Contracts
                : type.GetInterfaces().Where(i => i != typeof(IDisposable) && i.Assembly != gemAssembly).ToArray();

            Type? bad = contracts.FirstOrDefault(c => !c.IsAssignableFrom(type));
            if (bad is not null)
            {
                Log.Warn($"Skipping gem {gemName}: {type.FullName} does not implement its declared contract {bad}.");
                return null;
            }
            if (contracts.Length == 0)
                Log.Warn($"Gem {gemName}: {type.FullName} is marked [GemExport] but implements no host interface; nothing will be registered for it.");
        }

        List<(PropertyInfo, bool)> imports = [];
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            GemImportAttribute? import = property.GetCustomAttribute<GemImportAttribute>();
            if (import is null)
                continue;
            if (property.SetMethod is not { IsPublic: true })
            {
                Log.Warn($"Skipping gem {gemName}: [GemImport] property {type.FullName}.{property.Name} needs a public setter.");
                return null;
            }
            imports.Add((property, import.Required));
        }

        return new PartInfo(type, ctor, contracts, imports.ToArray(), isGemClass);
    }

    private static MethodInfo? FindHook<TAttribute>(Type gemType, string gemName, string expected, Func<MethodInfo, bool> isValid)
        where TAttribute : Attribute
    {
        MethodInfo[] hooks = gemType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.IsDefined(typeof(TAttribute), inherit: false))
            .ToArray();
        if (hooks.Length == 0)
            return null;
        if (hooks.Length > 1)
        {
            Log.Warn($"Gem {gemName}: more than one method is marked [{typeof(TAttribute).Name}]; ignoring all of them.");
            return null;
        }
        if (!isValid(hooks[0]))
        {
            Log.Warn($"Gem {gemName}: [{typeof(TAttribute).Name}] method {hooks[0].Name} must look like {expected}; ignoring it.");
            return null;
        }
        return hooks[0];
    }
}
