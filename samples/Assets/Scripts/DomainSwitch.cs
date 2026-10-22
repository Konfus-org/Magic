using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// A pressure plate: each time the player's camera comes within <see cref="Radius"/> of its entity it opens
/// <see cref="Domain"/> beside the world, with no loading screen and the player left where it is, or closes it when
/// it is open, which destroys what it spawned. A domain opened beside the world spawns where its own prefabs say,
/// so the one it opens is laid out to stand beside this one. A prefab attaches it as
/// <c>{ "id": 3433, "domain": { "id": 3404 } }</c>.
/// </summary>
internal sealed class DomainSwitch(Entity owner, World world) : IBehavior
{
    /// <summary>
    /// Whether the player was on the plate last frame, so stepping on switches once, not every frame on it.
    /// </summary>
    private bool _wasOn;

    public Handle<Domain> Domain { get; set; }

    /// <summary>
    /// Metres from its entity within which the player is on the plate.
    /// </summary>
    public float Radius { get; set; } = 1.5f;

    public void Update(in Frame frame)
    {
        bool isOn = PlayerCamera.TryGetPosition(world, out Vector3 player)
            && Vector3.DistanceSquared(player, world.Get<Transform>(owner).Position) < Radius * Radius;
        bool isStepping = isOn && !_wasOn;
        _wasOn = isOn;

        if (!isStepping || world.IsLoading) return;

        if (IsOpen())
        {
            world.Close(Domain);

            return;
        }

        Going.ToAsync(world, Domain, DomainOpenMode.Additive).FireAndForget();
    }

    private bool IsOpen()
    {
        foreach (Handle<Domain> open in world.Opened)
        {
            if (open == Domain) return true;
        }

        return false;
    }
}
