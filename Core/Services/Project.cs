using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Settings;
using System.Text.Json.Serialization;

namespace Magic.Services;

/// <summary>
/// The project: what its <c>.magic</c> file says (JSON in the <see cref="AssetJson"/> dialect, every value optional)
/// plus where things are on disk. Immutable: whoever builds it (the host from the command line, a test from its temp
/// folder) fills in the paths with <c>with</c>, so gems never resolve paths themselves (a stream-loaded gem has no
/// assembly location). <see cref="EngineGems"/> and <see cref="Resources"/> belong to the engine, not the file.
/// </summary>
public sealed record Project
{
    public string Name { get; init; } = "Magic";

    public Handle<Texture> Icon { get; init; } = Handle<Texture>.None;

    /// <summary>Per-subsystem settings; <c>--set Section.Key=value</c> on the command line overrides them.</summary>
    public Settings Settings { get; init; } = new();

    /// <summary>The domain opened at start-up (<c>"domain": { "id": N }</c>); <c>--domain</c> overrides it. None starts in an empty world.</summary>
    public Handle<Domain> Domain { get; init; } = Handle<Domain>.None;

    /// <summary>
    /// The engine gems to load, by assembly name (the name <c>GemDependsOn</c> uses); <c>"default"</c> stands for
    /// every gem in <see cref="EngineGems"/>. Left out of the file it is <c>["default"]</c>; <c>[]</c> loads none.
    /// Gems found under <see cref="Root"/> are never listed: they always load.
    /// </summary>
    public string[] Gems { get; init; } = ["default"];

    /// <summary>Where Assets and Cache live.</summary>
    [JsonIgnore]
    public string Root { get; init; } = "";

    /// <summary>The engine's own gem folder, where <see cref="Gems"/> are looked for (and native dlls sit).</summary>
    [JsonIgnore]
    public string EngineGems { get; init; } = "";

    /// <summary>Built in resources.</summary>
    [JsonIgnore]
    public string Resources { get; init; } = "";

    /// <summary>
    /// Where log files go: <c>Logs</c> next to the running executable (the build's <c>bin</c>, or the install folder), so
    /// a project folder never collects them. Only a Release build writes log files; a crash report (<c>.crash</c>) is
    /// written here by any build.
    /// </summary>
    [JsonIgnore]
    public static string Logs => Path.Combine(AppContext.BaseDirectory, "Logs");

    /// <summary>Where <c>--screenshots</c> PNGs go: <c>Screenshots</c> next to <see cref="Logs"/>, for the same reason.</summary>
    [JsonIgnore]
    public static string Screenshots => Path.Combine(AppContext.BaseDirectory, "Screenshots");

    [JsonIgnore]
    public string Assets => Path.Combine(Root, "Assets");

    /// <summary>Import, shader and other caches.</summary>
    [JsonIgnore]
    public string Cache => Path.Combine(Root, "Cache");
}
