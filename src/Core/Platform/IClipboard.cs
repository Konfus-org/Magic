namespace Magic.Platform;

/// <summary>
/// The OS clipboard, for copy and paste.
/// </summary>
/// <remarks>
/// A platform gem provides it. Use it only on the game thread.
/// </remarks>
public interface IClipboard
{
    /// <summary>
    /// The text on the clipboard, or empty if there is none.
    /// </summary>
    string Text { get; set; }
}
