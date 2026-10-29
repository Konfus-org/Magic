using System.Diagnostics.CodeAnalysis;
using Magic.Debugging;
using Magic.GemAPI;

namespace GemName;

/// <summary>
/// The gem: the one class in its assembly that implements <see cref="IGem"/>. Magic makes it after whatever its
/// constructor takes, and every other interface it implements is what it provides to the gems made after it.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Magic makes it by reflection when a project lists the gem."
)]
internal sealed class GemNameGem : IGem
{
    public void Step(in Frame frame)
    {
        if (frame.Index % 60 == 0)
            Log.Info($"GemName is running, at frame {frame.Index}.");
    }
}
