using System.Collections.ObjectModel;
using Magic.Contexts.Components;
using Magic.Interfaces;

namespace Magic.Contexts.Assets;

/// <summary>
/// Passes in order, as data: a <c>.pipeline</c> file. Each entry is a <see cref="Pass"/> or another pipeline, whose
/// passes go in its place, and may give the passes it brings in other params, by the pass's file name and the param's.
/// A renderer's frame is a pipeline, which its settings pick; so is each effect a world can add, and the list of them a
/// world's <see cref="PostProcessing"/> names: <c>{ "entries": [ { "pipeline": { "id": 10360 } }, { "pipeline": { "id":
/// 10370 }, "params": { "Tonemap": { "exposureBias": { "x": 0.5 } } } } ] }</c>. Each pass runs in its own stage, so a
/// pipeline only orders the passes of one stage.
/// </summary>
public sealed class Pipeline : IJsonAsset<Pipeline>
{
    public static IReadOnlyList<string> Extensions { get; } = [".pipeline"];

    public Handle<Pipeline> Handle { get; init; }

    /// <summary>
    /// What the file lists, as it lists it.
    /// </summary>
    public IReadOnlyList<PipelineEntry> Entries { get; set; } = [];

    /// <summary>
    /// The passes it runs, in order: its entries with each nested pipeline's passes in its place. Made as it loads, by
    /// a gem that post-processes pipelines, which replaces whatever the file says; none without that gem.
    /// </summary>
    public IReadOnlyList<PipelinePass> Passes { get; init; } = [];
}

/// <summary>
/// One entry of a <see cref="Pipeline"/>: a <see cref="Pass"/> or a pipeline, and param values for the passes it brings
/// in, which win over theirs.
/// </summary>
public sealed class PipelineEntry
{
    public Handle<Pass> Pass { get; set; }

    public Handle<Pipeline> Pipeline { get; set; }

    /// <summary>
    /// By the file name of a pass the entry brings in, without its extension, then by the param's name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, MaterialParam>> Params { get; set; }
        = ReadOnlyDictionary<string, IReadOnlyDictionary<string, MaterialParam>>.Empty;
}

/// <summary>
/// One pass a pipeline runs (<see cref="Pipeline.Passes"/>): the pass, the name its file gives it, which settings and
/// pipelines know it by, and its params' values, those of the machine's settings first, then those of the entries that
/// brought it in, the outermost first, then its own. A param none of them sets takes the shader's default.
/// </summary>
public sealed record PipelinePass(Pass Pass, string Name, IReadOnlyDictionary<string, MaterialParam> Params);
