using Assimp;
using Assimp.Configs;
using Assimp.Unmanaged;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
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
internal sealed class AssimpModels : IGem, IAssetLoader<Model>
{
    private const PostProcessSteps Steps =
        PostProcessSteps.Triangulate | PostProcessSteps.JoinIdenticalVertices | PostProcessSteps.GenerateSmoothNormals |
        PostProcessSteps.CalculateTangentSpace | PostProcessSteps.FlipUVs | PostProcessSteps.SortByPrimitiveType |
        PostProcessSteps.ImproveCacheLocality;

    public AssimpModels(Project project)
    {
        // AssimpNet finds assimp.dll itself, from AppContext.BaseDirectory (bin\, not bin\Gems\) and the NuGet
        // cache; gem assemblies are loaded from a stream, so there is no Assembly.Location to probe from.
        AssimpLibrary.Instance.Resolver.SetProbingPaths(project.EngineGems);
        AssimpLibrary.Instance.LoadLibrary();
    }

    public Result Load(Model asset, byte[] bytes)
    {
        using AssimpContext context = new(); // not shared: an importer instance serves one import at a time
        context.SetConfig(new FBXPreservePivotsConfig(false));

        string hint = Path.GetExtension(asset.Path).TrimStart('.');
        Scene scene;
        try
        {
            scene = context.ImportFileFromStream(new MemoryStream(bytes), Steps, hint);
        }
        catch (AssimpException ex)
        {
            return Result.Failure($"Assimp could not import the file: {ex.Message}");
        }

        if (scene is null || scene.SceneFlags.HasFlag(SceneFlags.Incomplete) || scene.RootNode is null)
            return Result.Failure("Assimp could not import the file.");

        List<Mesh> meshes = [];
        List<ModelPart> parts = [];
        Walk(scene, scene.RootNode, Matrix4x4.Identity, ToEngine(hint), meshes, parts);

        asset.Meshes = [.. meshes];
        asset.Parts = [.. parts];
        asset.SlotNames = [.. scene.Materials.Select(material => material.Name ?? "")];

        return Result.Success();
    }

    private static void Walk(
        Scene scene, Node node, Matrix4x4 parentWorld, Matrix4x4 toEngine,
        List<Mesh> meshes, List<ModelPart> parts)
    {

        // Assimp matrices are row-major with the translation in the last column (M14, M24, M34 once copied
        // into System.Numerics); the transpose puts them in the row-vector convention this code multiplies in.
        Matrix4x4 world = Matrix4x4.Transpose(node.Transform) * parentWorld;

        foreach (int meshIndex in node.MeshIndices)
        {
            Assimp.Mesh source = scene.Meshes[meshIndex];

            // Triangulate left only triangles (plus an n-gon flag).
            if (!source.PrimitiveType.HasFlag(PrimitiveType.Triangle) || source.VertexCount == 0)
                continue;

            parts.Add(new ModelPart(meshes.Count, source.MaterialIndex));
            meshes.Add(Bake(source, world * toEngine));
        }

        foreach (Node child in node.Children)
            Walk(scene, child, world, toEngine, meshes, parts);
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
        for (int faceIndex = 0; faceIndex < source.FaceCount; faceIndex++)
        {
            List<int> face = source.Faces[faceIndex].Indices;
            indices[faceIndex * 3] = (uint)face[0];
            indices[(faceIndex * 3) + 1] = (uint)face[2];
            indices[(faceIndex * 3) + 2] = (uint)face[1];
        }

        Mesh mesh = new() { Vertices = vertices, Indices = indices };
        mesh.ComputeBounds();

        return mesh;
    }

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
}
