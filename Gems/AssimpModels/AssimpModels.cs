using Assimp;
using Assimp.Configs;
using Assimp.Unmanaged;
using Magic.Attributes;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Services;
using System.Numerics;
using Mesh = Magic.Contexts.Assets.Mesh;

namespace AssimpGem;

/// <summary>
/// Loads <see cref="Model"/> assets with Assimp (FBX, glTF, OBJ, ...), baking every node transform into the
/// vertices and converting to engine space: left handed, +X right, +Y up, +Z forward, 1 unit = 1 metre = 1
/// Blender unit, triangles wound clockwise seen from outside. Assimp hands every format over right handed,
/// +Y up, with the model's front at +Z (glTF's convention), so the conversion is one X mirror plus a winding
/// flip for every format, and a unit fix for FBX only.
/// </summary>
[Gem(name: "Assimp Models", version: "1.0.0", description: "Loads models with Assimp.", author: "Konfus")]
[GemExport(typeof(IAssetLoader<Model>))]
internal sealed class AssimpModels : IAssetLoader<Model>
{
    private const PostProcessSteps Steps =
        PostProcessSteps.Triangulate | PostProcessSteps.JoinIdenticalVertices | PostProcessSteps.GenerateSmoothNormals |
        PostProcessSteps.CalculateTangentSpace | PostProcessSteps.FlipUVs | PostProcessSteps.SortByPrimitiveType |
        PostProcessSteps.ImproveCacheLocality;

    /// <summary>
    /// Assimp's right-handed frame to the engine's: an X mirror, and for FBX a constant 0.01. FBX files
    /// are in centimetres and Assimp's FBX importer folds the file's own unit scale and axis correction into
    /// the root node, so its output is always centimetres whatever Blender's export scale was, and the
    /// file's UnitScaleFactor must never be applied on top. Every other format Assimp reads (glTF, OBJ, ...)
    /// is already in metres.
    /// </summary>
    private static Matrix4x4 ToEngine(string format)
    {
        float scale = format.Equals("fbx", StringComparison.OrdinalIgnoreCase) ? 0.01f : 1f;
        return Matrix4x4.CreateScale(-scale, scale, scale);
    }

    public AssimpModels(Project project)
    {
        // AssimpNet finds assimp.dll itself, from AppContext.BaseDirectory (bin\, not bin\Gems\) and the NuGet
        // cache; gem assemblies are loaded from a stream, so there is no Assembly.Location to probe from.
        AssimpLibrary.Instance.Resolver.SetProbingPaths(project.Gems);
        AssimpLibrary.Instance.LoadLibrary();
    }

    public void Load(Model asset, byte[] bytes)
    {
        Load(asset, bytes, null, default);
    }

    public Task LoadAsync(Model asset, byte[] bytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() => Load(asset, bytes, progress, cancellationToken), cancellationToken);
    }

    private static void Load(Model asset, byte[] bytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using AssimpContext context = new(); // not shared: an importer instance serves one import at a time
        context.SetConfig(new FBXPreservePivotsConfig(false));
        string hint = Path.GetExtension(asset.Path).TrimStart('.');
        Scene scene = context.ImportFileFromStream(new MemoryStream(bytes), Steps, hint);
        if (scene is null || scene.SceneFlags.HasFlag(SceneFlags.Incomplete) || scene.RootNode is null)
            throw new InvalidOperationException("Assimp could not import the file.");
        progress?.Report(0.5);

        List<Mesh> meshes = [];
        List<ModelPart> parts = [];
        Walk(scene, scene.RootNode, Matrix4x4.Identity, ToEngine(hint), meshes, parts, cancellationToken);

        asset.Meshes = [.. meshes];
        asset.Parts = [.. parts];
        asset.SlotNames = [.. scene.Materials.Select(m => m.Name ?? "")];
        progress?.Report(1);
    }

    private static void Walk(Scene scene, Node node, Matrix4x4 parentWorld, Matrix4x4 toEngine, List<Mesh> meshes, List<ModelPart> parts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Assimp matrices are row-major with the translation in the last column (M14, M24, M34 once copied
        // into System.Numerics); the transpose puts them in the row-vector convention this code multiplies in.
        Matrix4x4 world = Matrix4x4.Transpose(node.Transform) * parentWorld;

        foreach (int meshIndex in node.MeshIndices)
        {
            Assimp.Mesh source = scene.Meshes[meshIndex];
            if (!source.PrimitiveType.HasFlag(PrimitiveType.Triangle) || source.VertexCount == 0) // Triangulate left only triangles (plus an n-gon flag)
                continue;
            parts.Add(new ModelPart(meshes.Count, source.MaterialIndex));
            meshes.Add(Bake(source, world * toEngine));
        }

        foreach (Node child in node.Children)
            Walk(scene, child, world, toEngine, meshes, parts, cancellationToken);
    }

    private static Mesh Bake(Assimp.Mesh source, Matrix4x4 transform)
    {
        Matrix4x4.Invert(transform, out Matrix4x4 inverse);
        Matrix4x4 normalTransform = Matrix4x4.Transpose(inverse);
        bool hasUv = source.HasTextureCoords(0);
        bool hasTangents = source.HasTangentBasis;

        Vertex[] vertices = new Vertex[source.VertexCount];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(source.Normals[i], normalTransform));
            Vector4 tangent = new(1, 0, 0, 1);
            if (hasTangents)
            {
                Vector3 t = Vector3.Normalize(Vector3.TransformNormal(source.Tangents[i], normalTransform));
                Vector3 b = Vector3.TransformNormal(source.BiTangents[i], normalTransform);
                tangent = new Vector4(t, Vector3.Dot(Vector3.Cross(normal, t), b) < 0 ? -1 : 1);
            }
            vertices[i] = new Vertex
            {
                Position = Vector3.Transform(source.Vertices[i], transform),
                Normal = normal,
                Tangent = tangent,
                Uv = hasUv ? new Vector2(source.TextureCoordinateChannels[0][i].X, source.TextureCoordinateChannels[0][i].Y) : Vector2.Zero
            };
        }

        // The mirror in the transform flips the winding; reversing each triangle puts it back to clockwise from outside.
        uint[] indices = new uint[source.FaceCount * 3];
        for (int f = 0; f < source.FaceCount; f++)
        {
            List<int> face = source.Faces[f].Indices;
            indices[f * 3] = (uint)face[0];
            indices[(f * 3) + 1] = (uint)face[2];
            indices[(f * 3) + 2] = (uint)face[1];
        }

        return new Mesh { Vertices = vertices, Indices = indices };
    }
}
