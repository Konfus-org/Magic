using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using Magic.Files;
using Magic.Files.Pipeline;
using Magic.Files.Types;
using Magic.Projects;
using Magic.Utils;

namespace Magic.DomainImporter;

/// <summary>
/// Reads <c>.domain</c> files into <see cref="Domain"/>s, each in a folder of its own beside its prefabs: every one in the
/// folder named <c>x_y_z</c> a chunk, and any other at the domain's origin. The file itself holds nothing, so <c>{}</c>
/// does. It is public because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class DomainImporterGem(Assets assets) : IGem, IAssetImporter<Domain>
{
    public ImmutableArray<string> Extensions => [".domain"];

    public Task<Result<Domain>> ImportAsync(AssetFile file, IProgress<LoadProgress>? progress, CancellationToken cancel)
    {
        ImmutableArray<Chunk>.Builder chunks = ImmutableArray.CreateBuilder<Chunk>();
        ImmutableArray<Handle<Prefab>>.Builder globals = ImmutableArray.CreateBuilder<Handle<Prefab>>();
        foreach (string path in assets.GetNeighbors(file))
        {
            Handle<Prefab> prefab = assets.Find<Prefab>(path);
            if (prefab == Handle<Prefab>.None)
                continue;

            // A prefab named x_y_z is that chunk; any other is at the domain's origin.
            if (
                Path.GetFileNameWithoutExtension(path).Split('_') is [string xs, string ys, string zs]
                && int.TryParse(xs, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)
                && int.TryParse(ys, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y)
                && int.TryParse(zs, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int z)
            )
                chunks.Add(new Chunk(x, y, z, prefab));
            else
                globals.Add(prefab);
        }

        Domain domain = new() { Chunks = chunks.ToImmutable(), Globals = globals.ToImmutable() };

        return Task.FromResult(Result.Success(domain));
    }
}
