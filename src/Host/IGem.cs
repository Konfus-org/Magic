namespace Magic.Host;

/// <summary>
/// A gem synonymous with a plugin.
/// </summary>
public interface IGem
{
    void Step(in Frame frame) { } // main thread, once a frame
    void FixedStep(in Frame frame) { } // main thread, 0 to 4 times a frame
    void Render(in Frame frame) { } // render thread, drawing the frame Step just finished
    void Stop() { } // once both threads are done
}
