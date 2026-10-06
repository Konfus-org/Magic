using System.IO.Hashing;
using System.Text;

namespace Magic.Contexts.Assets;

/// <summary>
/// The lesser versions of an asset, the highest threshold first: what an <see cref="Interfaces.ILODGenerator{T}"/>
/// makes and <see cref="Services.Assets.Lods{T}"/> answers.
/// </summary>
public sealed record Lods(Lod[] Levels)
{
    public static readonly Lods None = new([]);

    /// <summary>
    /// How an impostor's atlases are imported: linear (they hold uvs and normals, not colours) and without mips (a mip
    /// would average them with the empty texels around the silhouette). The asset index gives them this in place of a
    /// sidecar.
    /// </summary>
    public const string AtlasImport = """{ "usage": "mask", "mipmaps": false, "channels": "rgb" }""";

    /// <summary>
    /// The id of a file a generator writes in its folder of the cache. Generated files have no sidecar: the asset
    /// index gives each this id, so whatever names one (a stand-in chunk its models, a level its atlases) knows it
    /// before it is written.
    /// </summary>
    public static ulong IdOf(string folder, string file)
    {
        return Math.Max(1, XxHash64.HashToUInt64(Encoding.UTF8.GetBytes($"{Path.GetFileName(folder)}/{file}")));
    }
}
