using Magic.Attributes.Scripts;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Interfaces;
using Magic.Services;

namespace LoadingScreenGem;

/// <summary>
/// Shows the project's icon on its entity: the texture <see cref="Map"/> of the entity's first material becomes
/// the default <see cref="Icon"/>'s image (the project's, else the engine's), before the renderer first draws with
/// it. The material is one asset, so every entity drawn with it shows the icon. For a loading domain.
/// </summary>
[Impatient]
internal sealed class LoadingIcon(Handle entity, IEcs ecs, Assets assets) : IBehavior
{
    /// <summary>
    /// The texture parameter of the material's surface that takes the icon.
    /// </summary>
    public string Map { get; set; } = "colorMap";

    /// <summary>
    /// The icon is on the material: it is put there once.
    /// </summary>
    private bool Shown { get; set; }

    public void Update(in Frame frame)
    {
        if (Shown)
            return;

        Shown = true;
        Handle<Texture> icon = assets.Load(assets.Default<Icon>()) is { } own ? own.Image : Handle<Texture>.None;
        if (!icon.IsValid || !ecs.TryGet(entity, out Renderer renderer) || assets.Load(renderer.Materials[0]) is not { } material)
            return;

        material.Params.TryGetValue(Map, out Param param);
        param.Texture = icon;
        material.Params[Map] = param;
    }
}
