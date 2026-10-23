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
    /// When it last said it was running, on the world's clock.
    /// </summary>
    private double _saidSec;

    public void Update(in Frame frame)
    {
        if (frame.WorldSec - _saidSec < settings.Interval) return;

        _saidSec = frame.WorldSec;
        Debugging.Info($"GemName is running, at frame {frame.Index}.");
    }
}
