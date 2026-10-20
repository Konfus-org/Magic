using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.AssimpModelLoader;

/// <summary>
/// Reads models into <see cref="Model"/>s with Assimp (FBX, glTF, OBJ, …): every node's transform is baked into its
/// meshes' vertices, which are turned into the engine's frame, metres with +Y up and the front at +Z, so a model is the
/// size it was made at. Files load on workers, several at once, each an import of its own.
/// </summary>
internal sealed unsafe class AssimpModelLoaderGem : IGem, IAssetLoader<Model>, IDisposable
{
    /// <summary>
    /// What Assimp does to a file as it reads it. Normals are the file's, never made: a mesh without them is refused.
    /// Tangents are made only where the file has none.
    /// </summary>
    private const Assimp.PostProcessSteps Steps = Assimp.PostProcessSteps.Triangulate | Assimp.PostProcessSteps.JoinIdenticalVertices
        | Assimp.PostProcessSteps.CalcTangentSpace | Assimp.PostProcessSteps.FlipUVs | Assimp.PostProcessSteps.SortByPType
        | Assimp.PostProcessSteps.ImproveCacheLocality;

    /// <summary>
    /// Held while an import starts or ends, and while the loader stops.
    /// </summary>
    private readonly Lock _importing = new();

    /// <summary>
    /// Imports running now: the loader does not stop under one.
    /// </summary>
    private int _running;

    /// <summary>
    /// The loader has stopped: nothing more is imported.
    /// </summary>
    private bool _stopped;

    /// <summary>
    /// Stops once the imports running on workers are done, which nothing else waits for as the gem ends; none starts
    /// after, so no import is left calling into a gem that has gone.
    /// </summary>
    public void Dispose()
    {
        SpinWait spin = default;
        while (!TryStop())
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
        if (!meta.Ok) return Result.Failure<Model>($"its .meta is not a model's: {meta.Error}");

        lock (_importing)
        {
            if (_stopped) return Result.Failure<Model>("the model loader has stopped.");

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
    /// Stops the loader when no import is running, and lets none start after; false while one is.
    /// </summary>
    private bool TryStop()
    {
        lock (_importing)
        {
            if (_running > 0) return false;

            _stopped = true;
        }

        return true;
    }

    private static Result<Model> Import(string path, ReadOnlySpan<byte> file, Vector3 origin)
    {
        // The file's extension tells Assimp its format; FBX pivots are baked into the nodes, not kept as nodes of
        // their own.
        string format = Path.GetExtension(path).TrimStart('.');
        Assimp.Scene* scene;
        Assimp.PropertyStore* properties = Assimp.CreatePropertyStore();
        try
        {
            Assimp.SetImportPropertyInteger(properties, Assimp.ConfigImportFbxPreservePivots, 0);
            fixed (byte* bytes = file)
            fixed (byte* hint = CString.ToBytes(format))
            {
                scene = Assimp.ImportFileFromMemoryWithProperties(bytes, (uint)file.Length, (uint)Steps, hint, properties);
            }
        }
        finally
        {
            Assimp.ReleasePropertyStore(properties);
        }

        if (scene is null)
        {
            return Result.Failure<Model>($"Assimp could not import it: {CString.Read(Assimp.GetErrorString())}");
        }

        try
        {
            if (scene->MRootNode is null || (scene->MFlags & Assimp.SceneFlagsIncomplete) != 0)
            {
                return Result.Failure<Model>("Assimp could not import it.");
            }

            for (uint i = 0; i < scene->MNumMeshes; i++)
            {
                Assimp.Mesh* mesh = scene->MMeshes[i];
                if (IsTriangles(mesh) && mesh->MNormals is null)
                {
                    return Result.Failure<Model>($"its mesh {CString.Read(mesh->MName.Data)} has no normals, which are imported, never made: export it with them.");
                }
            }

            List<Mesh> meshes = [];
            Walk(scene, scene->MRootNode, Matrix4x4.Identity, ToEngine(scene, format), origin, meshes);
            return Result.Success(new Model { Meshes = meshes });
        }
        finally
        {
            Assimp.ReleaseImport(scene);
        }
    }

    private static bool IsTriangles(Assimp.Mesh* mesh) => (mesh->MPrimitiveTypes & (uint)Assimp.PrimitiveType.Triangle) != 0;

    /// <summary>
    /// Bakes the triangle meshes of <paramref name="node"/> and those under it into <paramref name="meshes"/>, each
    /// with its node's transform under <paramref name="parent"/>.
    /// </summary>
    private static void Walk(Assimp.Scene* scene, Assimp.Node* node, Matrix4x4 parent, Matrix4x4 toEngine, Vector3 origin, List<Mesh> meshes)
    {
        // Assimp's matrices have the translation in the last column; transposed, they are System.Numerics' row vectors.
        Matrix4x4 world = Matrix4x4.Transpose(Unsafe.BitCast<Assimp.Matrix4x4, Matrix4x4>(node->MTransformation)) * parent;
        for (uint i = 0; i < node->MNumMeshes; i++)
        {
            Assimp.Mesh* source = scene->MMeshes[node->MMeshes[i]];
            if (IsTriangles(source) && source->MNumVertices > 0) meshes.Add(Bake(source, world * toEngine, origin));
        }

        for (uint i = 0; i < node->MNumChildren; i++)
        {
            Walk(scene, node->MChildren[i], world, toEngine, origin, meshes);
        }
    }

    /// <summary>
    /// <paramref name="source"/> in the engine's frame, moved by <paramref name="transform"/> and then by
    /// <paramref name="origin"/> to (0, 0, 0).
    /// </summary>
    private static Mesh Bake(Assimp.Mesh* source, Matrix4x4 transform, Vector3 origin)
    {
        // Directions along the surface move with the transform; normals with its inverse transpose.
        Matrix4x4 normals = Matrix4x4.Invert(transform, out Matrix4x4 inverse) ? Matrix4x4.Transpose(inverse) : transform;
        // The texture coordinate channels are an array of pointers, read as one: an inline array of pointers has no indexer.
        Assimp.Vector3D* uvs = *(Assimp.Vector3D**)&source->MTextureCoords;
        bool hasTangents = source->MTangents is not null && source->MBitangents is not null;
        Vertex[] vertices = new Vertex[source->MNumVertices];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(ToVector3(source->MNormals[i]), normals));
            Vector4 tangent = new(1, 0, 0, 1);
            if (hasTangents)
            {
                Vector3 along = Vector3.Normalize(Vector3.TransformNormal(ToVector3(source->MTangents[i]), transform));
                Vector3 bitangent = Vector3.TransformNormal(ToVector3(source->MBitangents[i]), transform);
                tangent = new Vector4(along, Vector3.Dot(Vector3.Cross(normal, along), bitangent) < 0 ? -1 : 1);
            }

            vertices[i] = new Vertex
            {
                Position = Vector3.Transform(ToVector3(source->MVertices[i]), transform) - origin,
                Normal = normal,
                Tangent = tangent,
                Uv = uvs is null ? Vector2.Zero : new Vector2(uvs[i].X, uvs[i].Y),
            };
        }

        // A transform that mirrors turns each triangle's winding over; swapping two corners turns it back.
        bool mirrored = transform.GetDeterminant() < 0;
        uint[] indices = new uint[source->MNumFaces * 3];
        for (int face = 0; face < source->MNumFaces; face++)
        {
            uint* corners = source->MFaces[face].MIndices;
            indices[face * 3] = corners[0];
            indices[(face * 3) + 1] = corners[mirrored ? 2 : 1];
            indices[(face * 3) + 2] = corners[mirrored ? 1 : 2];
        }

        // A mesh naming a slot past the renderer's last is drawn with the last.
        return new Mesh { Vertices = vertices, Indices = indices, MaterialSlot = Math.Min((int)source->MMaterialIndex, MaterialSlots.Capacity - 1) };
    }

    private static Vector3 ToVector3(Assimp.Vector3D vector) => Unsafe.BitCast<Assimp.Vector3D, Vector3>(vector);

    /// <summary>
    /// What Assimp hands over, to the engine's frame. glTF and OBJ come right-handed, in metres, +Y up, the front at +Z.
    /// An FBX comes in its own unit and axes, untouched, and names them: its unit in centimetres (UnitScaleFactor) and
    /// which of its axes, and which way, are right, up and front. Those are turned into metres and that same frame, so
    /// a model exported from Blender is its size there whatever the export's scale and axes. Then an X mirror takes the
    /// right-handed frame to the engine's left-handed one, the front staying at +Z.
    /// </summary>
    private static Matrix4x4 ToEngine(Assimp.Scene* scene, string format)
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
    private static void Turn(ref Matrix4x4 frame, Assimp.Scene* scene, string key, int to)
    {
        int from = (int)GetNumber(scene, key, to);
        frame[from is >= 0 and <= 2 ? from : to, to] = GetNumber(scene, key + "Sign", 1) < 0 ? -1 : 1;
    }

    /// <summary>
    /// The number the scene's metadata holds under <paramref name="key"/>, whatever its width, or
    /// <paramref name="fallback"/> where there is none or it is not a number.
    /// </summary>
    private static double GetNumber(Assimp.Scene* scene, string key, double fallback)
    {
        Assimp.Metadata* metadata = scene->MMetaData;
        if (metadata is null) return fallback;

        for (uint i = 0; i < metadata->MNumProperties; i++)
        {
            if (!string.Equals(CString.Read(metadata->MKeys[i].Data), key, StringComparison.Ordinal)) continue;

            Assimp.MetadataType type = metadata->MValues[i].MType;
            void* data = metadata->MValues[i].MData;
            if (type == Assimp.MetadataType.AiBool) return *(byte*)data != 0 ? 1 : 0;

            if (type == Assimp.MetadataType.AiInt32) return *(int*)data;

            if (type == Assimp.MetadataType.AiUint32) return *(uint*)data;

            if (type == Assimp.MetadataType.AiInt64) return *(long*)data;

            if (type == Assimp.MetadataType.AiUint64) return *(ulong*)data;

            if (type == Assimp.MetadataType.AiFloat) return *(float*)data;

            return type == Assimp.MetadataType.AiDouble ? *(double*)data : fallback;
        }

        return fallback;
    }
}
