using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Assets.Types;
using Magic.Gems;
using Magic.Utils;

namespace DomainLoader;

/// <summary>
/// Reads <c>.domain</c> files into <see cref="Domain"/>s, each in a folder of its own beside its prefabs: every one in the
/// folder named <c>x_y_z</c> a chunk, and any other at the domain's origin. The file itself holds nothing, so <c>{}</c>
/// does. It is public because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class DomainLoaderGem(AssetDatabase assets) : IGem, IAssetLoader<Domain>
{
    public ImmutableArray<string> Extensions => [".domain"];

    public Task<Result<Domain>> LoadAsync(AssetFile file, IProgress<LoadProgress>? progress, CancellationToken cancel)
    {
        string work = $"Reading {file.Path}";
        progress?.Report(new LoadProgress(work, 0, 1));
        int slash = file.Path.LastIndexOf('/');
        string folder = slash < 0 ? string.Empty : file.Path[..slash];
        ImmutableArray<Chunk>.Builder chunks = ImmutableArray.CreateBuilder<Chunk>();
        ImmutableArray<Handle<Prefab>>.Builder globals = ImmutableArray.CreateBuilder<Handle<Prefab>>();
        foreach (string path in assets.GetPaths(folder, file.Id))
        {
            Handle<Prefab> prefab = assets.Find<Prefab>(path);
            if (prefab == Handle<Prefab>.None)
                continue;

            if (ParseChunk(path, prefab) is { } chunk)
                chunks.Add(chunk);
            else
                globals.Add(prefab);
        }

        Domain domain = new() { Chunks = chunks.ToImmutable(), Globals = globals.ToImmutable() };
        progress?.Report(new LoadProgress(work, 1, 1));

        return Task.FromResult(Result.Success(domain));
    }

    /// <summary>
    /// The chunk <paramref name="prefab"/> is, by a file named <c>x_y_z</c>; none for any other name.
    /// </summary>
    private static Chunk? ParseChunk(string path, Handle<Prefab> prefab)
    {
        string[] parts = Path.GetFileNameWithoutExtension(path).Split('_');

        return
            parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y)
            && int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int z)
            ? new Chunk(x, y, z, prefab)
            : null;
    }
}
