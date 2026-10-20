using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Services;

namespace Magic.Box3DPhysics;

/// <summary>
/// What a model collides as, made once per model and shared by every body shaped like it: its bounds, which a box, a
/// sphere or a capsule is fitted to, and its triangles as Box3D mesh data, for a mesh. A model loads on a worker as
/// any asset does and is fitted there, so a body waits for it; one that does not load, or an entity with no model,
/// takes a unit cube's bounds, so a body always has a shape. A model made again while running keeps the shape it was
/// first fitted with until the run restarts. Main thread, where the gem's ending is handed too, so a shape is only ever
/// read, put in and let go there.
/// </summary>
internal sealed class ModelShapes(Assets assets, Threads threads) : IDisposable
{
    /// <summary>
    /// Each model's shape once a worker has fitted it; null while its model loads.
    /// </summary>
    private readonly Dictionary<Handle<Model>, ModelShape?> _shapes = [];
    private readonly ModelShape _unitCube = new(new Vector3(-0.5f), new Vector3(0.5f));
    private bool _disposed;

    public void Dispose()
    {
        threads.Post(EngineThread.Main, () =>
        {
            _disposed = true;
            foreach (ModelShape? shape in _shapes.Values)
            {
                shape?.Free();
            }

            _shapes.Clear();
        });
    }

    /// <summary>
    /// The farthest any shape here reaches from its model's origin at scale 1 (<see cref="ModelShape.Reach"/>), and
    /// at least 1, which a shape still loading is taken to reach: at an entity's scale, a bound on how far its shape
    /// could reach, whatever its model, that tells from the transform alone whether it could be near.
    /// </summary>
    public float Longest { get; private set; } = 1;

    /// <summary>
    /// The shape of <paramref name="model"/>, or null while it loads.
    /// </summary>
    public ModelShape? Get(Handle<Model> model)
    {
        if (model == Handle<Model>.None)
        {
            return _unitCube;
        }

        if (!_shapes.TryGetValue(model, out ModelShape? shape))
        {
            _shapes.Add(model, null);
            LoadAsync(model).FireAndForget();
        }

        return shape;
    }

    /// <summary>
    /// Loads and fits the model on a worker, then puts the shape in on the main thread, or frees it when the shapes
    /// were let go meanwhile.
    /// </summary>
    private async Task LoadAsync(Handle<Model> model)
    {
        Result<Model> loaded = await assets.LoadAsync(model, CancellationToken.None).ConfigureAwait(false);
        ModelShape shape;
        if (loaded.Ok)
        {
            shape = ModelShape.Of(loaded.Value) ?? _unitCube;
        }
        else
        {
            Debugging.Error($"A body cannot be shaped like its model, which does not load, so it is a unit cube: {loaded.Error}");
            shape = _unitCube;
        }

        threads.Post(EngineThread.Main, () =>
        {
            if (_disposed)
            {
                shape.Free();
                return;
            }

            _shapes[model] = shape;
            Longest = Math.Max(Longest, shape.Reach);
        });
    }
}

/// <summary>
/// One model's bounds and, when it has triangles, its Box3D mesh data, which <see cref="Free"/> gives back. Made whole,
/// so whoever has one reads it.
/// </summary>
internal sealed unsafe class ModelShape(Vector3 min, Vector3 max)
{
    public Vector3 Min { get; } = min;

    public Vector3 Max { get; } = max;

    /// <summary>
    /// How far the shape reaches from the model's origin at scale 1, at most: half the bounds' diagonal, plus how far
    /// off the origin their centre is. At a scale it reaches at most this times the scale's length, as each axis is
    /// stretched by no more than that.
    /// </summary>
    public float Reach { get; } = ((max - min).Length() + (max + min).Length()) * 0.5f;

    /// <summary>
    /// The model's triangles, shared by every mesh shape made from it; null for a model with none, and once freed.
    /// </summary>
    public B3.MeshData* Mesh { get; private set; }

    /// <summary>
    /// The shape fitted to <paramref name="model"/>: its bounds to every mesh's vertices, and one Box3D mesh of all their
    /// triangles, in the model's own space, unscaled: a shape scales it as its entity is scaled, with the triangles as
    /// the model winds them, which Box3D takes as their front, the side things rest on. Null for a model with no
    /// triangles, which takes the unit cube's.
    /// </summary>
    public static ModelShape? Of(Model model)
    {
        int vertexCount = 0;
        int indexCount = 0;
        foreach (Mesh mesh in model.Meshes)
        {
            vertexCount += mesh.Vertices.Length;
            indexCount += mesh.Indices.Length;
        }

        if (vertexCount == 0 || indexCount < 3)
        {
            return null;
        }

        B3.Vec3[] vertices = new B3.Vec3[vertexCount];
        int[] indices = new int[indexCount];
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        int vertexAt = 0;
        int indexAt = 0;
        foreach (Mesh mesh in model.Meshes)
        {
            ReadOnlySpan<Vertex> meshVertices = mesh.Vertices.Span;
            for (int i = 0; i < meshVertices.Length; i++)
            {
                Vector3 position = meshVertices[i].Position;
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
                vertices[vertexAt + i] = Box3DPhysicsGem.ToVec(position);
            }

            ReadOnlySpan<uint> meshIndices = mesh.Indices.Span;
            for (int i = 0; i < meshIndices.Length; i++)
            {
                indices[indexAt + i] = vertexAt + (int)meshIndices[i];
            }

            vertexAt += meshVertices.Length;
            indexAt += meshIndices.Length;
        }

        fixed (B3.Vec3* vertexData = vertices)
        fixed (int* indexData = indices)
        {
            B3.MeshDef def = default;
            def.Vertices = vertexData;
            def.Indices = indexData;
            def.VertexCount = vertexCount;
            def.TriangleCount = indexCount / 3;
            def.IdentifyEdges = true;
            return new ModelShape(min, max) { Mesh = B3.CreateMesh(&def, null, 0) };
        }
    }

    public void Free()
    {
        if (Mesh is not null)
        {
            B3.DestroyMesh(Mesh);
            Mesh = null;
        }
    }
}
