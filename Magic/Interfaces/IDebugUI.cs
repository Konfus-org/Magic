namespace Magic.Interfaces;

/// <summary>
/// Immediate-mode widgets, exported by a UI gem. Not a service: like <see cref="ILogger"/>, every one the loaded gems
/// export gets every <see cref="Utils.Debugging.UI"/> call. Main thread only: every call between two of the UI gem's
/// Render hooks makes up what it draws in the next one. Answers come from input that arrived before the last draw.
/// A widget's label is drawn on its left and the widget stretches to the width of what it is in.
/// </summary>
public interface IDebugUI
{
    /// <summary>
    /// Opens a movable window titled <paramref name="title"/>; everything until <see cref="End"/> goes in it. Called while
    /// another is open, it is a view nested in that one instead, as wide as its parent and as tall as the room the parent
    /// has left after what follows the view. <paramref name="scrollable"/>: a window is sized by the user and scrolls
    /// (otherwise it fits its content); a nested view scrolls and, while scrolled to the bottom, follows what is added there.
    /// </summary>
    void Begin(string title, bool scrollable = false);

    void End();

    void Text(string text);

    /// <summary>True when the button was clicked.</summary>
    bool Button(string label);

    /// <summary>A one-line text field editing <paramref name="text"/>; true when Enter was pressed in it. Takes the keyboard when its panel appears.</summary>
    bool Input(string label, ref string text);

    /// <summary>Multi-line text editing <paramref name="text"/>, as tall as its lines; <paramref name="readOnly"/> text can still be selected and copied. True when changed.</summary>
    bool Document(string label, ref string text, bool readOnly = false);

    /// <summary>True when changed.</summary>
    bool Checkbox(string label, ref bool value);

    /// <summary>
    /// A number dragged left and right (or typed after a double-click), kept within <paramref name="min"/>..<paramref name="max"/>;
    /// equal bounds (the default) mean none. True when changed.
    /// </summary>
    bool Slider(string label, ref float value, float min = 0f, float max = 0f);

    /// <summary>One of <paramref name="options"/>, by index. True when changed.</summary>
    bool Choice(string label, ref int index, string[] options);
}
