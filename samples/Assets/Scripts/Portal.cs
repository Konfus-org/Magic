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
/// A way to another sample: when the player's camera comes within <see cref="Radius"/> of its entity,
/// <see cref="Destination"/> opens in place of the world, the samples' loading domain showing until it is there, and
/// the player is put at its spawn. Its <see cref="Label"/> stands over it as a few words of debug text. A prefab
/// attaches it as <c>{ "id": 3430, "destination": { "id": 3406 }, "label": "Audio" }</c>.
/// </summary>
internal sealed class Portal(Entity owner, World world, IDebugUI? debugUI = null) : IBehavior
{
    /// <summary>
    /// Whether it has set the opening off, which it does once: the world it is in closes as the other arrives.
    /// </summary>
    private bool _isOpening;

    public Handle<Domain> Destination { get; set; }

    /// <summary>
    /// The few words over it, naming where it goes.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Metres from its entity within which the player goes through.
    /// </summary>
    public float Radius { get; set; } = 2;

    /// <summary>
    /// Metres above its entity the label stands.
    /// </summary>
    public float LabelHeight { get; set; } = 3;

    public void Update(in Frame frame)
    {
        Vector3 at = world.Get<Transform>(owner).Position;
        if (debugUI is { Enabled: true } && Label.Length > 0) debugUI.Text(at + (Vector3.UnitY * LabelHeight), Label);

        if (_isOpening || world.IsLoading || !PlayerCamera.TryGetPosition(world, out Vector3 player)) return;
        if (Vector3.DistanceSquared(player, at) > Radius * Radius) return;

        _isOpening = true;
        Going.ToAsync(world, Destination, DomainOpenMode.Replace).FireAndForget();
    }
}
