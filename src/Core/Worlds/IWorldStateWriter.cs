using Magic.Assets;
using Magic.Utils;

namespace Magic.Worlds;

/// <summary>
/// Writes a saved world down as a file, for what keeps it past the run. Provided by the gem that reads saved worlds.
/// </summary>
public interface IWorldStateWriter
{
    /// <summary>
    /// Writes <paramref name="state"/> and its <c>.meta</c> at <paramref name="path"/>: the handle it can be loaded by, the
    /// same each time for the same path, once a file under an asset directory has been taken in; or why not. A component
    /// the file can't hold is an error, logged once per type, and left out.
    /// </summary>
    Task<Result<Handle<WorldState>>> WriteAsync(
        WorldState state,
        string path,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    );
}
