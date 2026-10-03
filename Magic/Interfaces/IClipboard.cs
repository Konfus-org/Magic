namespace Magic.Interfaces;

/// <summary>
/// The system clipboard, exported by the windowing gem. Main thread only.
/// </summary>
public interface IClipboard
{
    /// <summary>
    /// The clipboard's text: empty when it holds none. Setting it replaces whatever it held.
    /// </summary>
    string Text { get; set; }
}
