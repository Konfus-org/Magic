using Magic.Attributes.Assets;

namespace Magic.Contexts.Assets;

/// <summary>
/// The stages a frame is rendered in, in this order. A stage says what its passes run over: <see cref="Shadows"/> and
/// <see cref="Gi"/> once a frame with the main view's constants; <see cref="Scene"/>, <see cref="Lighting"/> and
/// <see cref="Transparency"/> once per view of each render target; <see cref="Sky"/> and <see cref="Post"/> once per
/// render target, with its first view's constants.
/// </summary>
public enum PipelineStage : byte
{
    Shadows,
    Gi,
    Scene,
    Lighting,
    Sky,
    Transparency,

    /// <summary>
    /// The world's <see cref="Components.PostProcessing"/> list, not the pipeline file's.
    /// </summary>
    Post
}

/// <summary>
/// How a frame is rendered, as data: a <c>.pipeline</c> file listing the <see cref="Pass"/> assets of each stage, in the
/// order they run. The project names one (<c>"pipeline"</c> in the <c>.magic</c> file, <c>--pipeline</c> overrides it);
/// without one the engine's <c>Pipelines/Default.pipeline</c> renders. The Post stage is not here: the world's
/// <see cref="Components.PostProcessing"/> lists it, since what a scene looks like at the end is the scene's business.
/// </summary>
[AssetFormat(AssetFormat.Json)]
public sealed class Pipeline : Asset
{
    /// <summary>
    /// The engine's own pipeline, under Resources.
    /// </summary>
    public const string DefaultPath = "Pipelines/Default.pipeline";

    public Handle<Pass>[] Shadows { get; set; } = [];

    public Handle<Pass>[] Gi { get; set; } = [];

    public Handle<Pass>[] Scene { get; set; } = [];

    public Handle<Pass>[] Lighting { get; set; } = [];

    public Handle<Pass>[] Sky { get; set; } = [];

    public Handle<Pass>[] Transparency { get; set; } = [];

    /// <summary>
    /// The passes of <paramref name="stage"/>, in order; none for <see cref="PipelineStage.Post"/>.
    /// </summary>
    public Handle<Pass>[] StageOf(PipelineStage stage)
    {
        return stage switch
        {
            PipelineStage.Shadows => Shadows,
            PipelineStage.Gi => Gi,
            PipelineStage.Scene => Scene,
            PipelineStage.Lighting => Lighting,
            PipelineStage.Sky => Sky,
            PipelineStage.Transparency => Transparency,
            _ => [],
        };
    }
}
