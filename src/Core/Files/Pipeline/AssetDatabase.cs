using System.Collections.Immutable;

namespace Magic.Files.Pipeline;

/// <summary>
/// What assets there are and where they are. A value, replaced whole on the main thread, and read by any thread as it
/// is.
/// </summary>
internal sealed record AssetDatabase
{
    public static AssetDatabase Empty { get; } = new();

    /// <summary>
    /// The folders assets are found under, in order, the first winning a path two of them have.
    /// </summary>
    public ImmutableArray<string> Directories { get; init; } = [];

    public AssetIndex Index { get; init; } = AssetIndex.Empty;

    /// <summary>
    /// What messages call asset <paramref name="id"/>: its path, or its id when no file has it.
    /// </summary>
    public string GetName(ulong id) =>
        Index.ById.TryGetValue(id, out IndexedFile? file) ? file.Path : $"The asset {id}";
}
