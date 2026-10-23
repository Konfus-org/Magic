using System.Numerics;
using System.Runtime.CompilerServices;
using Magic.Contexts.Assets;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// What a gem or script shows the developer while the game runs, provided by a UI gem, which decides how it looks and
/// when it shows.
/// <para>
/// What is reported (<c>Text</c>, <see cref="Info"/>, <see cref="Warn"/>, <see cref="Error"/>) shows for a
/// while, over the game and outside any window: a few words at a place in the world, or a line on screen. Report it
/// from any thread; it shows with the next frame drawn. Info, Warn and Error are logged through <see cref="Debugging"/>
/// too, whether anything shows or not. Reporting the same text at the same place again while it shows keeps it up
/// rather than adding another.
/// </para>
/// <para>
/// Windows are drawn with the widgets, <see cref="Begin(string, bool)"/> to <see cref="MenuItem"/>, in immediate mode:
/// what is called in a frame's Update is that frame's windows, so a window is drawn every frame it is open. They are
/// the main thread's, called in Update by a gem made after the provider, as one that takes this is. A widget's label is
/// drawn on its left, and the widget stretches to the width of what it is in; the part of a label from <c>##</c> on is
/// not shown, and tells apart widgets that show the same label. A widget answers from the input of the frame it is
/// called in.
/// </para>
/// </summary>
public interface IDebugUI
{
    /// <summary>
    /// Whether what is reported shows now: skip building text while it is false. Logging never depends on it.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// <paramref name="text"/> at <paramref name="position"/> in the world, in the next frame drawn: call it every
    /// frame it should show. A few words, a quick why: the log is where detail goes. <paramref name="tooltip"/> shows
    /// while the mouse rests on the text; a null <paramref name="color"/> is the UI's own.
    /// </summary>
    void Text(Vector3 position, string text, Color? color = null, string? tooltip = null);

    /// <summary>
    /// <paramref name="text"/> as a line on screen, in the next frame drawn, above the lines shown for a while: call it
    /// every frame it should show, as a frame rate is. Never logged.
    /// </summary>
    void Text(string text, Color? color = null);

    /// <summary>
    /// A line on screen for <paramref name="seconds"/>, null for the UI's own time, logged as information. The same
    /// line again while it shows is one line, with how often.
    /// </summary>
    void Info(
        string text,
        float? seconds = null,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0);

    /// <summary>
    /// A warning: at <paramref name="position"/> in the world, or a line on screen without one. It stays
    /// <paramref name="seconds"/> after it was last reported (null for the UI's own time, 0 for only while it is
    /// reported every frame), and is logged as a warning when it appears, not again while it stays.
    /// <paramref name="tooltip"/> shows while the mouse rests on it.
    /// </summary>
    void Warn(
        string text,
        Vector3? position = null,
        float? seconds = null,
        string? tooltip = null,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0);

    /// <summary>
    /// <see cref="Warn"/> for an error, logged as an error.
    /// </summary>
    void Error(
        string text,
        Vector3? position = null,
        float? seconds = null,
        string? tooltip = null,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0);

    /// <summary>
    /// Opens a movable window titled <paramref name="title"/>, and what is drawn until <see cref="End"/> goes in it.
    /// Called while another is open, it is a view nested in that one instead, as wide as its parent and as tall as the
    /// room the parent has left after what follows the view. <paramref name="scrollable"/>: a window is sized by the
    /// user and scrolls, where otherwise it fits what is in it; a nested view scrolls, and while it is scrolled to the
    /// bottom it follows what is added there.
    /// </summary>
    void Begin(string title, bool scrollable = false);

    /// <summary>
    /// <see cref="Begin(string, bool)"/> for a window with a close button in its header, which sets
    /// <paramref name="visible"/> to false when clicked: stop drawing the window from the next frame. This frame's
    /// <see cref="End"/> is still owed. A nested view has no header, so no button.
    /// </summary>
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
    /// A one-line text field editing <paramref name="text"/>; true when Enter was pressed in it. With
    /// <paramref name="focus"/> it takes the keyboard when its window appears, as the one field a window is for does;
    /// any field keeps the keyboard after Enter. <paramref name="hint"/> shows greyed in the field while it is empty,
    /// for a field whose label is hidden to say what it takes.
    /// </summary>
    bool Input(string label, ref string text, string? hint = null, bool focus = false);

    /// <summary>
    /// Text of many lines editing <paramref name="text"/>, as tall as its lines; <paramref name="readOnly"/> text can
    /// still be selected and copied. True when changed.
    /// </summary>
    bool Document(string label, ref string text, bool readOnly = false);

    /// <summary>
    /// True when changed.
    /// </summary>
    bool Checkbox(string label, ref bool value);

    /// <summary>
    /// A number dragged left and right, or typed after a double-click, kept within <paramref name="min"/> to
    /// <paramref name="max"/>; equal bounds, as by default, are none. True when changed.
    /// </summary>
    bool Slider(string label, ref float value, float min = 0f, float max = 0f);

    /// <summary>
    /// One of <paramref name="options"/>, by index. True when changed.
    /// </summary>
    bool Choice(string label, ref int index, ReadOnlySpan<string> options);

    /// <summary>
    /// A row of tabs across the window, one of <paramref name="options"/> selected by index: what is drawn after it is
    /// the selected tab's. <paramref name="label"/> only tells the rows of one window apart. True when changed.
    /// </summary>
    bool Tabs(string label, ref int index, ReadOnlySpan<string> options);

    /// <summary>
    /// A header that folds away what is drawn under it, open until clicked; <paramref name="folds"/> folds it this frame
    /// as a click would, for whoever draws it to fold it once and leave it to clicks after. True while open: draw what is
    /// under it only then.
    /// </summary>
    bool Header(string label, bool folds = false);

    /// <summary>
    /// Opens a table headed by <paramref name="columns"/>, whose rows are drawn with <see cref="Row"/> until
    /// <see cref="EndTable"/>: each column as wide as its widest cell, and resized by dragging its edge.
    /// <paramref name="label"/> only tells the tables of one window apart.
    /// </summary>
    void Table(string label, ReadOnlySpan<string> columns);

    /// <summary>
    /// A row of the table open, a cell per column in order, the rest empty when there are fewer. The row is what was
    /// drawn last, so a <see cref="Tooltip"/> after it shows while the mouse rests anywhere on it.
    /// </summary>
    void Row(ReadOnlySpan<string> cells);

    /// <summary>
    /// Closes what the last Table opened.
    /// </summary>
    void EndTable();

    /// <summary>
    /// <paramref name="text"/> shown while the mouse rests on what was drawn last.
    /// </summary>
    void Tooltip(string text);

    /// <summary>
    /// An item of the menu a right-click anywhere in the window or nested view it is called in opens, in the order
    /// called. True in the frame it is clicked.
    /// </summary>
    bool MenuItem(string label);
}
