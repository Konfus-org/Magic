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
    public bool Ready => Error is null && Pipeline.IsValid;
}

/// <summary>The passes some camera lists, their compiles, and the fullscreen vertex shader every fragment pass is built on.</summary>
internal sealed class PassTable(CompiledShader fullscreenVertex)
{
    /// <summary>More inputs than a pass can bind.</summary>
    public const int MaxInputs = 16;

    public CompiledShader FullscreenVertex { get; } = fullscreenVertex;

    public List<PassState> States { get; } = [];

    /// <summary>The ids every camera lists this frame; what is loaded and not in it is unloaded.</summary>
    public HashSet<ulong> Listed { get; } = [];

    public Compiles<ulong> Compiles { get; } = new();

    public int IndexOf(ulong id)
    {
        return States.FindIndex(pass => pass.Id == id);
    }
}
