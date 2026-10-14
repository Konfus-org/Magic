namespace Magic.DebugTools;

/// <summary>
/// What the debug windows show. Set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set DebugTools.meters=["Magic"]</c>.
/// </summary>
internal sealed class DebugToolsSettings
{
#if DEBUG
    private const bool IsDebug = true;
#else
    private const bool IsDebug = false;
#endif

    /// <summary>
    /// The frame rate and a frame's time, averaged over half a second, in the top left corner: by default in a Debug
    /// build, where the engine's speed is what is being watched.
    /// </summary>
    public bool FrameRate { get; set; } = IsDebug;

    /// <summary>
    /// The meters the telemetry window shows, a tab each in this order, each with the meters under it: "Magic" is Core's
    /// and every gem's "Magic.Gem", and "System.Runtime" .NET's own: garbage collection, memory, the JIT and the thread
    /// pool.
    /// </summary>
    public IReadOnlyList<string> Meters { get; set; } = ["Magic", "System.Runtime"];
}
