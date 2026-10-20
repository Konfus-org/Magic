using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// Each model's shape as a distance field, which the scene's field for global illumination is made from: a box of
/// 32 texels a side around the model at its least detail, each texel how far its middle is from the nearest surface,
/// negative inside, exact within a few texels of a surface and no further than that beyond. One is made on a worker as
/// its model is taken in, from the triangles the pool hands it, kept in the cache (<see cref="Caches"/>, written as
/// <see cref="ICache{T}"/> of <see cref="ModelField"/>), and packed in one volume, a brick each, which the model's row
/// names (<see cref="GpuModel.Field"/>). Render thread, but for making one.
/// </summary>
internal sealed class MeshFields : IDisposable
{
    /// <summary>
    /// Texels a side of a model's field.
    /// </summary>
    public const int Resolution = 32;

    /// <summary>
    /// Raised whenever how a field is made or written changes, so the cache's older ones are made again.
    /// </summary>
    public const int CacheVersion = 1;

    /// <summary>
    /// Models whose distance fields are held at once, 64 KB each.
    /// </summary>
    private const int Capacity = 1024;

    /// <summary>
    /// Bricks along the atlas's width and height; it is as many bricks deep as its capacity needs.
    /// </summary>
    private const int Across = 8;

    /// <summary>
    /// Texels past a surface its distance is exact to, of the field's coarsest step; and the margin around the model. The
    /// shaders' MeshFieldBand (Probes/GiJobs.hlsli).
    /// </summary>
    private const float Band = 2.5f;

    private readonly IGpu _gpu;
    private readonly Assets _assets;
    private readonly Caches _caches;
    private readonly Dictionary<Handle<Model>, int> _byModel = [];
    private readonly Stack<int> _free = new();

    public MeshFields(IGpu gpu, Assets assets, Caches caches)
    {
        _gpu = gpu;
        _assets = assets;
        _caches = caches;
        int deep = Math.Max(1, (Capacity + (Across * Across) - 1) / (Across * Across));
        Atlas = gpu.CreateTexture(new GpuTextureDescription(GpuFormat.R16Float, GpuTextureUsage.Sampled, Across * Resolution, Across * Resolution, 1, (uint)(deep * Resolution), GpuTextureKind.Texture3D));
        Table = new GpuArray<GpuMeshField>(gpu, GpuBufferUsage.ComputeRead, Across * Across * deep);

        // Slot 0 stands for none, as a mesh row with no field holds.
        for (int slot = (Across * Across * deep) - 1; slot >= 1; slot--)
        {
            _free.Push(slot);
        }
    }

    public void Dispose()
    {
        _gpu.Release(Atlas);
        Table.Dispose();
    }

    /// <summary>
    /// Every field, a brick of <see cref="Resolution"/> texels a side each, in rows of <see cref="Across"/> both ways.
    /// </summary>
    public GpuTexture Atlas { get; }

    /// <summary>
    /// Each brick's box in its model's space and its corner in the atlas, by slot.
    /// </summary>
    public GpuArray<GpuMeshField> Table { get; }

    /// <summary>
    /// <paramref name="model"/>'s field, made from <paramref name="meshes"/>, from the cache, or made and kept there; none
    /// for no triangles. It is kept by the model's version, or for a model a gem made, which has no file, by a hash of the
    /// triangles it is made from. From any thread: all of it runs on a worker, never the thread that asks, which may be
    /// the render thread.
    /// </summary>
    public Task<ModelField?> ReadOrMakeAsync(Handle<Model> model, IReadOnlyList<Mesh> meshes, CancellationToken cancel)
    {
        return Task.Run(() => ReadOrMakeOnWorkerAsync(model, meshes, cancel), cancel);
    }

    private async Task<ModelField?> ReadOrMakeOnWorkerAsync(Handle<Model> model, IReadOnlyList<Mesh> meshes, CancellationToken cancel)
    {
        ulong version = _assets.Version(model);
        version = version != 0 ? version : HashOf(meshes);
        if (await _caches.ReadAsync<ModelField>(model.Id, version, cancel).ConfigureAwait(false) is { Ok: true } cached)
        {
            return cached.Value;
        }

        ModelField? made = Make(meshes);
        if (made is not null)
        {
            await _caches.WriteAsync(model.Id, version, made, cancel).ConfigureAwait(false);
        }

        return made;
    }

    /// <summary>
    /// A hash of the triangles a field is made from (<see cref="Make"/>).
    /// </summary>
    private static ulong HashOf(IReadOnlyList<Mesh> meshes)
    {
        XxHash3 hash = new();
        foreach (Mesh mesh in meshes)
        {
            hash.Append(MemoryMarshal.AsBytes(mesh.Vertices.Span));
            hash.Append(MemoryMarshal.AsBytes(mesh.Indices.Span));
        }

        ulong value = hash.GetCurrentHashAsUInt64();
        return value == 0 ? 1 : value;
    }

    /// <summary>
    /// Packs <paramref name="field"/> as <paramref name="model"/>'s, in place of the one it had: the slot its row names, 0
    /// for none when it has none or the atlas is full.
    /// </summary>
    public uint Take(Handle<Model> model, ModelField? field)
    {
        Release(model);
        if (field is null || !_free.TryPop(out int slot))
        {
            return 0;
        }

        _byModel[model] = slot;
        uint x = (uint)(slot % Across * Resolution);
        uint y = (uint)(slot / Across % Across * Resolution);
        uint z = (uint)(slot / (Across * Across) * Resolution);
        _gpu.Upload(new GpuTextureRegion(Atlas, 0, 0, x, y, z, Resolution, Resolution, Resolution), MemoryMarshal.AsBytes(field.Distances.AsSpan()));
        Table.Set(slot, new GpuMeshField { Min = field.Min, Size = field.Size, Corner = new Vector3(x, y, z) });
        return (uint)slot;
    }

    /// <summary>
    /// Lets <paramref name="model"/>'s field go: its model left the pool.
    /// </summary>
    public void Release(Handle<Model> model)
    {
        if (_byModel.Remove(model, out int slot))
        {
            Table.Set(slot, default);
            _free.Push(slot);
        }
    }

    /// <summary>
    /// A model's field from <paramref name="meshes"/>, its least detail: exact distances near each triangle, then which texels are inside, those a flood from the box's faces cannot
    /// reach past the surface; none for no triangles.
    /// </summary>
    private static ModelField? Make(IReadOnlyList<Mesh> meshes)
    {
        Vector3[] corners = Corners(meshes);
        if (corners.Length == 0)
        {
            return null;
        }

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (Vector3 corner in corners)
        {
            min = Vector3.Min(min, corner);
            max = Vector3.Max(max, corner);
        }

        // A margin the band wide all round, so the field holds the space just outside the surface, even of a flat model.
        float largest = MathF.Max(max.X - min.X, MathF.Max(max.Y - min.Y, max.Z - min.Z));
        float margin = MathF.Max(Band * largest / (Resolution - 1 - (2 * Band)), 1e-3f);
        min -= new Vector3(margin);
        max += new Vector3(margin);
        Vector3 size = max - min;
        Vector3 step = size / (Resolution - 1);
        float band = Band * MathF.Max(step.X, MathF.Max(step.Y, step.Z));

        const int Count = Resolution * Resolution * Resolution;
        float[] squared = new float[Count];
        int[] nearest = new int[Count];
        Array.Fill(squared, float.MaxValue);
        Array.Fill(nearest, -1);
        for (int triangle = 0; triangle < corners.Length / 3; triangle++)
        {
            Vector3 a = corners[triangle * 3];
            Vector3 b = corners[(triangle * 3) + 1];
            Vector3 c = corners[(triangle * 3) + 2];
            (int x0, int x1) = Span(MathF.Min(a.X, MathF.Min(b.X, c.X)) - band, MathF.Max(a.X, MathF.Max(b.X, c.X)) + band, min.X, step.X);
            (int y0, int y1) = Span(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - band, MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) + band, min.Y, step.Y);
            (int z0, int z1) = Span(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - band, MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) + band, min.Z, step.Z);
            for (int z = z0; z <= z1; z++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        Vector3 point = min + (new Vector3(x, y, z) * step);
                        float distance = Vector3.DistanceSquared(point, ClosestOnTriangle(point, a, b, c));
                        int index = x + (Resolution * (y + (Resolution * z)));
                        if (distance < squared[index])
                        {
                            squared[index] = distance;
                            nearest[index] = triangle;
                        }
                    }
                }
            }
        }

        // A texel a surface passes through is a wall the flood does not cross: every one within half its diagonal.
        float wall = 0.5f * step.Length();
        bool[] outside = Flood(squared, wall * wall);
        Half[] distances = new Half[Count];
        for (int index = 0; index < Count; index++)
        {
            float distance = MathF.Min(MathF.Sqrt(squared[index]), band);
            bool isOutside = outside[index];
            if (squared[index] < wall * wall)
            {
                int x = index % Resolution;
                int y = index / Resolution % Resolution;
                int z = index / (Resolution * Resolution);
                isOutside = IsInFront(min + (new Vector3(x, y, z) * step), corners, nearest[index]);
            }

            distances[index] = (Half)(isOutside ? distance : -distance);
        }

        return new ModelField(min, size, distances);
    }

    /// <summary>
    /// Every triangle of <paramref name="meshes"/>, three corners each.
    /// </summary>
    private static Vector3[] Corners(IReadOnlyList<Mesh> meshes)
    {
        int count = 0;
        foreach (Mesh mesh in meshes)
        {
            count += mesh.Indices.Length / 3 * 3;
        }

        Vector3[] corners = new Vector3[count];
        int at = 0;
        foreach (Mesh mesh in meshes)
        {
            ReadOnlySpan<Vertex> vertices = mesh.Vertices.Span;
            ReadOnlySpan<uint> indices = mesh.Indices.Span;
            for (int i = 0; i < indices.Length / 3 * 3; i++)
            {
                corners[at++] = vertices[(int)indices[i]].Position;
            }
        }

        return corners;
    }

    /// <summary>
    /// The texels along one axis from <paramref name="low"/> to <paramref name="high"/>, inside the field.
    /// </summary>
    private static (int First, int Last) Span(float low, float high, float origin, float step)
    {
        int first = Math.Max(0, (int)MathF.Ceiling((low - origin) / step));
        int last = Math.Min(Resolution - 1, (int)MathF.Floor((high - origin) / step));
        return (first, last);
    }

    /// <summary>
    /// Which texels are outside: those reached from the box's faces stepping only between neighbours no surface passes
    /// through (<paramref name="wall"/>, the squared distance under which one does).
    /// </summary>
    private static bool[] Flood(float[] squared, float wall)
    {
        bool[] outside = new bool[squared.Length];
        Queue<int> next = new();
        for (int index = 0; index < squared.Length; index++)
        {
            int x = index % Resolution;
            int y = index / Resolution % Resolution;
            int z = index / (Resolution * Resolution);
            bool isFace = x == 0 || y == 0 || z == 0 || x == Resolution - 1 || y == Resolution - 1 || z == Resolution - 1;
            if (isFace && squared[index] >= wall)
            {
                outside[index] = true;
                next.Enqueue(index);
            }
        }

        Span<int> neighbours = stackalloc int[6];
        while (next.TryDequeue(out int index))
        {
            int x = index % Resolution;
            int y = index / Resolution % Resolution;
            int z = index / (Resolution * Resolution);
            int found = 0;
            if (x > 0)
            {
                neighbours[found++] = index - 1;
            }

            if (x < Resolution - 1)
            {
                neighbours[found++] = index + 1;
            }

            if (y > 0)
            {
                neighbours[found++] = index - Resolution;
            }

            if (y < Resolution - 1)
            {
                neighbours[found++] = index + Resolution;
            }

            if (z > 0)
            {
                neighbours[found++] = index - (Resolution * Resolution);
            }

            if (z < Resolution - 1)
            {
                neighbours[found++] = index + (Resolution * Resolution);
            }

            foreach (int neighbour in neighbours[..found])
            {
                if (!outside[neighbour] && squared[neighbour] >= wall)
                {
                    outside[neighbour] = true;
                    next.Enqueue(neighbour);
                }
            }
        }

        return outside;
    }

    /// <summary>
    /// Whether <paramref name="point"/> is on the side of its nearest triangle its winding faces, as a texel the surface
    /// passes through is told inside or out.
    /// </summary>
    private static bool IsInFront(Vector3 point, Vector3[] corners, int triangle)
    {
        if (triangle < 0)
        {
            return true;
        }

        Vector3 a = corners[triangle * 3];
        Vector3 b = corners[(triangle * 3) + 1];
        Vector3 c = corners[(triangle * 3) + 2];
        Vector3 facing = Vector3.Cross(b - a, c - a);
        return Vector3.Dot(point - ClosestOnTriangle(point, a, b, c), facing) >= 0;
    }

    /// <summary>
    /// The point of triangle <paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/> nearest
    /// <paramref name="p"/> (Ericson 2004, "Real-Time Collision Detection", 5.1.5).
    /// </summary>
    private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 ap = p - a;
        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0)
        {
            return a;
        }

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
        {
            return b;
        }

        float vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            return a + (ab * (d1 / (d1 - d3)));
        }

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
        {
            return c;
        }

        float vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            return a + (ac * (d2 / (d2 - d6)));
        }

        float va = (d3 * d6) - (d5 * d4);
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            return b + ((c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6))));
        }

        float denominator = va + vb + vc;
        if (MathF.Abs(denominator) < 1e-20f)
        {
            return a;
        }

        float v = vb / denominator;
        float w = vc / denominator;
        return a + (ab * v) + (ac * w);
    }
}

/// <summary>
/// A model's distance field: its box in the model's space, the corner of its first texel and the size from it to the
/// last, and <see cref="MeshFields.Resolution"/> cubed distances in that space, x fastest.
/// </summary>
internal sealed record ModelField(Vector3 Min, Vector3 Size, Half[] Distances);
