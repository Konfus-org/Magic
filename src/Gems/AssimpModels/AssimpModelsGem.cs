using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Assets.Pipeline;
using Magic.Assets.Types;
using Magic.Gems;
using Magic.Utils;
using static AssimpModels.aiPostProcessSteps;
using static AssimpModels.assimp;

namespace AssimpModels;

/// <summary>
/// Reads FBX, glTF and OBJ files through assimp into <see cref="Model"/>s: every node's meshes flattened into the
/// model's frame, triangulated, with normals and tangents, one mesh per material slot in the order the file's
/// materials are first used. It is public because nothing in its assembly makes it: Magic does, by reflection, when
/// a project lists the gem.
/// </summary>
public sealed unsafe class AssimpModelsGem : IGem, IAssetLoader<Model>
{
    /// <summary>
    /// How a file is brought into the engine's frame: left-handed with clockwise triangles, in metres, every node's
    /// transform applied, so a model is its triangles and nothing else.
    /// </summary>
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

    public ImmutableArray<string> Extensions { get; } = [".fbx", ".glb", ".gltf", ".obj"];

    public Task<Result<Model>> LoadAsync(AssetFile file, IProgress<LoadProgress>? progress, CancellationToken cancel)
    {
        Vector3 origin = file.Metadata.Get<Vector3>(nameof(Model.Origin));

        return Task.FromResult(Import(file.Path, file.Bytes.Span, origin));
    }

    private static Result<Model> Import(string path, ReadOnlySpan<byte> bytes, Vector3 origin)
    {
        int dot = path.LastIndexOf('.');
        string extension = dot < 0 ? string.Empty : path[(dot + 1)..];
        byte[] hint = System.Text.Encoding.UTF8.GetBytes(extension + "\0");
        aiScene* scene;
        fixed (byte* data = bytes)
        fixed (byte* hintPointer = hint)
        {
            scene = aiImportFileFromMemory(data, (uint)bytes.Length, Steps, hintPointer);
        }

        if (scene == null)
            return Result.Failure(Marshal.PtrToStringUTF8((nint)aiGetErrorString()) ?? "assimp gave no reason.");

        try
        {
            return ReadMeshes(scene, origin);
        }
        finally
        {
            aiReleaseImport(scene);
        }
    }

    /// <summary>
    /// Every triangle mesh of the scene as one <see cref="Mesh"/> per material slot, slots numbered as the file's
    /// materials are first used; a file with more than <see cref="Magic.Ecs.Components.MaterialSlots.Capacity"/> is
    /// refused.
    /// </summary>
    private static Result<Model> ReadMeshes(aiScene* scene, Vector3 origin)
    {
        Dictionary<uint, int> slots = [];
        List<List<Vertex>> vertices = [];
        List<List<uint>> indices = [];
        for (uint i = 0; i < scene->mNumMeshes; i++)
        {
            aiMesh* mesh = scene->mMeshes[i];
            if ((mesh->mPrimitiveTypes & (uint)aiPrimitiveType.aiPrimitiveType_TRIANGLE) == 0)
                continue;

            if (!slots.TryGetValue(mesh->mMaterialIndex, out int slot))
            {
                if (slots.Count >= Magic.Ecs.Components.MaterialSlots.Capacity)
                {
                    return Result.Failure(
                        $"it uses more than {Magic.Ecs.Components.MaterialSlots.Capacity} materials; a renderer has that many slots."
                    );
                }

                slot = slots.Count;
                slots.Add(mesh->mMaterialIndex, slot);
                vertices.Add([]);
                indices.Add([]);
            }

            AppendMesh(mesh, origin, vertices[slot], indices[slot]);
        }

        if (vertices.Count == 0)
            return Result.Failure("it holds no triangles.");

        ImmutableArray<Mesh>.Builder meshes = ImmutableArray.CreateBuilder<Mesh>(vertices.Count);
        for (int slot = 0; slot < vertices.Count; slot++)
        {
            meshes.Add(
                new Mesh
                {
                    Vertices = vertices[slot].ToArray(),
                    Indices = indices[slot].ToArray(),
                    MaterialSlot = slot,
                }
            );
        }

        return Result.Success(new Model { Meshes = meshes.MoveToImmutable(), Origin = origin });
    }

    private static void AppendMesh(aiMesh* mesh, Vector3 origin, List<Vertex> vertices, List<uint> indices)
    {
        uint first = (uint)vertices.Count;
        bool hasTangents = mesh->mTangents != null && mesh->mBitangents != null;
        bool hasUvs = mesh->mTextureCoords[0] != null;
        for (uint v = 0; v < mesh->mNumVertices; v++)
        {
            Vector3 normal = mesh->mNormals != null ? ToVector(mesh->mNormals[v]) : Vector3.UnitY;
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
            aiFace face = mesh->mFaces[f];
            if (face.mNumIndices != 3)
                continue;

            indices.Add(first + face.mIndices[0]);
            indices.Add(first + face.mIndices[1]);
            indices.Add(first + face.mIndices[2]);
        }
    }

    private static Vector3 ToVector(aiVector3D v) => new(v.x, v.y, v.z);
}
