using Magic.Contexts;
using Magic.Utils;

namespace Magic.Services;

/// <summary>
/// The project: what its <c>.magic</c> file says (every value optional; <see cref="AssetJson"/> reads it) plus where things are on disk. The host
/// reads its name and gems; every other key is a section a gem reads for itself (<see cref="Read{T}"/>). Immutable: whoever builds it (the host from the command line, a test from its temp
/// folder) fills in the paths with <c>with</c>, so gems never resolve paths themselves (a stream-loaded gem has no
/// assembly location). <see cref="EngineGems"/> belongs to the engine, not the file.
/// </summary>
public sealed record Project
{
    public string Name { get; init; } = "Magic";

    /// <summary>
    /// The engine gems to load, by assembly name; <c>"default"</c> stands for
    /// every gem in <see cref="EngineGems"/>. Left out of the file it is <c>["default"]</c>; <c>[]</c> loads none.
    /// Gems found under <see cref="Root"/> are never listed: they always load.
    /// </summary>
    public string[] Gems { get; init; } = ["default"];

    /// <summary>
    /// Everything else the <c>.magic</c> file says, by key: what a gem reads for itself (<see cref="Read{T}"/>), such
    /// as <c>"entryPoint"</c> and <c>"loading"</c>.
    /// </summary>
    public Dictionary<string, Unread> Sections { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where Assets live.
    /// </summary>
    public string Root { get; init; } = "";

    /// <summary>
    /// The engine's own gem folder, where <see cref="Gems"/> are looked for (and native dlls sit).
    /// </summary>
    public string EngineGems { get; init; } = "";

    /// <summary>
    /// Where log files go: <c>Logs</c> next to the running executable (the build's <c>bin</c>, or the install folder), so
    /// a project folder never collects them. Only a Release build writes log files; a crash report (<c>.crash</c>) is
    /// written here by any build. Static, unlike the folders beside it: <see cref="Utils.Log"/> writes a crash here before
    /// any project exists.
    /// </summary>
    public static string Logs => Path.Combine(AppContext.BaseDirectory, "Logs");

    /// <summary>
    /// Where <c>--screenshots</c> PNGs go: <c>Screenshots</c> next to <see cref="Logs"/>, for the same reason.
    /// </summary>
    public string Screenshots { get; init; } = Path.Combine(AppContext.BaseDirectory, "Screenshots");

    /// <summary>
    /// Shader, LOD and other caches: <c>Cache</c> next to <see cref="Logs"/>, for the same reason, unless whoever builds the project says otherwise.
    /// </summary>
    public string Cache { get; init; } = Path.Combine(AppContext.BaseDirectory, "Cache");

    public string Assets => Path.Combine(Root, "Assets");

    /// <summary>
    /// The section <paramref name="key"/> as a <typeparamref name="T"/> (a handle is <c>{ "id": N }</c>); default when the
    /// file has none, and warned and default when it is not one.
    /// </summary>
    public T? Read<T>(string key)
    {
        if (!Sections.TryGetValue(key, out Unread section))
            return default;

        Result<T> read = AssetJson.Read<T>(section);
        if (read.Failed)
            Log.Warn($"The project's \"{key}\" is not a {typeof(T).Name}: {read.Message}");

        return read.Payload;
    }
}
