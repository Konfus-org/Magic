using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Interfaces;

namespace Magic.ClusterRendering;

/// <summary>
/// What the renderer takes in for what streams into the world (<see cref="WorldStream"/>), worked out from each frame's
/// copy of it (<see cref="IPreloader"/>): what the world's domains draw is held in the pool while they name it, its
/// models and materials from when it begins to be taken in, the models as the nearest, and the frame built for the
/// effects it names while the world names none, so the loading domain is drawn with them and nothing is built again at
/// the switch; and how far all that is, and whether the world being readied has settled, is written down each frame
/// for the main thread to ask (<see cref="GetProgress"/>, <see cref="IsReady"/>). Render thread, but where a member
/// says otherwise.
/// </summary>
internal sealed class Preloader(GpuResources resources, PassGraph graph)
{
    /// <summary>
    /// What the world's domains draw, held in the pool, and of it what is still taken in, as of the stream's version
    /// they were worked out for.
    /// </summary>
    private Preloaded _held = Preloaded.None;
    private Preloaded _takingIn = Preloaded.None;
    private int _version = -1;

    /// <summary>
    /// How far the render thread last found what is taken in, and whether the world being readied had settled, for the
    /// main thread to read; a reference written whole.
    /// </summary>
    private Found? _found;

    /// <summary>
    /// How far what <paramref name="stream"/> takes in is, as the render thread last found it for that same stream;
    /// none while it has yet to. Main thread.
    /// </summary>
    internal float GetProgress(in WorldStream stream)
        => Volatile.Read(ref _found) is { } found && found.Version == stream.Version ? found.Progress : 0;

    /// <summary>
    /// Whether the world being readied has settled, as the render thread last found it for <paramref name="stream"/>.
    /// Main thread.
    /// </summary>
    internal bool IsReady(in WorldStream stream)
        => Volatile.Read(ref _found) is { } found && found.Version == stream.Version && found.IsSettled;

    /// <summary>
    /// Holds in the pool what <paramref name="stream"/>'s domains draw, once a version of it: what it names since is
    /// held, what it is taking in made the nearest, then what it no longer names let go, so what is still named is
    /// never let go between.
    /// </summary>
    internal void Take(in WorldStream stream)
    {
        if (stream.Version == _version) return;

        Preloaded held = Preloaded.Of(stream.Prefabs);
        Preloaded takingIn = Preloaded.Of(stream.TakingIn);
        foreach (Handle<Model> model in held.Models)
        {
            if (!_held.Models.Contains(model)) resources.Record(model);
        }

        foreach (Handle<Material> material in held.Materials)
        {
            if (!_held.Materials.Contains(material)) resources.Record(material);
        }

        foreach (Handle<Model> model in takingIn.Models)
        {
            resources.Prioritize(model, 0);
        }

        foreach (Handle<Model> model in _held.Models)
        {
            if (!held.Models.Contains(model)) resources.Release(model);
        }

        foreach (Handle<Material> material in _held.Materials)
        {
            if (!held.Materials.Contains(material)) resources.Release(material);
        }

        _held = held;
        _takingIn = takingIn;
        _version = stream.Version;
    }

    /// <summary>
    /// The effects the frame is built for: the world's, else those of what is taken in, so its listing is compiled
    /// under the loading domain, which names none of its own.
    /// </summary>
    internal Handle<Pipeline> GetEffects(Handle<Pipeline> world) => world == Handle<Pipeline>.None
        ? _takingIn.Effects
        : world;

    /// <summary>
    /// Writes down, for the main thread, how far what <paramref name="stream"/> takes in is: each model that can be
    /// drawn, its field in, or failed and drawn as the failure cube, each material shown, and the frame built for
    /// <paramref name="effects"/>; and whether the world being readied <paramref name="isSettled"/>. Each frame, once
    /// what came in is taken in.
    /// </summary>
    internal void Answer(in WorldStream stream, Handle<Pipeline> effects, bool isSettled)
    {
        int total = 1 + _takingIn.Models.Count + _takingIn.Materials.Count;
        int done = graph.IsBuiltFor(effects) ? 1 : 0;
        foreach (Handle<Model> model in _takingIn.Models)
        {
            if (resources.IsDrawable(model) && resources.HasField(model)) done++;
        }

        foreach (Handle<Material> material in _takingIn.Materials)
        {
            if (resources.IsShown(material)) done++;
        }

        Volatile.Write(ref _found, new Found(stream.Version, (float)done / total, isSettled));
    }

    /// <summary>
    /// What the render thread found of one version of what streams into the world: how far what it takes in is, and
    /// whether the world being readied had settled.
    /// </summary>
    private sealed record Found(int Version, float Progress, bool IsSettled);
}

/// <summary>
/// What the renderer takes in for what streams into the world: the models and materials its prefabs draw, the sky's
/// among them, and the effects they name, none when they name none.
/// </summary>
internal sealed record Preloaded(
    HashSet<Handle<Model>> Models,
    HashSet<Handle<Material>> Materials,
    Handle<Pipeline> Effects)
{
    /// <summary>
    /// Nothing to take in.
    /// </summary>
    internal static Preloaded None { get; } = new([], [], Handle<Pipeline>.None);

    /// <summary>
    /// What <paramref name="prefabs"/> draw, from their parts and the parts under them. Any thread.
    /// </summary>
    internal static Preloaded Of(IReadOnlyList<Prefab> prefabs)
    {
        HashSet<Handle<Model>> models = [];
        HashSet<Handle<Material>> materials = [];
        Handle<Pipeline> effects = Handle<Pipeline>.None;
        foreach (Prefab prefab in prefabs)
        {
            Add(prefab.Parts);
        }

        return new Preloaded(models, materials, effects);

        void Add(IReadOnlyList<PrefabPart> parts)
        {
            foreach (PrefabPart part in parts)
            {
                foreach (IComponent component in part.Components)
                {
                    AddComponent(component);
                }

                Add(part.Children);
            }
        }

        void AddComponent(IComponent component)
        {
            switch (component)
            {
                case Renderer renderer:
                    if (renderer.Model != Handle<Model>.None) models.Add(renderer.Model);

                    for (int i = 0; i < MaterialSlots.Capacity; i++)
                    {
                        if (renderer.Materials[i] != Handle<Material>.None) materials.Add(renderer.Materials[i]);
                    }

                    break;
                case Sky { Material: var sky } when sky != Handle<Material>.None:
                    materials.Add(sky);
                    break;
                case PostProcessing { Effects: var named } when named != Handle<Pipeline>.None:
                    effects = named;
                    break;
                default:
                    break;
            }
        }
    }
}
