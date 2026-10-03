using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Utils;
using System.Numerics;

namespace MeshLods;

/// <summary>
/// Makes the lesser versions of a <see cref="Model"/> that has none: the same model with every mesh simplified by
/// vertex clustering, once for each of <see cref="Levels"/>. A level lays a grid over a mesh's box, as many cells
/// along its longest side as the level says, and merges the vertices of each cell into one (their average), dropping
/// the triangles that collapse; so the error is one cell, a few pixels at the height on screen the level is drawn
/// under. It is quick and takes any mesh, closed or not; hard edges smooth out and uv seams blur, which is why the
/// first level only starts at a quarter of the view's height. Each lesser version is written as a <c>.model</c> with
/// the model's own parts and slot names, mesh for mesh.
/// </summary>
internal sealed class MeshLods(IFileSystem files) : IGem, ILODGenerator<Model>
{
    /// <summary>
    /// A mesh with fewer triangles than this is not worth simplifying: its lesser versions are itself.
    /// </summary>
    private const int MinTriangles = 64;

    /// <summary>
    /// A level is kept only when it has at most this share of the triangles of the one before it.
    /// </summary>
    private const float MinReduction = 0.7f;

    /// <summary>
    /// The height on screen, as a fraction of the view's, under which each level is drawn, and its grid's cells along the longest side.
    /// </summary>
    private static readonly (float Threshold, int Cells)[] Levels = [(0.25f, 64), (0.10f, 32), (0.04f, 16)];

    public int Version => 1;

    public async Task<Result<Dictionary<float, string>>> GenerateAsync(Model asset, string folder, IProgress<float>? progress, CancellationToken cancel)
    {
        Dictionary<float, string> lods = [];
        long previous = asset.Meshes.Sum(mesh => mesh.Indices.LongLength);
        for (int level = 0; level < Levels.Length; level++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report((float)level / Levels.Length);
            (float threshold, int cells) = Levels[level];
            Mesh[] meshes = [.. asset.Meshes.Select(mesh => mesh.Indices.Length / 3 < MinTriangles ? mesh : Simplify(mesh, cells))];
            long indices = meshes.Sum(mesh => mesh.Indices.LongLength);
            if (indices == 0 || indices > previous * MinReduction)
                continue;

            string name = $"lod{lods.Count + 1}.model";
            Model lesser = new() { Meshes = meshes, Parts = asset.Parts, SlotNames = asset.SlotNames };
            Result written = await files.WriteBinaryAsync(files.Combine(folder, name), lesser.ToBytes(), cancel).ConfigureAwait(false);
            if (written.Failed)
                return Result<Dictionary<float, string>>.Failure(written.Message);

            lods[threshold] = name;
            previous = indices;
        }

        progress?.Report(1f);

        return Result<Dictionary<float, string>>.Success(lods);
    }

    /// <summary>
    /// The mesh with the vertices of every grid cell merged into one: position, normal, tangent and uv averaged, the
    /// bitangent sign the first one's. A triangle with two corners in one cell is dropped. A mesh that would lose
    /// every triangle is returned as it is.
    /// </summary>
    private static Mesh Simplify(Mesh mesh, int cells)
    {
        Vector3 size = mesh.Box.Max - mesh.Box.Min;
        float cell = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / cells;
        if (cell <= 0f)
            return mesh;

        Dictionary<(int X, int Y, int Z), int> clusterOf = [];
        List<Vertex> sums = [];
        List<int> counts = [];
        int[] remap = new int[mesh.Vertices.Length];
        for (int i = 0; i < remap.Length; i++)
        {
            Vertex vertex = mesh.Vertices[i];
            Vector3 at = (vertex.Position - mesh.Box.Min) / cell;
            (int, int, int) key = ((int)at.X, (int)at.Y, (int)at.Z);
            if (!clusterOf.TryGetValue(key, out int cluster))
            {
                clusterOf[key] = cluster = sums.Count;
                sums.Add(default);
                counts.Add(0);
            }

            Vertex sum = sums[cluster];
            sum.Position += vertex.Position;
            sum.Normal += vertex.Normal;
            sum.Tangent = new Vector4(sum.Tangent.X + vertex.Tangent.X, sum.Tangent.Y + vertex.Tangent.Y, sum.Tangent.Z + vertex.Tangent.Z, counts[cluster] == 0 ? vertex.Tangent.W : sum.Tangent.W);
            sum.Uv += vertex.Uv;
            sums[cluster] = sum;
            counts[cluster]++;
            remap[i] = cluster;
        }

        List<uint> indices = new(mesh.Indices.Length);
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            int a = remap[mesh.Indices[i]], b = remap[mesh.Indices[i + 1]], c = remap[mesh.Indices[i + 2]];
            if (a == b || b == c || a == c)
                continue;

            indices.Add((uint)a);
            indices.Add((uint)b);
            indices.Add((uint)c);
        }

        if (indices.Count == 0)
            return mesh;

        Vertex[] vertices = new Vertex[sums.Count];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vertex sum = sums[i];
            Vector3 tangent = new(sum.Tangent.X, sum.Tangent.Y, sum.Tangent.Z);
            vertices[i] = new Vertex
            {
                Position = sum.Position / counts[i],
                Normal = NormalizeOr(sum.Normal, Vector3.UnitY),
                Tangent = new Vector4(NormalizeOr(tangent, Vector3.UnitX), sum.Tangent.W),
                Uv = sum.Uv / counts[i],
            };
        }

        Mesh simplified = new() { Vertices = vertices, Indices = [.. indices] };
        simplified.ComputeBounds();

        return simplified;
    }

    /// <summary>
    /// Normals that cancel out when averaged leave nothing to normalise.
    /// </summary>
    private static Vector3 NormalizeOr(Vector3 vector, Vector3 fallback)
    {
        return vector.LengthSquared() > 1e-12f ? Vector3.Normalize(vector) : fallback;
    }
}
