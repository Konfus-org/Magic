using Magic.Projects;

namespace Magic.GemName;

/// <summary>
/// The gem: the one class in its assembly that implements <see cref="IGem"/>. Magic makes it after whatever its
/// constructor takes and calls the hooks it implements every frame. Each public property is a part that it makes: what the
/// gem provides to the gems made after it.
/// </summary>
public sealed class EntryPoint : IGem
{
    private readonly Heartbeat _heartbeat = new(frames: 60);

    public void Update(in Frame frame) => _heartbeat.Beat(frame);
}
