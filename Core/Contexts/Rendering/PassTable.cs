using Magic.Contexts.Assets;

namespace Magic.Contexts.Rendering;

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
    public bool Ready => Pass.Enabled && Error is null && Pipeline.IsValid;
}

/// <summary>The custom passes in (stage, order, path) order, their compiles, and the fullscreen vertex shader every fragment pass is built on.</summary>
internal sealed class PassTable(CompiledShader fullscreenVertex)
{
    /// <summary>More inputs than a pass can bind.</summary>
    public const int MaxInputs = 16;

    public CompiledShader FullscreenVertex { get; } = fullscreenVertex;

    public List<PassState> Passes { get; } = [];

    public Compiles<ulong> Compiles { get; } = new();

    public int IndexOf(ulong id)
    {
        return Passes.FindIndex(p => p.Id == id);
    }
}
