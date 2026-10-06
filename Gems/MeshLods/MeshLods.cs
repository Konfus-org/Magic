using Magic.Contexts.Assets;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Utils;
using System.Numerics;

namespace MeshLods;

/// <summary>
/// Makes the lesser versions of a <see cref="Model"/> that has none: three meshes, each the model with every mesh
/// simplified to a share of its triangles by Forstmann's fast quadric simplification (<see cref="QuadricSimplifier"/>,
/// https://github.com/sp4cerat/Fast-Quadric-Mesh-Simplification), and last an impostor (<see cref="ImpostorBaker"/>): a
/// card per mesh that always faces the camera and shows the model as baked from the nearest of eight directions, lit and
/// shadowed through the normals baked with it and casting a shadow of its shape. Every model gets the impostor, however
/// few its triangles (a box's two are fewer than its twelve); a mesh level that does not cut the triangles enough is left
/// out. Each lesser version is written as a <c>.model</c> with the model's own parts and slot names, mesh for mesh, and the
/// impostor's atlases as PNGs beside it, named by its <see cref="Lod"/>.
/// </summary>
internal sealed class MeshLods(IFileSystem files) : IGem, ILODGenerator<Model>
{
    /// <summary>
    /// A level is kept only when it has at most this share of the triangles of the one before it.
    /// </summary>
    private const float MinReduction = 0.7f;

    /// <summary>
    /// The height on screen, as a fraction of the view's, under which the impostor is drawn: a few pixels.
    /// </summary>
    private const float ImpostorThreshold = 0.006f;

    /// <summary>
    /// How far a mesh level may move the surface, in pixels of a 1080-pixel-tall view at the height on screen it starts
    /// at: what keeps the levels true to the shape however few triangles they were asked for.
    /// </summary>
    private const float MaxErrorPixels = 1.5f;

    /// <summary>
    /// The height on screen, as a fraction of the view's, under which each mesh level is drawn, and its share of the
    /// model's triangles.
    /// </summary>
    private static readonly (float Threshold, float Share)[] Levels = [(0.10f, 0.5f), (0.04f, 0.2f), (0.015f, 0.06f)];

    public int Version => 6;

    public async Task<Result<Lods>> GenerateAsync(Model asset, string folder, IProgress<float>? progress, CancellationToken cancel)
    {
        List<Lod> lods = [];
        long previous = asset.Meshes.Sum(mesh => mesh.Indices.LongLength);
        float size = Size(asset);
        for (int level = 0; level < Levels.Length; level++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report((float)level / (Levels.Length + 1));
            (float threshold, float share) = Levels[level];
            float maxError = MaxErrorPixels * size / (threshold * 1080f);
            Mesh[] meshes = [.. asset.Meshes.Select(mesh => QuadricSimplifier.Simplify(mesh, share, maxError))];
            long indices = meshes.Sum(mesh => mesh.Indices.LongLength);
            if (indices == 0 || indices > previous * MinReduction)
                continue;

            string name = $"lod{lods.Count + 1}.model";
            Model lesser = new() { Meshes = meshes, Parts = asset.Parts, SlotNames = asset.SlotNames };
            Result written = await files.WriteBinaryAsync(files.Combine(folder, name), lesser.ToBytes(), cancel).ConfigureAwait(false);
            if (written.Failed)
                return Result<Lods>.Failure(written.Message);

            lods.Add(new Lod(threshold, Lods.IdOf(folder, name), []));
            previous = indices;
        }

        cancel.ThrowIfCancellationRequested();
        progress?.Report((float)Levels.Length / (Levels.Length + 1));
        Result<Lod> impostor = await WriteImpostorAsync(asset, folder, cancel).ConfigureAwait(false);
        if (impostor.Failed)
            return Result<Lods>.Failure(impostor.Message);

        lods.Add(impostor.Payload);
        progress?.Report(1f);

        return Result<Lods>.Success(new Lods([.. lods]));
    }

    /// <summary>
    /// The model's biggest extent: what its height on screen is measured against.
    /// </summary>
    private static float Size(Model model)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vertex vertex in model.Meshes.SelectMany(mesh => mesh.Vertices))
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        Vector3 size = max - min;
        return MathF.Max(1e-4f, MathF.Max(size.X, MathF.Max(size.Y, size.Z)));
    }

    /// <summary>
    /// The impostor: its cards as <c>impostor.model</c> and each mesh's two atlases as PNGs.
    /// </summary>
    private async Task<Result<Lod>> WriteImpostorAsync(Model asset, string folder, CancellationToken cancel)
    {
        (Mesh Card, byte[] Surface, byte[] Normal)[] baked = ImpostorBaker.Bake(asset, asset.Id);
        List<ulong> atlases = [];
        for (int mesh = 0; mesh < baked.Length; mesh++)
        {
            (string Name, byte[] Pixels)[] images = [($"impostor{mesh}surface.png", baked[mesh].Surface), ($"impostor{mesh}normal.png", baked[mesh].Normal)];
            foreach ((string name, byte[] pixels) in images)
            {
                byte[] png = ((ReadOnlySpan<byte>)pixels).Png(ImpostorBaker.AtlasSize, ImpostorBaker.AtlasSize);
                Result written = await files.WriteBinaryAsync(files.Combine(folder, name), png, cancel).ConfigureAwait(false);
                if (written.Failed)
                    return Result<Lod>.Failure(written.Message);

                atlases.Add(Lods.IdOf(folder, name));
            }
        }

        const string Cards = "impostor.model";
        Model impostor = new() { Meshes = [.. baked.Select(mesh => mesh.Card)], Parts = asset.Parts, SlotNames = asset.SlotNames };
        Result model = await files.WriteBinaryAsync(files.Combine(folder, Cards), impostor.ToBytes(), cancel).ConfigureAwait(false);
        if (model.Failed)
            return Result<Lod>.Failure(model.Message);

        return Result<Lod>.Success(new Lod(ImpostorThreshold, Lods.IdOf(folder, Cards), [.. atlases]));
    }
}
