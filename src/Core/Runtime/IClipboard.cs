namespace Magic.Runtime;

/// <summary>
/// The OS's clipboard, which copy and paste go through. A platform gem provides it. Main thread.
/// </summary>
public interface IClipboard
{
    /// <summary>
    /// The text on it; empty when there is none.
    /// </summary>
    string Text { get; set; }
}
