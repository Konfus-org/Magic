using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Numerics;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Ecs.Components;
using Magic.Utils;
using static Magic.AssimpModelImporter.aiPostProcessSteps;
using static Magic.AssimpModelImporter.assimp;

namespace Magic.AssimpModelImporter;

/// <summary>
/// Reads FBX, glTF and OBJ files through assimp into <see cref="Model"/>s, with one mesh per material slot.
/// </summary>
/// <remarks>
/// It flattens every node's meshes into the model's frame, triangulates them, and adds normals and tangents. The slots
/// are in the order that the file first uses its materials.
/// </remarks>
public sealed unsafe class Importer : IAssetImporter<Model>
{
    /// <summary>
    /// The steps that bring a file into the engine's frame: left-handed, clockwise triangles, metres, node transforms
    /// applied.
    /// </summary>
    /// <remarks>
    /// Thus a model is its triangles and nothing else. The import relies on these steps: triangulating and sorting by
    /// type leave only triangles in a triangle mesh, and generating normals gives every mesh normals.
    /// </remarks>
    private const uint Steps = (uint)(
        aiProcess_Triangulate
        | aiProcess_GenSmoothNormals
        | aiProcess_CalcTangentSpace
        | aiProcess_JoinIdenticalVertices
        | aiProcess_ImproveCacheLocality
        | aiProcess_SortByPType
        | aiProcess_PreTransformVertices
        | aiProcess_GlobalScale
        | aiProcess_MakeLeftHanded
        | aiProcess_FlipWindingOrder
        | aiProcess_FlipUVs
    );

    internal Importer() { }

    public ImmutableArray<string> Extensions { get; } = [".fbx", ".glb", ".gltf", ".obj"];

    public Task<Result<Model>> ImportAsync(
        AssetFile file,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    ) => Task.FromResult(Import(file));

    /// <summary>
    /// Imports every triangle mesh of the file as one <see cref="Mesh"/> per material slot, in the order that the file
    /// first uses its materials.
    /// </summary>
    /// <remarks>
    /// A file with more than <see cref="MaterialSlots.Capacity"/> materials is refused.
    /// </remarks>
    private static Result<Model> Import(AssetFile file)
    {
        // Read the scene
        Vector3 origin = file.Metadata.Get<Vector3>(nameof(Model.Origin));
        ReadOnlySpan<byte> bytes = file.Bytes.Span;
        aiScene* scene;
        fixed (byte* data = bytes)
        fixed (byte* hintPointer = CString.ToBytes(Path.GetExtension(file.Path)[1..]))
        {
            scene = aiImportFileFromMemory(data, (uint)bytes.Length, Steps, hintPointer);
        }

        if (scene == null)
            return Result.Failure(CString.Read(aiGetErrorString()));

        try
        {
            // Gather each material's triangles into its slot
            List<(uint Material, List<Vertex> Vertices, List<uint> Indices)> slots = [];
            for (uint i = 0; i < scene->mNumMeshes; i++)
            {
                aiMesh* mesh = scene->mMeshes[i];
                if ((mesh->mPrimitiveTypes & (uint)aiPrimitiveType.aiPrimitiveType_TRIANGLE) == 0)
                    continue;

                uint material = mesh->mMaterialIndex;
                int slot = slots.FindIndex(held => held.Material == material);
                if (slot < 0)
                {
                    slot = slots.Count;
                    slots.Add((material, [], []));
                }

                AppendMesh(mesh, origin, slots[slot].Vertices, slots[slot].Indices);
            }

            if (slots.Count > MaterialSlots.Capacity)
                return Result.Failure(
                    $"it uses more than {MaterialSlots.Capacity} materials; a renderer has that many slots."
                );

            if (slots.Count == 0)
                return Result.Failure("it holds no triangles.");

            // One mesh per slot
            ImmutableArray<Mesh> meshes =
            [
                .. slots.Select(
                    (held, slot) =>
                        new Mesh
                        {
                            Vertices = held.Vertices.ToArray(),
                            Indices = held.Indices.ToArray(),
                            MaterialSlot = slot,
                        }
                ),
            ];

            return Result.Success(new Model { Meshes = meshes, Origin = origin });
        }
        finally
        {
            aiReleaseImport(scene);
        }
    }

    private static void AppendMesh(aiMesh* mesh, Vector3 origin, List<Vertex> vertices, List<uint> indices)
    {
        uint first = (uint)vertices.Count;
        bool hasTangents = mesh->mTangents != null && mesh->mBitangents != null;
        bool hasUvs = mesh->mTextureCoords[0] != null;
        for (uint v = 0; v < mesh->mNumVertices; v++)
        {
            Vector3 normal = ToVector(mesh->mNormals[v]);
            Vector4 tangent = new(Vector3.UnitX, 1);
            if (hasTangents)
            {
                Vector3 t = ToVector(mesh->mTangents[v]);
                Vector3 b = ToVector(mesh->mBitangents[v]);
                tangent = new Vector4(t, Vector3.Dot(Vector3.Cross(normal, t), b) < 0 ? -1 : 1);
            }

            aiVector3D uv = hasUvs ? mesh->mTextureCoords[0][v] : default;
            vertices.Add(
                new Vertex
                {
                    Position = ToVector(mesh->mVertices[v]) - origin,
                    Normal = normal,
                    Tangent = tangent,
                    Uv = new Vector2(uv.x, uv.y),
                }
            );
        }

        for (uint f = 0; f < mesh->mNumFaces; f++)
        {
            uint* face = mesh->mFaces[f].mIndices;
            indices.Add(first + face[0]);
            indices.Add(first + face[1]);
            indices.Add(first + face[2]);
        }
    }

    private static Vector3 ToVector(aiVector3D v) => new(v.x, v.y, v.z);
}
