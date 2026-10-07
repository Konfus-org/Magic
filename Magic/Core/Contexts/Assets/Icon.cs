using Magic.Attributes.Assets;

namespace Magic.Contexts.Assets;

/// <summary>
/// An application's icon: a <c>.icon</c> file naming the image the window shows in its title bar and task bar, and the
/// loading screen on its card. The default one, <c>Icons/Default.icon</c>, is the engine's mage; a project that has
/// its own there shows that instead.
/// </summary>
[AssetFormat(AssetFormat.Json)]
public sealed class Icon : Asset
{
    public Handle<Texture> Image { get; set; }
}
