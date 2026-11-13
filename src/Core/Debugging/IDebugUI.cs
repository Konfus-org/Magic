using System.Numerics;
using Magic.Assets;

namespace Magic.Debugging;

/// <summary>
/// What a gem or script shows the developer while the game runs. A UI gem provides it and decides how it looks.
/// </summary>
/// <remarks>
/// <para>
/// <c>Text</c>, and what <see cref="Log"/> is told to show on screen, shows over the game and outside any window: a few
/// words at a place in the world, or a line on screen. Report it from any thread; it shows with the next frame drawn.
/// The same text at the same place again keeps it up, and does not add another.
/// </para>
/// <para>
/// The widgets, <see cref="Begin(string, bool)"/> to <see cref="MenuItem"/>, draw windows in immediate mode: the calls
/// in a frame's Update are that frame's windows, so draw a window in each frame that it is open. Call them only on the
/// game thread, in Update of a gem made after the provider. A widget's label is drawn on its left, and the widget
/// stretches to the width of its parent. The part of a label from <c>##</c> on is not shown, and tells apart widgets
/// with the same label. A widget answers from the input of the frame it is called in.
/// </para>
/// </remarks>
public interface IDebugUI
{
    /// <summary>
    /// Whether what is reported shows now: skip building text while it is false. Logging never depends on it.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Shows <paramref name="text"/> at <paramref name="position"/> in the world in the next frame drawn. Call it in
    /// each frame that it shows.
    /// </summary>
    /// <remarks>
    /// Write a few words; put detail in the log. <paramref name="tooltip"/> shows while the mouse rests on the text. A
    /// null <paramref name="color"/> is the UI's own.
    /// </remarks>
    void Text(Vector3 position, string text, Color? color = null, string? tooltip = null);

    /// <summary>
    /// Shows <paramref name="text"/> as a line on screen in the next frame drawn. Call it in each frame that it shows.
    /// </summary>
    /// <remarks>
    /// The line goes below the frame's earlier lines and above the lines shown for a while. It is not logged.
    /// </remarks>
    void Text(string text, Color? color = null);

    /// <summary>
    /// Opens a movable window titled <paramref name="title"/> that holds what is drawn until <see cref="End"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While another is open, it opens a view nested in that one, as wide as its parent and as tall as the room left
    /// after what follows the view.
    /// </para>
    /// <para>
    /// With <paramref name="scrollable"/>, the user sizes a window and it scrolls; otherwise it fits its contents. A
    /// scrollable nested view follows what is added at the bottom while it is scrolled to the bottom.
    /// </para>
    /// </remarks>
    void Begin(string title, bool scrollable = false);

    /// <summary>
    /// Opens a window as <see cref="Begin(string, bool)"/> does, with a close button that sets
    /// <paramref name="visible"/> to false.
    /// </summary>
    /// <remarks>
    /// After a click, stop drawing the window from the next frame, but still call this frame's <see cref="End"/>. A
    /// nested view has no header, so it has no button.
    /// </remarks>
    void Begin(string title, ref bool visible, bool scrollable = false);

    /// <summary>
    /// Closes what the last Begin opened.
    /// </summary>
    void End();

    /// <summary>
    /// A line of text in the window; a null <paramref name="color"/> is the UI's own.
    /// </summary>
    void Label(string text, Color? color = null);

    /// <summary>
    /// True when clicked.
    /// </summary>
    bool Button(string label);

    /// <summary>
    /// Draws a one-line text field that edits <paramref name="text"/>. True when Enter was pressed in it.
    /// </summary>
    /// <remarks>
    /// With <paramref name="focus"/>, it takes the keyboard when its window appears. Each field keeps the keyboard
    /// after Enter. <paramref name="hint"/> shows grey in the empty field, to say what a field with a hidden label
    /// takes.
    /// </remarks>
    bool Input(string label, ref string text, string? hint = null, bool focus = false);

    /// <summary>
    /// Draws a field of many lines that edits <paramref name="text"/>, as tall as its lines. True when changed.
    /// </summary>
    /// <remarks>
    /// <paramref name="readOnly"/> text can still be selected and copied.
    /// </remarks>
    bool Document(string label, ref string text, bool readOnly = false);

    /// <summary>
    /// True when changed.
    /// </summary>
    bool Checkbox(string label, ref bool value);

    /// <summary>
    /// Draws a number to drag left and right, or to type after a double-click. True when changed.
    /// </summary>
    /// <remarks>
    /// The value stays within <paramref name="min"/> to <paramref name="max"/>. Equal bounds, the default, set no
    /// limit.
    /// </remarks>
    bool Slider(string label, ref float value, float min = 0f, float max = 0f);

    /// <summary>
    /// One of <paramref name="options"/>, by index. True when changed.
    /// </summary>
    bool Choice(string label, ref int index, ReadOnlySpan<string> options);

    /// <summary>
    /// Draws a row of tabs, one of <paramref name="options"/> selected by index. True when changed.
    /// </summary>
    /// <remarks>
    /// What is drawn after it belongs to the selected tab. <paramref name="label"/> only tells the rows of one window
    /// apart.
    /// </remarks>
    bool Tabs(string label, ref int index, ReadOnlySpan<string> options);

    /// <summary>
    /// Draws a header that folds away what is under it. True while open: draw what is under it only then.
    /// </summary>
    /// <remarks>
    /// It starts open as <paramref name="isOpenAtStart"/> says, then toggles on a click. <paramref name="depth"/> nests
    /// it under the headers before it: it and what follows are indented one step for each, until the next header.
    /// </remarks>
    bool Header(string label, bool isOpenAtStart = true, int depth = 0);

    /// <summary>
    /// Opens a table headed by <paramref name="columns"/>. Draw rows with <see cref="Row"/>, then call
    /// <see cref="EndTable"/>.
    /// </summary>
    /// <remarks>
    /// Each column is as wide as its widest cell, and its edge drags to resize it. <paramref name="label"/> only tells
    /// the tables of one window apart.
    /// </remarks>
    void Table(string label, ReadOnlySpan<string> columns);

    /// <summary>
    /// Draws a row of the open table, a cell per column in order. Missing cells are empty.
    /// </summary>
    /// <remarks>
    /// A <see cref="Tooltip"/> after it shows while the mouse rests anywhere on the row.
    /// </remarks>
    void Row(ReadOnlySpan<string> cells);

    /// <summary>
    /// Closes what the last Table opened.
    /// </summary>
    void EndTable();

    /// <summary>
    /// Shows <paramref name="text"/> while the mouse rests on what was drawn last.
    /// </summary>
    void Tooltip(string text);

    /// <summary>
    /// Adds an item to the right-click menu of the current window or nested view. True in the frame it is clicked.
    /// </summary>
    bool MenuItem(string label);
}
