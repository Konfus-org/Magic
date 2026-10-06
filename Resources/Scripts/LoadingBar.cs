using Magic.Attributes.Scripts;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Domain;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Behaviors;

/// <summary>
/// Makes its entity as wide as the world is loaded: <see cref="Width"/> metres once every domain that is loading is
/// there, a share of it until then (the least far of them counts). For a loading domain: on an entity whose model has
/// its origin on its left edge, a bar that fills from left to right.
/// </summary>
[AlwaysUpdate]
[Impatient]
internal sealed class LoadingBar(Handle entity, IEcs ecs, World world) : IBehavior
{
    /// <summary>
    /// The least it is, as a share of <see cref="Width"/>: a scale of nothing is no shape at all.
    /// </summary>
    private const float Least = 0.001f;

    /// <summary>
    /// The entity's scale along X when everything is loaded; the chunk sets it beside the script's id.
    /// </summary>
    public float Width { get; set; } = 1f;

    public void Update(in Frame frame)
    {
        float progress = 1f;
        foreach ((Handle<Domain> domain, DomainState state, float told) in world.Active)
        {
            if (domain != world.Loading && state == DomainState.Loading)
                progress = MathF.Min(progress, told);
        }

        ref Transform transform = ref ecs.Get<Transform>(entity);
        transform.Scale = transform.Scale with { X = Width * MathF.Max(Least, progress) };
    }
}
