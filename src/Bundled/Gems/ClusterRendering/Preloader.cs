using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Subscriptions;
using Magic.Interfaces;

namespace Magic.ClusterRendering;

/// <summary>
/// Domains taken in before they spawn (<see cref="IPreloader"/>), so each is drawn whole in its first frame: its models
/// with their fields and its materials held from when it begins, the models as the nearest, and the frame built for the
/// effects it names while the world names none, so the loading domain is drawn with them and nothing is built again at
/// the switch. Whoever opens the domain waits until all of it is in, or failed and so drawn as the failure, and its
/// holds go when what it was handed is disposed. One that replaces the world is then readied: drawn out of sight, the
/// window holding the loading domain's last image, until what builds up over frames has settled
/// (<see cref="Settle"/>). Asked from any thread; what it is told is taken in on the render thread.
/// </summary>
internal sealed class Preloader(GpuResources resources, PassGraph graph)
{
    /// <summary>
    /// The domains being taken in, each checked every frame until all it draws is in (<see cref="Check"/>). Render
    /// thread.
    /// </summary>
    private readonly List<Preload> _preloads = [];

    /// <summary>
    /// What other threads told the render thread, taken in as <see cref="Take"/> runs, in the order told: a domain to
    /// begin taking in, one taken in to let go, or one whose open was cancelled; a domain spawned to begin readying, or
    /// one whose readying was cancelled.
    /// </summary>
    private readonly Channel<Told> _told = Channel.CreateUnbounded<Told>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// The effects the latest domain taken in names; none once that domain is let go.
    /// </summary>
    private Handle<Pipeline> _effects;

    /// <summary>
    /// The domain being readied, spawned in place of the world, until it has settled; none when none is. Render thread.
    /// </summary>
    private Readying? _readying;

    /// <summary>
    /// Whether the window shows the image kept as the world loaded rather than the world: from when a domain replacing
    /// the world begins being readied until the world is shown (<see cref="Show"/>), or its readying is cancelled.
    /// Render thread.
    /// </summary>
    internal bool IsHolding { get; private set; }

    /// <summary>
    /// How many frames the domain being readied has been drawn so far, from its first; none while none is, or once it
    /// has settled. What builds up over frames reads it to take its first frames faster. Render thread.
    /// </summary>
    internal int? Settling => _readying?.Frames;

    /// <summary>
    /// Takes in what <paramref name="prefabs"/> draw before they spawn: their models in, their materials
    /// packed with their textures in and every surface's pipelines compiled, and the frame built for the effects they
    /// name. Done once all of it is in, or failed, and so drawn as the failure; what it hands back keeps it
    /// until it is disposed.
    /// </summary>
    internal async Task<Subscription> PreloadAsync(
        IReadOnlyList<Prefab> prefabs,
        IProgress<float> progress,
        CancellationToken cancel)
    {
        Preloaded preloaded = Preloaded.Of(prefabs);
        Preload preload = new(preloaded, progress);
        await using ConfiguredAsyncDisposable cancelling = cancel.Register(
            () => _told.Writer.TryWrite(new Told(Cancelled: preload, Cancel: cancel))).ConfigureAwait(false);
        _told.Writer.TryWrite(new Told(Begun: preload));
        await preload.Done.Task.ConfigureAwait(false);
        return new Subscription(() => _told.Writer.TryWrite(new Told(Ended: preloaded)));
    }

    /// <summary>
    /// Readies the domain taken in, spawned in place of the world: from the next frame drawn, the window holds the
    /// image kept as the world loaded, and the world is drawn out of sight until it has settled (<see cref="Settle"/>),
    /// when the task ends; the window shows the world once it is shown (<see cref="Show"/>).
    /// </summary>
    internal async Task ReadyAsync(CancellationToken cancel)
    {
        Readying readying = new();
        await using ConfiguredAsyncDisposable cancelling = cancel.Register(
            () => _told.Writer.TryWrite(new Told(Unready: readying, Cancel: cancel))).ConfigureAwait(false);
        _told.Writer.TryWrite(new Told(Ready: readying));
        await readying.Done.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Takes in what other threads told since the last frame, in the order told. Render thread.
    /// </summary>
    internal void Take()
    {
        while (_told.Reader.TryRead(out Told told))
        {
            if (told.Begun is { } begun) Begin(begun);

            if (told.Ended is { } ended) End(ended);

            if (told.Cancelled is { } cancelled) Cancel(cancelled, told.Cancel);

            if (told.Ready is { } ready) BeginReadying(ready);

            if (told.Unready is { } unready) CancelReadying(unready, told.Cancel);
        }
    }

    /// <summary>
    /// The frame drawn of the domain being readied is counted, and once <paramref name="isSettled"/>, all that builds up
    /// over frames having settled, it is ready: whoever waits for it goes on, while the window holds the kept image
    /// until the world is shown. Render thread, each frame once it is drawn.
    /// </summary>
    internal void Settle(bool isSettled)
    {
        if (_readying is not { } readying) return;

        readying.Frames++;
        if (!isSettled) return;

        readying.Done.TrySetResult();
        _readying = null;
    }

    /// <summary>
    /// The world is shown: the window shows it from this frame, no longer the kept image. Render thread.
    /// </summary>
    internal void Show()
    {
        IsHolding = false;
    }

    /// <summary>
    /// The effects the frame is built for: the world's, else those of the latest domain being taken in, so its listing
    /// is compiled under the loading domain, which names none of its own.
    /// </summary>
    internal Handle<Pipeline> GetEffects(Handle<Pipeline> world)
    {
        return world == Handle<Pipeline>.None ? _effects : world;
    }

    /// <summary>
    /// Checks each domain being taken in: each model that can be drawn, its field in, or failed and is drawn as the
    /// failure cube, each material shown, and the frame built for <paramref name="effects"/>, tell how far it is; once
    /// all are in, whoever waits for it goes on and spawns it. Render thread.
    /// </summary>
    internal void Check(Handle<Pipeline> effects)
    {
        if (_preloads.Count == 0) return;

        bool isBuilt = graph.IsBuiltFor(effects);
        for (int i = _preloads.Count - 1; i >= 0; i--)
        {
            Preload preload = _preloads[i];
            Preloaded preloaded = preload.Preloaded;
            int total = 1 + preloaded.Models.Count + preloaded.Materials.Count;
            int done = isBuilt ? 1 : 0;
            foreach (Handle<Model> model in preloaded.Models)
            {
                if (resources.IsDrawable(model) && resources.HasField(model)) done++;
            }

            foreach (Handle<Material> material in preloaded.Materials)
            {
                if (resources.IsShown(material)) done++;
            }

            float share = (float)done / total;
            if (share != preload.Reported)
            {
                preload.Reported = share;
                preload.Progress.Report(share);
            }

            if (done != total) continue;

            _preloads.RemoveAt(i);
            preload.Done.TrySetResult();
        }
    }

    /// <summary>
    /// Starts taking in what a domain draws: its models and materials are held, which starts their loads,
    /// the models as the nearest, and the frame is built for its effects.
    /// </summary>
    private void Begin(Preload preload)
    {
        Preloaded preloaded = preload.Preloaded;
        foreach (Handle<Model> model in preloaded.Models)
        {
            resources.Record(model);
            resources.Prioritize(model, 0);
        }

        foreach (Handle<Material> material in preloaded.Materials)
        {
            resources.Record(material);
        }

        if (preloaded.Effects != Handle<Pipeline>.None) _effects = preloaded.Effects;

        _preloads.Add(preload);
    }

    /// <summary>
    /// A domain that was taken in is let go: its holds on its models and materials go, so what nothing else holds
    /// leaves, and the frame no longer builds for its effects.
    /// </summary>
    private void End(Preloaded preloaded)
    {
        foreach (Handle<Model> model in preloaded.Models)
        {
            resources.Release(model);
        }

        foreach (Handle<Material> material in preloaded.Materials)
        {
            resources.Release(material);
        }

        if (preloaded.Effects == _effects) _effects = Handle<Pipeline>.None;
    }

    /// <summary>
    /// The open of a domain being taken in was cancelled before all it draws was in: it is let go now, and whoever
    /// waited is told; one already in is let go by whoever holds it.
    /// </summary>
    private void Cancel(Preload preload, CancellationToken cancel)
    {
        if (!_preloads.Remove(preload)) return;

        End(preload.Preloaded);
        preload.Done.TrySetCanceled(cancel);
    }

    /// <summary>
    /// A domain spawned in place of the world begins being readied, from this frame, in place of one being readied
    /// still, which can no longer settle as it is gone; the window holds the kept image.
    /// </summary>
    private void BeginReadying(Readying readying)
    {
        _readying?.Done.TrySetCanceled(CancellationToken.None);
        _readying = readying;
        IsHolding = true;
    }

    /// <summary>
    /// The readying of a domain was cancelled, as it closed: whoever waited is told, and the window shows what is drawn
    /// in it, as the loading domain is again, once it is there.
    /// </summary>
    private void CancelReadying(Readying readying, CancellationToken cancel)
    {
        readying.Done.TrySetCanceled(cancel);
        if (_readying != readying) return;

        _readying = null;
        IsHolding = false;
    }

    /// <summary>
    /// One thing told to the render thread: a domain to begin taking in, one taken in to let go, or one whose open was
    /// cancelled; or a domain spawned to begin readying, or one whose readying was cancelled; with the token that
    /// cancelled it.
    /// </summary>
    private readonly record struct Told(
        Preload? Begun = null,
        Preloaded? Ended = null,
        Preload? Cancelled = null,
        Readying? Ready = null,
        Readying? Unready = null,
        CancellationToken Cancel = default);

    /// <summary>
    /// A domain being taken in before it spawns: what it draws, how far that is as last told, and the task whoever
    /// opens it awaits.
    /// </summary>
    private sealed class Preload(Preloaded preloaded, IProgress<float> progress)
    {
        internal Preloaded Preloaded { get; } = preloaded;

        internal IProgress<float> Progress { get; } = progress;

        internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal float Reported { get; set; } = -1;
    }

    /// <summary>
    /// A domain spawned in place of the world being readied: how many frames it has been drawn, and the task whoever
    /// opens it awaits.
    /// </summary>
    private sealed class Readying
    {
        internal int Frames { get; set; }

        internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// What the renderer takes in for a domain before it spawns: the models and materials its prefabs draw, the sky's among
/// them, and the effects they name, none when they name none.
/// </summary>
internal sealed record Preloaded(
    HashSet<Handle<Model>> Models,
    HashSet<Handle<Material>> Materials,
    Handle<Pipeline> Effects)
{
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
