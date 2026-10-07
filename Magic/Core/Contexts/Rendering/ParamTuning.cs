namespace Magic.Contexts.Assets;

/// <summary>
/// How the settings window shows one param of a pass or post; nothing here reaches the shader. <see cref="Min"/> equal
/// to <see cref="Max"/> (the default) is no range.
/// </summary>
public sealed class ParamTuning
{
    /// <summary>
    /// <c>"Tab/Header"</c>; empty is the pass's.
    /// </summary>
    public string Group { get; set; } = "";

    /// <summary>
    /// Plain words for the param; empty is its name split into words.
    /// </summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// What it does, shown while the mouse rests on it.
    /// </summary>
    public string Description { get; set; } = "";

    public float Min { get; set; }

    public float Max { get; set; }

    /// <summary>
    /// Shown, not edited: the param repeats what the file itself sets (the size of something it creates), so changing
    /// it alone would only make the shader disagree with its resources. Change both in the file.
    /// </summary>
    public bool Fixed { get; set; }
}
