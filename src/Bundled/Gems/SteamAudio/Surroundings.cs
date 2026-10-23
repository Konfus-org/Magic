using System.Numerics;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.SteamAudio;

/// <summary>
/// One solid as the main thread gathered it: its entity, what it is shaped like and made of, and where it is.
/// </summary>
internal readonly record struct Solid(
    Entity Entity,
    Handle<Model> Model,
    AcousticMaterial Material,
    AcousticMesh Mesh,
    Transform Transform);

/// <summary>
/// What is solid near the player, as sound hears it: every entity with a <see cref="Transform"/> and a
/// <see cref="Physics"/> that stays put or is moved by code, near the player's cameras, with the triangles of its
/// model, fitted once per model on a worker and kept until a new world takes the old one's place without it
/// (<see cref="Forget"/>), and the material its <see cref="Acoustics"/> says. Gathered a few times a second on the
/// main thread, which reads the world, for the simulation thread to bring the acoustic scene up to date with
/// (<see cref="SoundWorld"/>). A body that falls and is pushed is left out: it moves every step, and placing each move
/// in Steam Audio's scene costs far more than what a crate or a ball does to what is heard.
/// </summary>
internal sealed class Surroundings(Assets assets)
{
    /// <summary>
    /// How much farther than the range a solid goes before it leaves, so one at the edge is not added and taken out
    /// every other frame.
    /// </summary>
    private const float Leave = 1.25f;

    /// <summary>
    /// Each model's triangles once a worker has fitted them; null while its model loads.
    /// </summary>
    private readonly Dictionary<Handle<Model>, AcousticMesh?> _meshes = [];

    /// <summary>
    /// Models fitted on workers, for the main thread to take in at the start of its hook.
    /// </summary>
    private readonly Channel<(Handle<Model> Model, AcousticMesh Mesh)> _fitted = Channel
        .CreateUnbounded<(Handle<Model> Model, AcousticMesh Mesh)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly AcousticMesh _unitCube = AcousticMesh.MakeCube();

    /// <summary>
    /// The entities gathered last frame, which stay until a little past the range, and those gathered this frame.
    /// </summary>
    private HashSet<Entity> _wasNear = [];
    private HashSet<Entity> _near = [];

    /// <summary>
    /// The farthest any model fitted here reaches from its origin at scale 1, and at least 1: at an entity's scale, a
    /// bound on how far it could reach, which tells from its transform alone whether it could be near.
    /// </summary>
    internal float Longest { get; private set; } = 1;

    /// <summary>
    /// Adds every model a solid among <paramref name="parts"/>, and the parts under them, is shaped like.
    /// </summary>
    internal static void AddModels(IReadOnlyList<PrefabPart> parts, HashSet<Handle<Model>> models)
    {
        foreach (PrefabPart part in parts)
        {
            // Whether the part's body stays put or is moved by code, and the model it is drawn as.
            Handle<Model> model = Handle<Model>.None;
            bool isSolid = false;
            foreach (IComponent component in part.Components)
            {
                isSolid |= component is Physics { Motion: not Motion.Dynamic };
                model = component is Renderer renderer ? renderer.Model : model;
            }

            if (isSolid && model != Handle<Model>.None) models.Add(model);

            AddModels(part.Children, models);
        }
    }

    /// <summary>
    /// Takes in the models workers fitted since the last frame.
    /// </summary>
    internal void TakeArrived()
    {
        while (_fitted.Reader.TryRead(out (Handle<Model> Model, AcousticMesh Mesh) fitted))
        {
            _meshes[fitted.Model] = fitted.Mesh;
            Longest = Math.Max(Longest, fitted.Mesh.Reach);
        }
    }

    /// <summary>
    /// Lets go of the triangles of every model not in <paramref name="kept"/>, so the next solid shaped like one fits
    /// it again, and works the farthest reach out again over those left; a model still being fitted stays, to be let go
    /// at a later forget once it is there.
    /// </summary>
    internal void Forget(HashSet<Handle<Model>> kept)
    {
        // Listed first, as the models cannot change while they are walked.
        List<Handle<Model>> gone = [];
        Longest = 1;
        foreach ((Handle<Model> model, AcousticMesh? mesh) in _meshes)
        {
            if (mesh is null) continue;

            if (kept.Contains(model)) Longest = Math.Max(Longest, mesh.Reach);
            else gone.Add(model);
        }

        foreach (Handle<Model> model in gone)
        {
            _meshes.Remove(model);
        }

        if (gone.Count > 0)
            Debugging.Verbose($"{gone.Count} models' triangles let go, which only the world before needed.");
    }

    /// <summary>
    /// Whether <paramref name="model"/> has been fitted, asking for it if it has not been asked for.
    /// </summary>
    internal bool IsFitted(Handle<Model> model) => GetMesh(model) is not null;

    /// <summary>
    /// Lists into <paramref name="into"/> every solid near <paramref name="listeners"/>, or every solid when there are
    /// none, whose model is fitted: one within <paramref name="range"/>, or a little past it if it was near last frame.
    /// </summary>
    internal void Gather(World world, ReadOnlySpan<Vector3> listeners, float range, List<Solid> into)
    {
        // Last frame's near entities are what may stay out to the far end; this frame's start empty. Each body is
        // passed over from its transform alone first, and only one that could be near has its model looked up.
        into.Clear();
        (_wasNear, _near) = (_near, _wasNear);
        _near.Clear();
        float farthest = range * Leave;
        foreach (EcsChunk<Transform, Physics> chunk in world.Query<Transform, Physics>())
        {
            (ReadOnlySpan<Entity> entities, Span<Transform> transforms, Span<Physics> physics) = chunk;
            for (int i = 0; i < entities.Length; i++)
            {
                if (physics[i].Motion == Motion.Dynamic || !CanBeNear(transforms[i], listeners, farthest)) continue;

                Entity entity = entities[i];
                Handle<Model> model = world.Has<Renderer>(entity)
                    ? world.Get<Renderer>(entity).Model
                    : Handle<Model>.None;

                if (GetMesh(model) is not { } mesh
                    || !IsNear(transforms[i], mesh, listeners, _wasNear.Contains(entity) ? farthest : range))
                {
                    continue;
                }

                AcousticMaterial material = world.Has<Acoustics>(entity)
                    ? world.Get<Acoustics>(entity).Material
                    : AcousticMaterial.Generic;
                into.Add(new Solid(entity, model, material, mesh, transforms[i]));
                _near.Add(entity);
            }
        }
    }

    /// <summary>
    /// Whether the entity's surface reaches within <paramref name="range"/> of a listener: its centre's distance less
    /// how far its model reaches at its scale, so the ground is near from anywhere on it. With no listener, everything
    /// is.
    /// </summary>
    private static bool IsNear(in Transform transform, AcousticMesh mesh, ReadOnlySpan<Vector3> listeners, float range)
    {
        if (listeners.IsEmpty) return true;

        float reach = Vector3.Abs(transform.Scale).Length() * mesh.Reach;
        foreach (Vector3 listener in listeners)
        {
            if (Vector3.Distance(transform.Position, listener) - reach < range) return true;
        }

        return false;
    }

    /// <summary>
    /// The model's triangles, asking for them if they have not been asked for; null while they are fitted.
    /// </summary>
    private AcousticMesh? GetMesh(Handle<Model> model)
    {
        if (model == Handle<Model>.None) return _unitCube;

        if (!_meshes.TryGetValue(model, out AcousticMesh? mesh))
        {
            _meshes.Add(model, null);
            FitAsync(model).FireAndForget();
        }

        return mesh;
    }

    /// <summary>
    /// Loads and fits the model on a worker, for the main thread to take in; one that does not load, or has no
    /// triangles, is a unit cube, as its body is.
    /// </summary>
    private async Task FitAsync(Handle<Model> model)
    {
        Result<Model> loaded = await assets.LoadAsync(model, CancellationToken.None).ConfigureAwait(false);
        AcousticMesh mesh = loaded.Ok ? AcousticMesh.Of(loaded.Value) ?? _unitCube : _unitCube;
        _fitted.Writer.TryWrite((model, mesh));
    }

    /// <summary>
    /// Whether the entity could be within <paramref name="range"/> of a listener, from its transform alone: its
    /// centre's distance less the farthest any model reaches at its scale, so the many far off are passed over without
    /// a look at their model. With no listener, everything could be.
    /// </summary>
    private bool CanBeNear(in Transform transform, ReadOnlySpan<Vector3> listeners, float range)
    {
        if (listeners.IsEmpty) return true;

        float limit = range + (Vector3.Abs(transform.Scale).Length() * Longest);
        foreach (Vector3 listener in listeners)
        {
            if (Vector3.DistanceSquared(transform.Position, listener) < limit * limit) return true;
        }

        return false;
    }
}
