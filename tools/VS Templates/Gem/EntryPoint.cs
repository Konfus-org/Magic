using Magic.Projects;

namespace Magic.GemName;

/// <summary>
/// The gem: the one class in its assembly that implements <see cref="IGem"/>.
/// </summary>
/// <remarks>
/// Magic makes it after whatever its constructor takes, and calls the hooks that it implements every frame. Each public
/// property is a part that it makes: what the gem provides to the gems made after it.
/// </remarks>
public sealed class EntryPoint(GemNameSettings settings) : IGem
{
    private readonly Heartbeat _heartbeat = new(settings);

    /// <summary>
    /// How often the gem logs that it runs. Game thread.
    /// </summary>
    public GemNameSettings Settings { get; } = settings;

    public void Update(in Frame frame) => _heartbeat.Beat(frame);
}
