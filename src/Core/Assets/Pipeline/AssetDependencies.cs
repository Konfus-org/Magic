using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Magic.Files;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// What a gem-read asset depends on, kept in the cache by its file's version.
/// </summary>
internal readonly record struct AssetDependencies(ImmutableArray<ulong> Ids);

/// <summary>
/// Writes and reads <see cref="AssetDependencies"/> as the ids' bytes.
/// </summary>
internal sealed class AssetDependencyCache : ICache<AssetDependencies>
{
    public static AssetDependencyCache Instance { get; } = new();

    public int Version => 1;

    public void Write(AssetDependencies value, IBufferWriter<byte> bytes) =>
        bytes.Write(MemoryMarshal.AsBytes(value.Ids.AsSpan()));

    public Result<AssetDependencies> Read(ReadOnlySpan<byte> bytes) =>
        bytes.Length % sizeof(ulong) == 0
            ? Result.Success(new AssetDependencies([.. MemoryMarshal.Cast<byte, ulong>(bytes)]))
            : Result.Failure($"{bytes.Length} bytes are not a list of ids.");
}
