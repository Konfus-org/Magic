using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Behaviors;

/// <summary>
/// Shows the project's icon on its entity: the texture <see cref="Map"/> of the entity's first material becomes
/// <see cref="Project.Icon"/>, before the renderer first draws with it. The material is one asset, so every entity
/// drawn with it shows the icon. For a loading domain.
/// </summary>
internal sealed class LoadingIcon : IBehavior
{
    private readonly Handle _entity;
    private readonly IEcs _ecs;
    private readonly Project _project;
    private readonly Assets _assets;
    private bool _shown;

    public LoadingIcon(Handle entity, IEcs ecs, Project project, Assets assets)
    {
        _entity = entity;
        _ecs = ecs;
        _project = project;
        _assets = assets;
    }

    /// <summary>
    /// The texture parameter of the material's surface that takes the icon.
    /// </summary>
    public string Map { get; set; } = "colorMap";

    public void Update(in Frame frame)
    {
        if (_shown)
            return;

        _shown = true;
        if (!_project.Icon.IsValid || !_ecs.TryGet<Renderer>(_entity, out Renderer renderer) || _assets.Load(renderer.Materials[0]) is not { } material)
            return;

        material.Params.TryGetValue(Map, out Param param);
        param.Texture = _project.Icon;
        material.Params[Map] = param;
    }
}
