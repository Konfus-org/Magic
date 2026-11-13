using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Magic.Assets;
using Magic.DeferredRendering.Post;
using Magic.DeferredRendering.View;
using Magic.DeferredRendering.World;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.DeferredRendering;

/// <summary>
/// What the frame's effects are resolved from: the world as the GPU holds it.
/// </summary>
public readonly record struct PostInput(GpuWorld World);

/// <summary>
/// Runs the post-processing chain over each view's lit picture, as the frame's <see cref="PictureEffects"/>.
/// </summary>
/// <remarks>
/// <para>
/// The chain is the effects of the first entity's <see cref="PostProcessing"/>, or when no entity has one, the exposure,
/// set by the exposure compensation, and the AgX tonemap. Each effect whose material the settings turn off is left out.
/// </para>
/// <para>
/// Each view keeps its effects' histories. A domain loading waits until its effects are on the GPU, or failed.
/// </para>
/// </remarks>
public sealed class PostRenderer : IRender<PostInput, PictureEffects>, IRender, IHookIntoDomainLoading
{
    /// <summary>
    /// The exposure compensation and the effects turned off, which the debug window also sets on the game thread.
    /// </summary>
    private readonly DeferredRenderingSettings _settings;

    private readonly IEcs _ecs;

    private readonly AssetManager _assets;

    private readonly Threads _threads;

    private readonly Mailbox<EcsSnapshot<PostProcessing>> _processing = new();

    /// <summary>
    /// What is done to the picture when no entity has a <see cref="PostProcessing"/>: the exposure and the AgX tonemap.
    /// </summary>
    private readonly PostProcessing _defaultPost;

    private readonly Effects _effects;

    /// <summary>
    /// Each view's effects, by its number, which keep their histories from frame to frame. Render thread.
    /// </summary>
    private readonly List<PixelEffects> _views = [];

    /// <summary>
    /// The effects every frame hands on, with the jitter of the frame, so a frame makes no delegate.
    /// </summary>
    private readonly PictureEffects _made;

    /// <summary>
    /// The frame's effects and their material rows, which each view records. Render thread.
    /// </summary>
    private (ReadOnlyMemory<GpuEffect> Effects, GpuBuffer Rows) _resolved;

    /// <summary>
    /// The effects a domain loading runs, and what ends once they are on the GPU, or failed. Render thread.
    /// </summary>
    private (MaterialSlots Effects, TaskCompletionSource Done)? _preparing;

    internal PostRenderer(DeferredRenderingSettings settings, AssetManager assets, IEcs ecs, Threads threads)
    {
        _settings = settings;
        _ecs = ecs;
        _assets = assets;
        _threads = threads;
        _defaultPost.Effects[0] = new MaterialInstance { Handle = assets.Find<Material>("Materials/Exposure.mat") };
        _defaultPost.Effects[1] = new MaterialInstance { Handle = assets.Find<Material>("Materials/Agx.mat") };
        _effects = new Effects(assets);
        _made = new PictureEffects(false, Record);
    }

    /// <summary>
    /// Holds the domain loading until the effects of the first post processing of <paramref name="entities"/>, or the
    /// default's, are on the GPU, or failed and left out.
    /// </summary>
    /// <remarks>
    /// The frame's step asks for them. One that comes while another waits ends that one. Render thread.
    /// </remarks>
    public async Task OnDomainLoadingAsync(
        ImmutableArray<ImmutableArray<IComponent>> entities,
        CancellationToken cancel
    )
    {
        await _threads.Enter(EngineThread.Render);
        EndPreparing();
        MaterialSlots post = entities
            .SelectMany(entity => entity.OfType<PostProcessing>())
            .Select(processing => processing.Effects)
            .DefaultIfEmpty(_defaultPost.Effects)
            .First();
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _preparing = (post, done);
        await done.Task.WaitAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the frame's effects and uploads their material rows.
    /// </summary>
    /// <returns>
    /// The frame's effects, jittered while one that is ready asks for it; none while their rows are not made.
    /// </returns>
    public PictureEffects? Render(in Frame frame, RenderGraph graph, in PostInput input)
    {
        MaterialPacker pack = input.World.PackMaterial;
        if (_preparing is { } preparing && !_effects.IsLoading(graph, preparing.Effects, pack))
            EndPreparing();

        // The first post processing's effects, or the engine's, exposed by the exposure compensation; each whose
        // material the settings turn off left out, which most frames name none of.
        MaterialSlots post;
        if (_processing.TakeNewest() is { Count: > 0 } processing)
            post = processing.First[0].Effects;
        else
        {
            post = _defaultPost.Effects;
            post[0].Overrides["compensation"] = _settings.ExposureCompensation;
        }

        ImmutableArray<string> disabled = _settings.DisabledEffects;
        for (int place = 0; place < MaterialSlots.Capacity && !disabled.IsEmpty; place++)
        {
            if (
                _assets.GetPath(post[place].Handle) is { } path
                && disabled.Contains(Path.GetFileNameWithoutExtension(path))
            )
                post[place] = default;
        }

        Result<(ReadOnlyMemory<GpuEffect> Effects, GpuBuffer Rows)> effects = _effects.Resolve(graph, post, pack);
        if (!effects.Ok)
        {
            graph.Fail(RenderFailure.PostEffect, "Effects are not drawn", effects.Error);

            return null;
        }

        _resolved = effects.Value;
        bool isJittered = false;
        foreach (GpuEffect effect in _resolved.Effects.Span)
        {
            isJittered |= effect.State == GpuState.Ready && effect.Needs.HasJitter;
        }

        return _made with
        {
            IsJittered = isJittered,
        };
    }

    /// <summary>
    /// Publishes the world's post processing for the frame's effects.
    /// </summary>
    internal void PublishPostProcessing()
    {
        _processing.Back.Fill(_ecs);
        _processing.Publish();
    }

    /// <summary>
    /// Lets the loading domain show when the world's step is out, as the effects are then not resolved.
    /// </summary>
    void IRender.Render(in Frame frame, RenderGraph graph)
    {
        if (graph.IsMakerOut<GpuWorld>())
            EndPreparing();
    }

    /// <summary>
    /// Runs the frame's effects that are ready over the view's lit picture.
    /// </summary>
    private void Record(RenderGraph graph, GpuView view, uint frameIndex)
    {
        while (_views.Count <= graph.View)
        {
            _views.Add(new PixelEffects());
        }

        _views[graph.View].Record(graph, view, _resolved.Effects.Span, _resolved.Rows, frameIndex);
    }

    private void EndPreparing()
    {
        _preparing?.Done.TrySetResult();
        _preparing = null;
    }
}
