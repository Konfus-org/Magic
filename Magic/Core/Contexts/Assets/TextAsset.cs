namespace Magic.Contexts.Assets;

/// <summary>
/// An asset whose file is text, kept as it is: the asset manager reads the file into <see cref="Text"/>.
/// </summary>
public abstract class TextAsset : Asset
{
    public string Text { get; internal set; } = "";
}
