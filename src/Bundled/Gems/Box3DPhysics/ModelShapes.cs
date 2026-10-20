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
/// first fitted with until the run restarts.
/// </summary>
internal sealed class ModelShapes(Assets assets, Threads threads) : IDisposable
{
    private readonly Dictionary<Handle<Model>, ModelShape> _shapes = [];
    private readonly ModelShape _unitCube = new() { Min = new Vector3(-0.5f), Max = new Vector3(0.5f), IsReady = true };
    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        foreach (ModelShape shape in _shapes.Values)
        {
            shape.Free();
        }

        _shapes.Clear();
    }

    /// <summary>
    /// The shape of <paramref name="model"/>, or null while it loads. Main thread only.
    /// </summary>
    public ModelShape? Get(Handle<Model> model)
    {
        if (model == Handle<Model>.None)
        {
            return _unitCube;
        }

        if (!_shapes.TryGetValue(model, out ModelShape? shape))
        {
            shape = new ModelShape();
            _shapes.Add(model, shape);
            LoadAsync(model, shape).FireAndForget();
        }

        return shape.IsReady ? shape : null;
    }

    private async Task LoadAsync(Handle<Model> model, ModelShape shape)
    {
        Result<Model> loaded = await assets.LoadAsync(model, CancellationToken.None).ConfigureAwait(false);
        if (loaded.Ok)
        {
            shape.Fit(loaded.Value);
        }
        else
        {
            Debugging.Error($"A body cannot be shaped like its model, which does not load, so it is a unit cube: {loaded.Error}");
            shape.Min = _unitCube.Min;
            shape.Max = _unitCube.Max;
        }

        threads.Post(EngineThread.Main, () =>
        {
            if (_disposed)
            {
                shape.Free();
                return;
            }

            shape.IsReady = true;
        });
    }
}

/// <summary>
/// One model's bounds and, when it has triangles, its Box3D mesh data, which <see cref="Free"/> gives back.
/// </summary>
internal sealed unsafe class ModelShape
{
    public Vector3 Min { get; set; }

    public Vector3 Max { get; set; }

    /// <summary>
    /// The model's triangles, shared by every mesh shape made from it; null for a model with none.
    /// </summary>
    public B3.MeshData* Mesh { get; private set; }

    /// <summary>
    /// Whether the bounds and mesh are there to read: set on the main thread once the worker has fitted them.
    /// </summary>
    public bool IsReady { get; set; }

    /// <summary>
    /// Fits the bounds to every mesh's vertices, and makes one Box3D mesh of all their triangles, in the model's own
    /// space, unscaled: a shape scales it as its entity is scaled, with the triangles as the model winds them, which
    /// Box3D takes as their front, the side things rest on.
    /// </summary>
    public void Fit(Model model)
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
            Min = new Vector3(-0.5f);
            Max = new Vector3(0.5f);
            return;
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
                vertices[vertexAt + i] = new B3.Vec3 { X = position.X, Y = position.Y, Z = position.Z };
            }

            ReadOnlySpan<uint> meshIndices = mesh.Indices.Span;
            for (int i = 0; i < meshIndices.Length; i++)
            {
                indices[indexAt + i] = vertexAt + (int)meshIndices[i];
            }

            vertexAt += meshVertices.Length;
            indexAt += meshIndices.Length;
        }

        Min = min;
        Max = max;
        fixed (B3.Vec3* vertexData = vertices)
        fixed (int* indexData = indices)
        {
            B3.MeshDef def = default;
            def.Vertices = vertexData;
            def.Indices = indexData;
            def.VertexCount = vertexCount;
            def.TriangleCount = indexCount / 3;
            def.IdentifyEdges = true;
            Mesh = B3.CreateMesh(&def, null, 0);
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
