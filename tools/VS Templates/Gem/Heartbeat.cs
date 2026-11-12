using Magic.Debugging;
using Magic.Projects;

namespace Magic.GemName;

/// <summary>
/// Logs that the gem runs, once every <paramref name="frames"/> frames.
/// </summary>
internal sealed class Heartbeat(int frames)
{
    public void Beat(in Frame frame)
    {
        if (frame.Index % frames == 0)
            Log.Info($"GemName is running, at frame {frame.Index}.");
    }
}
