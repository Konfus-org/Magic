using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;

namespace DeferredRendererGem;

/// <summary>
/// What a stage's passes run over: once a frame, once per render target, or once per view of each target.
/// </summary>
internal enum PassScope : byte
{
    Frame,
    Target,
    View
}

/// <summary>
/// One pass as loaded and compiled: its file (a <c>.pass</c>, or a <c>.post</c> turned into one), the stage it is
/// listed in, its shaders, the parameter values it runs with (the file's until the settings window changes them)
/// packed by its layout, and what the last good compile made (<see cref="NeedsPipeline"/> while a compile has landed
/// that the pipeline is not built from yet). A material draw has no pipeline of its own: it draws with the material
/// pipelines. Not ready until its file and shaders have arrived: <see cref="PassLoader"/> fills it in.
/// </summary>
internal sealed class PassState(ulong id, PipelineStage stage)
{
    public ulong Id { get; } = id;

    public PipelineStage Stage { get; } = stage;

    public string Path { get; set; } = "";

    public bool IsPost { get; set; } = stage == PipelineStage.Post;

    public Pass Pass { get; set; } = new();

    public ulong ShaderId { get; set; }

    public ulong FragmentId { get; set; }

    public Dictionary<string, Param> Values { get; set; } = [];

    public byte[] Params { get; set; } = new byte[ParamLayout.RecordBytes];

    public ParamLayout? Layout { get; set; }

    public GpuPipeline Pipeline { get; set; }

    public CompiledShader? Compiled { get; set; }

    public CompiledShader? CompiledFragment { get; set; }

    public bool PingPong { get; set; }

    public bool NeedsPipeline { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// Nothing is wrong with it and it has what it draws with: it runs.
    /// </summary>
    public bool Ready => Error is null && (Pipeline.IsValid || UsesMaterialPipelines);

    /// <summary>
    /// A draw of the scene's materials: the engine's pipelines, composed per surface, not one of the pass's own.
    /// </summary>
    public bool UsesMaterialPipelines => Pass.Kind == PassKind.Draw && Pass.Draw.Pipeline != DrawPipeline.Depth;

    /// <summary>
    /// Whether its shaders have all compiled, whatever became of the pipeline.
    /// </summary>
    public bool HasCompiled => Compiled is not null && (!NeedsFragment || CompiledFragment is not null);

    /// <summary>
    /// A depth draw and a quads pass have a vertex and a fragment shader of their own.
    /// </summary>
    public bool NeedsFragment => Pass.Kind == PassKind.Quads || (Pass.Kind == PassKind.Draw && Pass.Draw.Pipeline == DrawPipeline.Depth);

    /// <summary>
    /// The scope a stage's passes run in.
    /// </summary>
    public static PassScope ScopeOf(PipelineStage stage)
    {
        return stage switch
        {
            PipelineStage.Shadows or PipelineStage.Gi => PassScope.Frame,
            PipelineStage.Sky or PipelineStage.Post => PassScope.Target,
            _ => PassScope.View,
        };
    }
}
