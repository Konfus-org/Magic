using Magic.Contexts;
using Magic.Contexts.Assets;
using System.Text.Json.Serialization;

namespace Magic.Services;

/// <summary>
/// The project: what its <c>.magic</c> file says (JSON in the <see cref="Contexts.Assets.AssetJson"/> dialect,
/// every value optional) plus where things are on disk. In the file <see cref="Assets"/> and <see cref="Cache"/>
/// are folder names relative to the root; whoever builds the project (the host from the command line, a test from
/// its temp folder) makes them absolute, so gems never resolve paths themselves (a stream-loaded gem has no
/// assembly location). Gems and Resources belong to the engine, not the file.
/// </summary>
public sealed class Project
{
    public string Name { get; set; } = "Magic";

    public Handle<Texture> Icon { get; set; } = Handle<Texture>.None;

    /// <summary>Where Assets, Cache, logs and screenshots live.</summary>
    [JsonIgnore]
    public string Root { get; set; } = "";

    /// <summary>The location of gems AKA plugins.</summary>
    [JsonIgnore]
    public string Gems { get; set; } = "";

    /// <summary>Built in resources.</summary>
    [JsonIgnore]
    public string Resources { get; set; } = "";

    /// <summary>The location of logs.</summary>
    [JsonIgnore]
    public string Logs => Path.Combine(Root, "Logs");

    public string Assets => Path.Combine(Root, "Assets");

    /// <summary>Import, shader and other caches.</summary>
    public string Cache => Path.Combine(Root, "Cache");
}
