namespace Magic.Assets.Types;

/// <summary>
/// The application's icon: a <c>.icon</c> file naming the image a window shows in its title bar and task bar,
/// <c>{ "image": { "id": 7436761896624509111 } }</c>.
/// </summary>
[AssetFormat(AssetFileFormat.Json)]
public sealed class Icon : IAsset<Icon>
{
    public required Handle<Texture> Image { get; init; }
}
