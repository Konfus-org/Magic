using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Services;

namespace Magic.SteamAudio;

/// <summary>
/// What sound is muffled and echoed by, in Steam Audio: the solids the main thread gathered (<see cref="Surroundings"/>), each
/// model and material one sub-scene of its triangles, shared by every solid shaped and made alike, and each solid an
/// instance of its sub-scene, moved as its transform moves. The simulation thread's alone, which brings it up to date
/// before each simulation.
/// <para>
/// Steam Audio's space is right-handed, the engine's left-handed, so everything here is the world mirrored along Z: the
/// listener and the sounds are mirrored the same way, so what is to the left stays to the left.
/// </para>
/// <para>
/// Placing a solid costs Steam Audio about a millisecond, so a domain's thousands cannot all come in at once without
/// holding up the direct sound for seconds: each update places the nearest first, for as long as it is given, and the
/// rest in the updates after, from the solids last gathered, which the scene keeps until the next are.
/// </para>
/// </summary>
internal sealed class SoundWorld : IDisposable
{
    private readonly unsafe IPL.Context* _context;
    private readonly unsafe IPL.Scene* _scene;
    private readonly IPL.SceneSettings _settings;
    private readonly Dictionary<(Handle<Model> Model, AcousticMaterial Material), SubScene> _subScenes = [];
    private readonly Dictionary<Entity, Instance> _instances = [];
    private readonly List<Entity> _gone = [];

    /// <summary>
    /// The solids last gathered, which each update brings the scene up to date with.
    /// </summary>
    private readonly List<Solid> _solids = [];
    private readonly Comparison<Solid> _byNearness;

    /// <summary>
    /// Where the listener is as this update sorts the solids by how near they are.
    /// </summary>
    private Vector3 _listener;

    /// <summary>
    /// Which update this is, stamped on each instance seen in it: one not stamped is of a solid no longer gathered.
    /// </summary>
    private uint _update;

    internal unsafe SoundWorld(IPL.Context* context, IPL.Scene* scene, IPL.SceneSettings settings)
    {
        _context = context;
        _scene = scene;
        _settings = settings;
        _byNearness = CompareNearness;
    }

    /// <summary>
    /// Takes every instance out and releases it, then every sub-scene, as the run ends, once nothing simulates.
    /// </summary>
    public unsafe void Dispose()
    {
        foreach (Instance instance in _instances.Values)
        {
            IPL.InstancedMesh* mesh = instance.Mesh;
            IPL.InstancedMeshRemove(mesh, _scene);
            IPL.InstancedMeshRelease(&mesh);
        }

        _instances.Clear();
        foreach (SubScene subScene in _subScenes.Values)
        {
            subScene.Release();
        }

        _subScenes.Clear();
    }

    /// <summary>
    /// How many solids are in the scene.
    /// </summary>
    internal int Count => _instances.Count;

    /// <summary>
    /// Takes <paramref name="solids"/>, newly gathered, for the updates from here on, in place of those before.
    /// </summary>
    internal void Take(List<Solid> solids)
    {
        _solids.Clear();
        _solids.AddRange(solids);
    }

    /// <summary>
    /// Brings the scene up to date with the solids last taken, nearest <paramref name="listener"/> first: a moved solid
    /// moves, one whose model or material changed is made again, one no longer listed goes, and new ones come in for as
    /// long as <paramref name="seconds"/> allows. True when anything changed, so the scene must be committed.
    /// </summary>
    internal unsafe bool Update(Vector3 listener, double seconds)
    {
        long start = Stopwatch.GetTimestamp();
        _update++;
        _listener = listener;
        _solids.Sort(_byNearness);
        bool isChanged = false;
        foreach (Solid solid in _solids)
        {
            ref Instance held = ref CollectionsMarshal.GetValueRefOrNullRef(_instances, solid.Entity);
            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref held) && held.Model == solid.Model && held.Material == solid.Material)
            {
                held.Seen = _update;
                if (held.Transform == solid.Transform) continue;

                IPL.InstancedMeshUpdateTransform(held.Mesh, _scene, ToMatrix(solid.Transform));
                held.Transform = solid.Transform;
                isChanged = true;
                continue;
            }

            if (Stopwatch.GetElapsedTime(start).TotalSeconds > seconds) continue;

            isChanged |= Remove(solid.Entity);
            isChanged |= Add(solid);
        }

        foreach ((Entity entity, Instance instance) in _instances)
        {
            if (instance.Seen != _update) _gone.Add(entity);
        }

        foreach (Entity entity in _gone)
        {
            isChanged |= Remove(entity);
        }

        _gone.Clear();
        return isChanged;
    }

    /// <summary>
    /// <paramref name="position"/> in Steam Audio's space: the world mirrored along Z.
    /// </summary>
    internal static IPL.Vector3 ToVector(Vector3 position)
    {
        return new IPL.Vector3 { X = position.X, Y = position.Y, Z = -position.Z };
    }

    /// <summary>
    /// <paramref name="transform"/> in Steam Audio's space, as it takes a matrix: mirrored along Z on both sides, so it
    /// takes a mirrored point to the mirrored place, and written row by row for column vectors, the transpose of the
    /// engine's row-vector matrix.
    /// </summary>
    internal static unsafe IPL.Matrix4x4 ToMatrix(in Transform transform)
    {
        Matrix4x4 mirror = Matrix4x4.CreateScale(1, 1, -1);
        Matrix4x4 world = mirror * transform.Matrix * mirror;
        IPL.Matrix4x4 matrix = default;
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                matrix.Elements[(row * 4) + column] = world[column, row];
            }
        }

        return matrix;
    }

    private int CompareNearness(Solid a, Solid b)
    {
        return Vector3.DistanceSquared(a.Transform.Position, _listener).CompareTo(Vector3.DistanceSquared(b.Transform.Position, _listener));
    }

    /// <summary>
    /// Places the solid's sub-scene at its transform, making the sub-scene first if no solid has needed it yet; false
    /// when Steam Audio could not, which is logged.
    /// </summary>
    private unsafe bool Add(in Solid solid)
    {
        if (!_subScenes.TryGetValue((solid.Model, solid.Material), out SubScene? subScene))
        {
            subScene = SubScene.Make(_context, _settings, solid.Mesh, solid.Material);
            if (subScene is null) return false;

            _subScenes.Add((solid.Model, solid.Material), subScene);
        }

        IPL.InstancedMeshSettings settings = new() { SubScene = subScene.Scene, Transform = ToMatrix(solid.Transform) };
        IPL.InstancedMesh* instance;
        if (IPL.InstancedMeshCreate(_scene, &settings, &instance) != IPL.Error.Success)
        {
            Debugging.Error($"Entity {solid.Entity.Id} is not heard against: Steam Audio could not place its model.");
            return false;
        }

        IPL.InstancedMeshAdd(instance, _scene);
        _instances.Add(solid.Entity, new Instance { Mesh = instance, Model = solid.Model, Material = solid.Material, Transform = solid.Transform, Seen = _update });
        return true;
    }

    private unsafe bool Remove(Entity entity)
    {
        if (!_instances.Remove(entity, out Instance instance)) return false;

        IPL.InstancedMesh* mesh = instance.Mesh;
        IPL.InstancedMeshRemove(mesh, _scene);
        IPL.InstancedMeshRelease(&mesh);
        return true;
    }

    /// <summary>
    /// One solid in the scene, with what it was made from, which the next update's solids are compared with.
    /// </summary>
    private unsafe struct Instance
    {
        internal IPL.InstancedMesh* Mesh;

        internal Handle<Model> Model;

        internal AcousticMaterial Material;

        internal Transform Transform;

        internal uint Seen;
    }

    /// <summary>
    /// One model made of one material: a scene of its own holding its triangles, which instances place in the world.
    /// </summary>
    private sealed unsafe class SubScene
    {
        private IPL.StaticMesh* _mesh;

        internal IPL.Scene* Scene { get; private set; }

        /// <summary>
        /// The sub-scene of <paramref name="mesh"/>'s triangles, all made of <paramref name="material"/>, committed; null,
        /// with the error logged, when Steam Audio cannot make it.
        /// </summary>
        internal static SubScene? Make(IPL.Context* context, IPL.SceneSettings settings, AcousticMesh mesh, AcousticMaterial material)
        {
            IPL.Scene* scene;
            if (IPL.SceneCreate(context, &settings, &scene) != IPL.Error.Success)
            {
                Debugging.Error("Steam Audio could not make a scene for a model, so it is not heard against.");
                return null;
            }

            IPL.Material made = Materials.Get(material);
            IPL.StaticMesh* staticMesh;
            IPL.Error error;
            fixed (IPL.Vector3* vertices = mesh.Vertices)
            fixed (IPL.Triangle* triangles = mesh.Triangles)
            fixed (int* materials = mesh.Materials)
            {
                IPL.StaticMeshSettings meshSettings = new()
                {
                    NumVertices = mesh.Vertices.Length,
                    NumTriangles = mesh.Triangles.Length,
                    NumMaterials = 1,
                    Vertices = vertices,
                    Triangles = triangles,
                    MaterialIndices = materials,
                    Materials = &made,
                };
                error = IPL.StaticMeshCreate(scene, &meshSettings, &staticMesh);
            }

            if (error != IPL.Error.Success)
            {
                IPL.SceneRelease(&scene);
                Debugging.Error($"Steam Audio could not take a model's {mesh.Triangles.Length} triangles ({error}), so it is not heard against.");
                return null;
            }

            IPL.StaticMeshAdd(staticMesh, scene);
            IPL.SceneCommit(scene);
            return new SubScene { Scene = scene, _mesh = staticMesh };
        }

        internal void Release()
        {
            IPL.StaticMesh* mesh = _mesh;
            IPL.Scene* scene = Scene;
            IPL.StaticMeshRemove(mesh, scene);
            IPL.StaticMeshRelease(&mesh);
            IPL.SceneRelease(&scene);
            _mesh = null;
            Scene = null;
        }
    }
}
