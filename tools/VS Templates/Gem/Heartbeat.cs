using Magic.Debugging;
using Magic.Projects;

namespace Magic.GemName;

/// <summary>
/// Logs that the gem runs, one time every <see cref="GemNameSettings.HeartbeatFrames"/> frames.
/// </summary>
internal sealed class Heartbeat(GemNameSettings settings)
{
    public void Beat(in Frame frame)
    {
        if (settings.HeartbeatFrames > 0 && frame.Index % settings.HeartbeatFrames == 0)
            Log.Info($"GemName is running, at frame {frame.Index}.");
    }
}
