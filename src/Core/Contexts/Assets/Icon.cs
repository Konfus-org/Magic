using Magic.Interfaces;

namespace Magic.Contexts.Assets;

/// <summary>
/// The application's icon: a <c>.icon</c> file naming the image a window shows in its title bar and task bar,
/// <c>{ "image": { "id": 7436761896624509111 } }</c>.
/// </summary>
public sealed class Icon : IJsonAsset<Icon>
{
    public static IReadOnlyList<string> Extensions { get; } = [".icon"];

    public Handle<Icon> Handle { get; init; }

    public required Handle<Texture> Image { get; init; }
}
