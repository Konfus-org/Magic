using Magic.Interfaces;
using System.Reflection;

namespace Magic.Contexts;

/// <summary>
/// A loaded gem: its metadata (copied out of the attribute, so nothing from the gem assembly is kept once it
/// is unloaded), the instances the loader built and what it registered on their behalf.
/// </summary>
internal sealed class GemContext
{
    public GemContext(GemManifest manifest)
    {
        Path = manifest.Path;
        Alc = manifest.Alc;
        Name = manifest.Meta.Name;
        Version = manifest.Meta.Version;
        Description = manifest.Meta.Description;
        Author = manifest.Meta.Author;
        Provides = manifest.Provides;
        Requires = manifest.Requires;
        OnReloading = manifest.OnReloading;
        OnReloaded = manifest.OnReloaded;
    }

    public string Path { get; set; }
    public GemAssemblyContext Alc { get; }
    public string Name { get; }
    public string Version { get; }
    public string Description { get; }
    public string? Author { get; }
    public IReadOnlySet<Type> Provides { get; }
    public IReadOnlySet<Type> Requires { get; }
    public MethodInfo? OnReloading { get; set; }
    public MethodInfo? OnReloaded { get; set; }

    /// <summary>The <see cref="Attributes.GemAttribute"/> class instance.</summary>
    public object? Instance { get; set; }

    /// <summary>Every instance the loader built for this gem, in construction order (includes <see cref="Instance"/>).</summary>
    public List<object> Exports { get; } = [];

    /// <summary>Contract types registered into the container for this gem.</summary>
    public List<Type> Registrations { get; } = [];

    /// <summary>Exports that were also registered with <see cref="Utils.Log"/>.</summary>
    public List<ILogger> Loggers { get; } = [];

    public bool IsLoggerGem => Provides.Contains(typeof(ILogger));
}
