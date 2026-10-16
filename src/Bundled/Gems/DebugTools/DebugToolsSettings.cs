using System.ComponentModel;
using Magic.Interfaces;

namespace Magic.DebugTools;

/// <summary>
/// What the debug windows show. Set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set DebugTools.frameRate=false</c>.
/// </summary>
[Category("Debug")]
internal sealed class DebugToolsSettings : ISettings
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
    /// The telemetry window's tabs, in this order: each a title, and the meters under it, by their name or a prefix of
    /// it. "Magic.Core" is Core's meter and "Magic.Gems" every gem's, "Magic.Gems.&lt;Gem&gt;"; "System.Runtime" is
    /// .NET's own: garbage collection, memory, the JIT and the thread pool.
    /// </summary>
    public IReadOnlyDictionary<string, string> Tabs { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Magic.Core"] = "Magic.Core",
        ["Magic.Gems"] = "Magic.Gems",
        [".NET"] = "System.Runtime",
    };
}
