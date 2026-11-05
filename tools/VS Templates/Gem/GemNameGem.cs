using Magic.Debugging;
using Magic.Projects;

namespace GemName;

/// <summary>
/// The gem: the one class in its assembly that implements <see cref="IGem"/>. Magic makes it after whatever its
/// constructor takes and calls the hooks it implements every frame. Every other interface is what it provides to the gems
/// made after it. It is public because nothing in its assembly makes it: Magic does, by reflection, when a project lists the
/// gem.
/// </summary>
public sealed class GemNameGem : IGem
{
    public void Update(in Frame frame)
    {
        if (frame.Index % 60 == 0)
            Log.Info($"GemName is running, at frame {frame.Index}.");
    }
}
