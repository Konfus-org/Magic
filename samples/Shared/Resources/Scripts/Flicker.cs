using Magic.CSharpScripting;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Projects;
using Magic.Worlds;

namespace Magic.SamplesShared.Resources.Scripts;

/// <summary>
/// Makes the point light of its entity flicker like a flame: its intensity goes up and down around the intensity
/// that it starts with.
/// </summary>
public sealed class Flicker(Entity owner, World world) : IBehavior
{
    private float _baseIntensity;

    /// <summary>
    /// How far the intensity goes from where it started, as a share of it. A prefab sets it beside the handle of the
    /// script.
    /// </summary>
    public float Depth { get; set; } = 0.35f;

    public void Start() => _baseIntensity = world.Get<PointLight>(owner).Intensity;

    public void Update(in Frame frame)
    {
        // Three sines at rates that do not line up, so the flicker never repeats in a way that shows.
        float t = (float)frame.SimSec;
        float wave = (MathF.Sin(t * 7.1f) + MathF.Sin(t * 13.3f) + MathF.Sin(t * 23.7f)) / 3;
        world.Mutate<PointLight>(owner).Intensity = _baseIntensity * (1 + (Depth * wave));
    }
}
