using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.Loader;
using System.Text.Json;
using Assimp;
using Assimp.Configs;
using Assimp.Unmanaged;
using Magic.Contexts.Assets;
using Magic.Contexts.ECS;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;
using Mesh = Magic.Contexts.Assets.Mesh;

namespace Magic.AssimpModelLoader;

/// <summary>
/// Reads models into <see cref="Model"/>s with Assimp (FBX, glTF, OBJ, …): every node's transform is baked into its
/// meshes' vertices, which are turned into the engine's frame, metres with +Y up and the front at +Z, so a model is the
/// size it was made at. Files load on workers, several at once, each in an importer of its own.
/// </summary>
internal sealed unsafe class AssimpModelLoaderGem : IGem, IAssetLoader<Model>, IDisposable
{
    /// <summary>
    /// What Assimp does to a file as it reads it. Normals are the file's, never made: a mesh without them is refused.
    /// Tangents are made only where the file has none.
    /// </summary>
    private const PostProcessSteps Steps = PostProcessSteps.Triangulate | PostProcessSteps.JoinIdenticalVertices
        | PostProcessSteps.CalculateTangentSpace | PostProcessSteps.FlipUVs | PostProcessSteps.SortByPrimitiveType
        | PostProcessSteps.ImproveCacheLocality;

    /// <summary>
    /// The last of a <see cref="Renderer"/>'s material slots: a mesh naming one past it is drawn with it.
    /// </summary>
    private const int LastSlot = 7;

    /// <summary>
    /// Held while an import starts or ends, and while the library is freed.
    /// </summary>
    private readonly Lock _importing = new();

    /// <summary>
    /// Imports running now: the library is not freed under one.
    /// </summary>
    private int _running;

    /// <summary>
    /// The library is freed: nothing more is imported.
    /// </summary>
    private bool _freed;

    public AssimpModelLoaderGem()
    {
        // The wrapper loads the library itself, by a path: the machine's, found from the gem's dependencies as its load
        // context finds every other native library.
        string library = new AssemblyDependencyResolver(typeof(AssimpModelLoaderGem).Assembly.Location).ResolveUnmanagedDllToPath("assimp")
            ?? throw new InvalidOperationException("The gem carries no Assimp for this machine.");
        AssimpLibrary.Instance.LoadLibrary(library);
    }

    /// <summary>
    /// Frees the library once the imports running on workers are done, which nothing else waits for as the gem ends;
    /// none starts after.
    /// </summary>
    public void Dispose()
    {
        SpinWait spin = default;
        while (!TryFree())
        {
            spin.SpinOnce();
        }
    }

    public ValueTask<Result<Model>> LoadAsync(string path, ReadOnlyMemory<byte> bytes, JsonElement settings, CancellationToken cancel)
    {
        return new(Load(path, bytes.Span, settings));
    }

    private Result<Model> Load(string path, ReadOnlySpan<byte> bytes, JsonElement settings)
    {
        Result<ModelMeta> meta = Json.Read<ModelMeta>(settings);
        if (!meta.Ok)
        {
            return Result.Failure<Model>($"its .meta is not a model's: {meta.Error}");
        }

        lock (_importing)
        {
            if (_freed)
            {
                return Result.Failure<Model>("the model loader has stopped.");
            }

            _running++;
        }

        try
        {
            return Import(path, bytes, meta.Value.Origin);
        }
        finally
        {
            lock (_importing)
            {
                _running--;
            }
        }
    }

    /// <summary>
    /// Frees the library when no import is running, and lets none start after; false while one is.
    /// </summary>
    private bool TryFree()
    {
        lock (_importing)
        {
            if (_running > 0)
            {
                return false;
            }

            _freed = true;
        }

        if (AssimpLibrary.Instance.IsLibraryLoaded)
        {
            AssimpLibrary.Instance.FreeLibrary();
        }

        return true;
    }

    private static Result<Model> Import(string path, ReadOnlySpan<byte> file, Vector3 origin)
    {
        string format = Path.GetExtension(path).TrimStart('.');
        using AssimpContext importer = new();
        importer.SetConfig(new FBXPreservePivotsConfig(false));

        Scene? scene;
        try
        {
            fixed (byte* bytes = file)
            {
                using UnmanagedMemoryStream stream = new(bytes, file.Length);
                scene = importer.ImportFileFromStream(stream, Steps, format);
            }
        }
        catch (AssimpException ex)
        {
            return Result.Failure<Model>($"Assimp could not import it: {ex.Message}");
        }

        if (scene?.RootNode is null || scene.SceneFlags.HasFlag(SceneFlags.Incomplete))
        {
            return Result.Failure<Model>("Assimp could not import it.");
        }

        foreach (Assimp.Mesh mesh in scene.Meshes)
        {
            if (mesh.PrimitiveType.HasFlag(PrimitiveType.Triangle) && !mesh.HasNormals)
            {
                return Result.Failure<Model>($"its mesh {mesh.Name} has no normals, which are imported, never made: export it with them.");
            }
        }

        List<Mesh> meshes = [];
        Walk(scene, scene.RootNode, Matrix4x4.Identity, ToEngine(scene, format), origin, meshes);
        return Result.Success(new Model { Meshes = meshes });
    }

    /// <summary>
    /// Bakes the triangle meshes of <paramref name="node"/> and those under it into <paramref name="meshes"/>, each
    /// with its node's transform under <paramref name="parent"/>.
    /// </summary>
    private static void Walk(Scene scene, Node node, Matrix4x4 parent, Matrix4x4 toEngine, Vector3 origin, List<Mesh> meshes)
    {
        // Assimp's matrices have the translation in the last column; transposed, they are System.Numerics' row vectors.
        Matrix4x4 world = Matrix4x4.Transpose(node.Transform) * parent;
        foreach (int index in node.MeshIndices)
        {
            Assimp.Mesh source = scene.Meshes[index];
            if (source.PrimitiveType.HasFlag(PrimitiveType.Triangle) && source.VertexCount > 0)
            {
                meshes.Add(Bake(source, world * toEngine, origin));
            }
        }

        foreach (Node child in node.Children)
        {
            Walk(scene, child, world, toEngine, origin, meshes);
        }
    }

    /// <summary>
    /// <paramref name="source"/> in the engine's frame, moved by <paramref name="transform"/> and then by
    /// <paramref name="origin"/> to (0, 0, 0).
    /// </summary>
    private static Mesh Bake(Assimp.Mesh source, Matrix4x4 transform, Vector3 origin)
    {
        // Directions along the surface move with the transform; normals with its inverse transpose.
        Matrix4x4 normals = Matrix4x4.Invert(transform, out Matrix4x4 inverse) ? Matrix4x4.Transpose(inverse) : transform;
        List<Vector3>? uvs = source.HasTextureCoords(0) ? source.TextureCoordinateChannels[0] : null;
        Vertex[] vertices = new Vertex[source.VertexCount];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(source.Normals[i], normals));
            Vector4 tangent = new(1, 0, 0, 1);
            if (source.HasTangentBasis)
            {
                Vector3 along = Vector3.Normalize(Vector3.TransformNormal(source.Tangents[i], transform));
                Vector3 bitangent = Vector3.TransformNormal(source.BiTangents[i], transform);
                tangent = new Vector4(along, Vector3.Dot(Vector3.Cross(normal, along), bitangent) < 0 ? -1 : 1);
            }

            vertices[i] = new Vertex
            {
                Position = Vector3.Transform(source.Vertices[i], transform) - origin,
                Normal = normal,
                Tangent = tangent,
                Uv = uvs is null ? Vector2.Zero : new Vector2(uvs[i].X, uvs[i].Y),
            };
        }

        // A transform that mirrors turns each triangle's winding over; swapping two corners turns it back.
        bool mirrored = transform.GetDeterminant() < 0;
        uint[] indices = new uint[source.FaceCount * 3];
        for (int face = 0; face < source.FaceCount; face++)
        {
            List<int> corners = source.Faces[face].Indices;
            indices[face * 3] = (uint)corners[0];
            indices[(face * 3) + 1] = (uint)corners[mirrored ? 2 : 1];
            indices[(face * 3) + 2] = (uint)corners[mirrored ? 1 : 2];
        }

        return new Mesh { Vertices = vertices, Indices = indices, MaterialSlot = Math.Min(source.MaterialIndex, LastSlot) };
    }

    /// <summary>
    /// What Assimp hands over, to the engine's frame. glTF and OBJ come right-handed, in metres, +Y up, the front at +Z.
    /// An FBX comes in its own unit and axes, untouched, and names them: its unit in centimetres (UnitScaleFactor) and
    /// which of its axes, and which way, are right, up and front. Those are turned into metres and that same frame, so
    /// a model exported from Blender is its size there whatever the export's scale and axes. Then an X mirror takes the
    /// right-handed frame to the engine's left-handed one, the front staying at +Z.
    /// </summary>
    private static Matrix4x4 ToEngine(Scene scene, string format)
    {
        Matrix4x4 frame = Matrix4x4.Identity;
        float scale = 1;
        if (format.Equals("fbx", StringComparison.OrdinalIgnoreCase))
        {
            frame = default;
            frame.M44 = 1;
            Turn(ref frame, scene, "CoordAxis", 0);
            Turn(ref frame, scene, "UpAxis", 1);
            Turn(ref frame, scene, "FrontAxis", 2);
            scale = (float)(Number(scene, "UnitScaleFactor", 1) / 100);
        }

        return frame * Matrix4x4.CreateScale(-scale, scale, scale);
    }

    /// <summary>
    /// Sends the FBX axis the file names under <paramref name="key"/>, by its sign, to the frame's axis
    /// <paramref name="to"/>: what the file calls right to X, up to Y, front to Z. A file that names none is that axis
    /// already.
    /// </summary>
    private static void Turn(ref Matrix4x4 frame, Scene scene, string key, int to)
    {
        int from = (int)Number(scene, key, to);
        frame[from is >= 0 and <= 2 ? from : to, to] = Number(scene, key + "Sign", 1) < 0 ? -1 : 1;
    }

    private static double Number(Scene scene, string key, double fallback)
    {
        return scene.Metadata.TryGetValue(key, out Metadata.Entry entry) ? Convert.ToDouble(entry.Data, CultureInfo.InvariantCulture) : fallback;
    }
}
