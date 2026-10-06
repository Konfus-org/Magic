using Magic.Contexts.Assets;

namespace Magic.Interfaces;

/// <summary>
/// What kind of value a pass parameter is, as its shader declares it.
/// </summary>
public enum TunableKind : byte
{
    Float,
    Float2,
    Float3,
    Float4,
    Int,
    Uint,
    Bool,
    Color
}

/// <summary>
/// One parameter of a pass as it runs now: its name in the shader's <c>PassParams</c>, its kind, its value, and how the
/// settings window shows it (its <see cref="ParamTuning.Group"/> always filled in: the param's, the pass's, or the stage's).
/// </summary>
public readonly record struct TunableParam(string Name, TunableKind Kind, Param Value, ParamTuning Tuning);

/// <summary>
/// One pass of the pipeline as listed: its id, the stage it runs in, its name, where the settings window shows it
/// (<see cref="Pass.Group"/>, or the stage's name), what is wrong with it (null when nothing), and its parameters.
/// </summary>
public sealed record TunablePass(ulong Id, string Stage, string Name, string Group, string? Error, IReadOnlyList<TunableParam> Params);

/// <summary>
/// The render pipeline's passes with their parameters as they run: exported by the renderer gem, listed by the
/// settings window. A parameter is changed as any setting is, by its <c>"PassName.param"</c> key through
/// <see cref="Services.Settings.Set"/>. Main thread only.
/// </summary>
public interface IPipelineTuning
{
    /// <summary>
    /// Every pass listed this frame, in the order they run, the post-processing last.
    /// </summary>
    IReadOnlyList<TunablePass> Passes { get; }
}
