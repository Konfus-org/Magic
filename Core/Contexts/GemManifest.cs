using Magic.Attributes;
using System.Reflection;

namespace Magic.Contexts;

/// <summary>
/// One class the loader constructs for a gem: the <see cref="GemAttribute"/> class itself or a
/// <see cref="GemExportAttribute"/> class. Nothing here is an instance; it is all reflection metadata.
/// </summary>
internal sealed record PartInfo(
    Type Type,
    ConstructorInfo Ctor,
    Type[] Contracts,
    (PropertyInfo Property, bool Required)[] Imports,
    bool IsGemClass)
{
    public IEnumerable<Type> Requires =>
        Ctor.GetParameters().Select(p => p.ParameterType)
            .Concat(Imports.Where(i => i.Required).Select(i => i.Property.PropertyType));
}

/// <summary>
/// What the loader learned about a gem assembly before constructing anything in it. Lives only between
/// inspection and construction; a manifest for a gem that is skipped must be dropped right after its
/// <see cref="Alc"/> is unloaded, or the assembly stays alive.
/// </summary>
internal sealed class GemManifest
{
    public required string Path { get; init; }
    public required GemAssemblyContext Alc { get; init; }
    public required GemAttribute Meta { get; init; }

    /// <summary>Parts in construction order (gem class first unless it needs one of its own exports).</summary>
    public required IReadOnlyList<PartInfo> Parts { get; init; }

    /// <summary>Every contract type this gem registers.</summary>
    public required IReadOnlySet<Type> Provides { get; init; }

    /// <summary>Every type this gem needs from outside itself (host services or other gems' exports).</summary>
    public required IReadOnlySet<Type> Requires { get; init; }

    public MethodInfo? OnReloading { get; init; }
    public MethodInfo? OnReloaded { get; init; }
}
