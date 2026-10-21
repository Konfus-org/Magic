using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace GemName;

/// <summary>
/// The gem: the one class in its assembly that implements <see cref="IGem"/>. Magic makes it when a project lists
/// GemName, after whatever its constructor asks for, Magic's services, other gems' interfaces or its settings, and
/// calls the hooks it implements every frame. Every other interface it implements is what it provides to the gems
/// made after it.
/// </summary>
internal sealed class GemNameGem(GemNameSettings settings) : IGem
{
    /// <summary>
    /// When it last said it was running, in seconds since the run started.
    /// </summary>
    private double _said;

    public void Update(in Frame frame)
    {
        if (frame.Seconds - _said < settings.Interval) return;

        _said = frame.Seconds;
        Debugging.Info($"GemName is running, at frame {frame.Index}.");
    }
}
