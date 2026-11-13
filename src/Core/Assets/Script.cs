namespace Magic.Assets;

/// <summary>
/// The source text of a script: what an entity does, in the language of the file's extension, such as <c>.cs</c>.
/// </summary>
/// <remarks>
/// An entity has a script when its <see cref="Ecs.Components.ScriptContainer"/> holds the handle of the script. A gem
/// for the language runs it. A gem for a compiled language can run the code that the project compiled from the file
/// instead of the text.
/// </remarks>
[AssetFormat(AssetFileFormat.Text)]
public sealed class Script : IAsset<Script>
{
    public required string Text { get; init; }
}
