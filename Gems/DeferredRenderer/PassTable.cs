using Magic.Contexts.Rendering;
using Magic.Contexts.Assets;
using Magic.Utils;

namespace DeferredRendererGem;

/// <summary>
/// One custom pass as loaded and compiled: its file, whether it reads its own output, its shader, its packed parameters,
/// the format of its output, and what the last good compile made (the compiled shader says compute or fragment).
/// </summary>
internal sealed record PassState(
    ulong Id,
    string Path,
    Pass Pass,
    bool PingPong,
    ulong ShaderId,
    byte[] Params,
    GpuFormat OutputFormat,
    GpuPipeline Pipeline,
    CompiledShader? Compiled,
    string? Error)
{
    public bool Ready => Error is null && Pipeline.IsValid;
}

/// <summary>
/// The passes the pass list names, id to <see cref="PassState"/>, reference counted by the list, with their compiles and
/// the fullscreen vertex shader every fragment pass is built on.
/// </summary>
internal sealed class PassTable(CompiledShader fullscreenVertex) : RefCountTable<ulong, PassState>
{
    /// <summary>
    /// More inputs than a pass can bind.
    /// </summary>
    public const int MaxInputs = 16;

    public CompiledShader FullscreenVertex { get; } = fullscreenVertex;

    /// <summary>
    /// The ids the pass list named when it was last synced: one reference each.
    /// </summary>
    public List<ulong> Held { get; } = [];

    public Pending<ulong, Result<CompiledShader>> Compiles { get; } = new();
}
