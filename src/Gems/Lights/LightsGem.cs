using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Assets.Types;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Gems;
using Magic.Pipelines;

namespace Lights;

/// <summary>
/// Fills the room of the sample's Lights domain as it loads: a thousand small coloured point lights spread through
/// it, every one the domain's, so they go when it does. The sample's proof that lights cost by what each pixel sees.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Magic makes it by reflection when a project lists the gem."
)]
internal sealed class LightsGem(IEcs ecs, AssetDatabase assets) : IGem, IPipeline<WorldLoad>
{
    private const string DomainPath = "Domains/Lights/Lights.domain";
    private const int LightCount = 1000;
    private const float RoomHalf = 9.5f;
    private const float RoomHeight = 5.5f;

    public ValueTask<WorldLoad> RunAsync(WorldLoad state, CancellationToken cancel)
    {
        Handle<Domain> lights = assets.Find<Domain>(DomainPath);
        if (lights == Handle<Domain>.None || state.Content.Domain != lights)
            return ValueTask.FromResult(state);

        Transform[] transforms = new Transform[LightCount];
        PointLight[] points = new PointLight[LightCount];
        for (int i = 0; i < LightCount; i++)
        {
            uint light = (uint)i;
            transforms[i] = new Transform
            {
                Position = new Vector3(
                    Scatter(light, 0) * RoomHalf,
                    0.3f + (((Scatter(light, 1) * 0.5f) + 0.5f) * RoomHeight),
                    Scatter(light, 2) * RoomHalf
                ),
            };
            points[i] = new PointLight
            {
                Color = new Color(
                    0.5f + (0.5f * Scatter(light, 3)),
                    0.5f + (0.5f * Scatter(light, 4)),
                    0.5f + (0.5f * Scatter(light, 5))
                ),
                Intensity = 20,
                Range = 3,
                Radius = 0.05f,
            };
        }

        Entity[] created = new Entity[LightCount];
        ecs.Create<Transform, PointLight>(transforms, points, created);
        Origin origin = new() { Domain = lights, Content = state.Content.Id };
        foreach (Entity entity in created)
        {
            ecs.Set(entity, in origin);
        }

        Log.Info($"The room is lit: {LightCount} lights.");

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// A number in [-1, 1] that is always the same for a light and a purpose, from a hash of both.
    /// </summary>
    private static float Scatter(uint light, uint purpose)
    {
        uint hash = unchecked((light * 0x9E3779B1u) ^ (purpose * 0x85EBCA77u));
        hash ^= hash >> 15;
        hash = unchecked(hash * 0x2C1B3C6Du);
        hash ^= hash >> 12;

        return ((hash & 0xFFFFFFu) / 8388607.5f) - 1;
    }
}
