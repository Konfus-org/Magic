using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Magic.Interfaces;

/// <summary>
/// What a gem or script shows the developer for a while, without a window of its own: a few words at a place in the
/// world, and notes, warnings and errors in the list down the left of the main window. Shown while
/// <see cref="Enabled"/>; warnings and errors are logged too. Any thread.
/// </summary>
public interface IDebugUI
{
    /// <summary>
    /// Metres from a camera beyond which text anchored in the world is not shown in its view.
    /// </summary>
    const float TextDistance = 50f;

    /// <summary>
    /// Seconds a note, warning or error stays in the list, unless the call says otherwise.
    /// </summary>
    const float ScreenSeconds = 5f;

    /// <summary>
    /// Whether any of it shows: always in a Debug build, in a Release build only while a debug window is open. Skip
    /// building text while it is false. Logging never depends on it.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Text at a world position, shown in every view that has it in front of its camera and within
    /// <see cref="TextDistance"/> metres. A few words, a quick "why"; the log is where detail goes. Call it every
    /// frame the text should show.
    /// </summary>
    void Text(Vector3 position, string text, Color? color = null);

    /// <summary>
    /// A line in the list for <paramref name="seconds"/>, logged as information; the same line again while it shows
    /// is one line with how often.
    /// </summary>
    void Info(string text, float seconds = ScreenSeconds, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0);

    /// <summary>
    /// A warning, as <c>!!! text !!!</c> in orange: at <paramref name="position"/> in the world, or in the list without
    /// one. It stays <paramref name="seconds"/> after it was last reported (0: only while reported every frame), and
    /// is logged when it appears, not again while it stays up.
    /// </summary>
    void Warning(string text, Vector3? position = null, float seconds = ScreenSeconds, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0);

    /// <summary>
    /// <see cref="Warning"/> for an error: red, and logged as an error.
    /// </summary>
    void Error(string text, Vector3? position = null, float seconds = ScreenSeconds, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0);
}
