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
/// One parameter of a pass as it runs now: its name in the shader's <c>PassParams</c>, its kind, and its value.
/// </summary>
public readonly record struct TunableParam(string Name, TunableKind Kind, Param Value);

/// <summary>
/// One pass of the pipeline as listed: its id, the stage it runs in, its name, what is wrong with it (null when
/// nothing), and its parameters.
/// </summary>
public sealed record TunablePass(ulong Id, string Stage, string Name, string? Error, IReadOnlyList<TunableParam> Params);

/// <summary>
/// The render pipeline's passes with their parameters, for editing live: exported by the renderer gem, read by the
/// settings window. A value set here holds until the pass's file changes, and is never written to disk. Main thread
/// only.
/// </summary>
public interface IPipelineTuning
{
    /// <summary>
    /// Every pass listed this frame, in the order they run, the post-processing last.
    /// </summary>
    IReadOnlyList<TunablePass> Passes { get; }

    /// <summary>
    /// Sets a parameter of a pass; the pass runs with it from the next frame. Nothing for a name the pass does not declare.
    /// </summary>
    void Set(ulong pass, string name, Param value);
}
