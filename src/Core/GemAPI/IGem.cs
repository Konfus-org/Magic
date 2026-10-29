namespace Magic.GemAPI;

/// <summary>
/// A gem synonymous with a plugin.
/// </summary>
public interface IGem
{
    void Step(in Frame frame) { } // once a frame
    void FixedStep(in Frame frame) { } // 0 to 4 times a frame, after Step, 60 times a simulated second
    void Render(in Frame frame) { } // once a frame, after the fixed steps, drawing what Step left
}
