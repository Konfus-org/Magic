using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;
using static Magic.AssimpModelLoader.aiMetadataType;
using static Magic.AssimpModelLoader.aiPostProcessSteps;
using static Magic.AssimpModelLoader.aiPrimitiveType;
using static Magic.AssimpModelLoader.Assimp;

namespace Magic.AssimpModelLoader;

/// <summary>
/// Reads models into <see cref="Model"/>s with Assimp (FBX, glTF, OBJ, …): every node's transform is baked into its
/// meshes' vertices, which are turned into the engine's frame, metres with +Y up and the front at +Z, so a model is the
/// size it was made at. Files load on workers, several at once, each an import of its own.
/// </summary>
internal sealed unsafe class AssimpModelLoaderGem : IGem, IAssetLoader<Model>
{
    /// <summary>
    /// What Assimp does to a file as it reads it. Normals are the file's, never made: a mesh without them is refused.
    /// Tangents are made only where the file has none.
    /// </summary>
    private const aiPostProcessSteps Steps = aiProcess_Triangulate
        | aiProcess_JoinIdenticalVertices
        | aiProcess_CalcTangentSpace
        | aiProcess_FlipUVs
        | aiProcess_SortByPType
        | aiProcess_ImproveCacheLocality;

    public ValueTask<Result<Model>> LoadAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        JsonElement settings,
        CancellationToken cancel) => new(Load(path, bytes.Span, settings));

    private static Result<Model> Import(string path, ReadOnlySpan<byte> file, Vector3 origin)
    {
        // The file's extension tells Assimp its format; FBX pivots are baked into the nodes, not kept as nodes of
        // their own.
        string format = Path.GetExtension(path).TrimStart('.');
        aiScene* scene;
        aiPropertyStore* properties = aiCreatePropertyStore();
        try
        {
            aiSetImportPropertyInteger(properties, CString.Constant(AI_CONFIG_IMPORT_FBX_PRESERVE_PIVOTS), 0);
            fixed (byte* bytes = file)
            fixed (byte* hint = CString.ToBytes(format))
            {
                scene = aiImportFileFromMemoryWithProperties(
                    bytes,
                    (uint)file.Length,
                    (uint)Steps,
                    hint,
                    properties);
            }
        }
        finally
        {
            aiReleasePropertyStore(properties);
        }

        if (scene is null)
            return Result.Failure<Model>($"Assimp could not import it: {CString.Read(aiGetErrorString())}");

        try
        {
            if (scene->mRootNode is null || (scene->mFlags & AI_SCENE_FLAGS_INCOMPLETE) != 0)
                return Result.Failure<Model>("Assimp could not import it.");

            for (uint i = 0; i < scene->mNumMeshes; i++)
            {
                aiMesh* mesh = scene->mMeshes[i];

                if (IsTriangles(mesh) && mesh->mNormals is null)
                {
                    return Result.Failure<Model>($"its mesh {CString.Read((byte*)&mesh->mName.data)} has no normals, "
                        + "which are imported, never made: export it with them.");
                }
            }

            List<Mesh> meshes = [];
            BakeMeshes(scene, scene->mRootNode, Matrix4x4.Identity, ToEngine(scene, format), origin, meshes);

            return Result.Success(new Model { Meshes = meshes });
        }
        finally
        {
            aiReleaseImport(scene);
        }
    }

    private static bool IsTriangles(aiMesh* mesh)
        => (mesh->mPrimitiveTypes & (uint)aiPrimitiveType_TRIANGLE) != 0;

    /// <summary>
    /// Bakes the triangle meshes of <paramref name="node"/> and those under it into <paramref name="meshes"/>, each
    /// with its node's transform under <paramref name="parent"/>.
    /// </summary>
    private static void BakeMeshes(
        aiScene* scene,
        aiNode* node,
        Matrix4x4 parent,
        Matrix4x4 toEngine,
        Vector3 origin,
        List<Mesh> meshes)
    {
        // Assimp's matrices have the translation in the last column; transposed, they are System.Numerics' row vectors.
        Matrix4x4 world = Matrix4x4.Transpose(Unsafe.BitCast<aiMatrix4x4, Matrix4x4>(node->mTransformation))
            * parent;
        for (uint i = 0; i < node->mNumMeshes; i++)
        {
            aiMesh* source = scene->mMeshes[node->mMeshes[i]];
            if (IsTriangles(source) && source->mNumVertices > 0) meshes.Add(Bake(source, world * toEngine, origin));
        }

        for (uint i = 0; i < node->mNumChildren; i++)
        {
            BakeMeshes(scene, node->mChildren[i], world, toEngine, origin, meshes);
        }
    }

    /// <summary>
    /// <paramref name="source"/> in the engine's frame, moved by <paramref name="transform"/> and then by
    /// <paramref name="origin"/> to (0, 0, 0).
    /// </summary>
    private static Mesh Bake(aiMesh* source, Matrix4x4 transform, Vector3 origin)
    {
        // Directions along the surface move with the transform; normals with its inverse transpose.
        Matrix4x4 normals = Matrix4x4.Invert(transform, out Matrix4x4 inverse)
            ? Matrix4x4.Transpose(inverse)
            : transform;
        // The texture coordinate channels are an array of pointers, read as one: an inline array of pointers has no
        // indexer.
        aiVector3D* uvs = *(aiVector3D**)&source->mTextureCoords;
        bool hasTangents = source->mTangents is not null && source->mBitangents is not null;
        // Each vertex in the engine's frame, less the origin. A tangent's w is the side of it the bitangent is on, -1
        // where the texture is mirrored on the model; a mesh without tangents has +X.
        Vertex[] vertices = new Vertex[source->mNumVertices];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(ToVector3(source->mNormals[i]), normals));
            Vector4 tangent = new(1, 0, 0, 1);
            if (hasTangents)
            {
                Vector3 along = Vector3.Normalize(Vector3.TransformNormal(ToVector3(source->mTangents[i]), transform));
                Vector3 bitangent = Vector3.TransformNormal(ToVector3(source->mBitangents[i]), transform);
                tangent = new Vector4(along, Vector3.Dot(Vector3.Cross(normal, along), bitangent) < 0 ? -1 : 1);
            }

            vertices[i] = new Vertex
            {
                Position = Vector3.Transform(ToVector3(source->mVertices[i]), transform) - origin,
                Normal = normal,
                Tangent = tangent,
                Uv = uvs is null ? Vector2.Zero : new Vector2(uvs[i].x, uvs[i].y),
            };
        }

        // A transform that mirrors turns each triangle's winding over; swapping two corners turns it back.
        bool mirrored = transform.GetDeterminant() < 0;
        uint[] indices = new uint[source->mNumFaces * 3];
        for (int face = 0; face < source->mNumFaces; face++)
        {
            uint* corners = source->mFaces[face].mIndices;
            indices[face * 3] = corners[0];
            indices[(face * 3) + 1] = corners[mirrored ? 2 : 1];
            indices[(face * 3) + 2] = corners[mirrored ? 1 : 2];
        }

        // A mesh naming a slot past the renderer's last is drawn with the last.
        return new Mesh
        {
            Vertices = vertices,
            Indices = indices,
            MaterialSlot = Math.Min((int)source->mMaterialIndex, MaterialSlots.Capacity - 1)
        };
    }

    private static Vector3 ToVector3(aiVector3D vector) => Unsafe.BitCast<aiVector3D, Vector3>(vector);

    /// <summary>
    /// What Assimp hands over, to the engine's frame. glTF and OBJ come right-handed, in metres, +Y up, the front at
    /// +Z. An FBX comes in its own unit and axes, untouched, and names them: its unit in centimetres (UnitScaleFactor)
    /// and which of its axes, and which way, are right, up and front. Those are turned into metres and that same frame,
    /// so a model exported from Blender is its size there whatever the export's scale and axes. Then an X mirror takes
    /// the right-handed frame to the engine's left-handed one, the front staying at +Z.
    /// </summary>
    private static Matrix4x4 ToEngine(aiScene* scene, string format)
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
            scale = (float)(GetNumber(scene, "UnitScaleFactor", 1) / 100);
        }

        return frame * Matrix4x4.CreateScale(-scale, scale, scale);
    }

    /// <summary>
    /// Sends the FBX axis the file names under <paramref name="key"/>, by its sign, to the frame's axis
    /// <paramref name="to"/>: what the file calls right to X, up to Y, front to Z. A file that names none is that axis
    /// already.
    /// </summary>
    private static void Turn(ref Matrix4x4 frame, aiScene* scene, string key, int to)
    {
        int from = (int)GetNumber(scene, key, to);
        frame[from is >= 0 and <= 2 ? from : to, to] = GetNumber(scene, key + "Sign", 1) < 0 ? -1 : 1;
    }

    /// <summary>
    /// The number the scene's metadata holds under <paramref name="key"/>, whatever its width, or
    /// <paramref name="fallback"/> where there is none or it is not a number.
    /// </summary>
    private static double GetNumber(aiScene* scene, string key, double fallback)
    {
        aiMetadata* metadata = scene->mMetaData;

        if (metadata is null) return fallback;

        for (uint i = 0; i < metadata->mNumProperties; i++)
        {
            if (!string.Equals(CString.Read((byte*)&metadata->mKeys[i].data), key, StringComparison.Ordinal)) continue;

            aiMetadataType type = metadata->mValues[i].mType;
            void* data = metadata->mValues[i].mData;

            if (type == AI_BOOL) return *(byte*)data != 0 ? 1 : 0;

            if (type == AI_INT32) return *(int*)data;

            if (type == AI_UINT32) return *(uint*)data;

            if (type == AI_INT64) return *(long*)data;

            if (type == AI_UINT64) return *(ulong*)data;

            if (type == AI_FLOAT) return *(float*)data;

            return type == AI_DOUBLE ? *(double*)data : fallback;
        }

        return fallback;
    }

    private Result<Model> Load(string path, ReadOnlySpan<byte> bytes, JsonElement settings)
    {
        Result<ModelMeta> meta = Json.Read<ModelMeta>(settings);

        return meta.Ok
            ? Import(path, bytes, meta.Value.Origin)
            : Result.Failure<Model>($"its .meta is not a model's: {meta.Error}");
    }
}
