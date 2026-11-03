using System.Collections.Immutable;
using Magic.Files;
using Magic.Pipelines;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Reads the file and works out its version: first in loading a <typeparamref name="T"/>. A file the type's loader takes
/// no such extension of is refused before it is read.
/// </summary>
internal sealed class ReadFilePipeline<T>(IFileSystem files) : IPipeline<AssetLoad<T>>
    where T : IAsset<T>
{
    public async ValueTask<AssetLoad<T>> RunAsync(AssetLoad<T> state, CancellationToken cancel)
    {
        if (state.Loader is { } loader && !HasExtension(state.Path, loader.Extensions))
            return state.ToNotAsset($"a {typeof(T).Name}'s file ends in {string.Join(", ", loader.Extensions)}.");

        Result<byte[]> bytes = await files.ReadBytesAsync(state.File, cancel).ConfigureAwait(false);
        if (!bytes.Ok)
            return state.ToFailed($"{state.Path} could not be read: {bytes.Error}");

        return state with
        {
            Bytes = bytes.Value,
            Version = AssetIndex.ComputeVersion(bytes.Value, state.Settings),
        };
    }

    /// <summary>
    /// Whether <paramref name="path"/> ends in one of <paramref name="extensions"/>.
    /// </summary>
    private static bool HasExtension(string path, ImmutableArray<string> extensions)
    {
        foreach (string extension in extensions)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
