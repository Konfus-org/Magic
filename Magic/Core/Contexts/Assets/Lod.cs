using System.IO.Hashing;
using System.Text;

namespace Magic.Contexts.Assets;

/// <summary>
/// One lesser version of an asset: the asset (by id) that stands in for it from <see cref="Threshold"/> on, and the
/// textures baked for that version alone. What a threshold measures belongs to the type: for a model the height on
/// screen, as a fraction of the view's, under which it is drawn; for a chunk the metres from a camera beyond which it is
/// spawned. <see cref="Atlases"/> are an impostor's: per mesh of it, where each texel's surface uv and coverage are,
/// then its normal; nothing names them but this. An asset's lesser versions are a <c>Lod[]</c>, the highest threshold
/// first: what an <see cref="Interfaces.ILODGenerator{T}"/> makes and <see cref="Services.Assets.Lods{T}"/> answers.
/// </summary>
public readonly record struct Lod(float Threshold, ulong Asset, ulong[] Atlases)
{
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
