using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Shows <see cref="Hit"/> on its entity for <see cref="Seconds"/> after something strikes its body, then its own
/// material again: the way to change one entity's look, putting another material in its renderer's slot, since a
/// material is shared by everything drawn with it. The entity carries a <see cref="Contacts"/> beside its
/// <see cref="Physics"/>. <c>{ "id": 3444, "hit": { "id": 3460 } }</c>.
/// </summary>
internal sealed class Paint(Entity owner, World world) : IBehavior
{
    /// <summary>
    /// The material it spawned with, which it goes back to.
    /// </summary>
    private Handle<Material> _own;

    /// <summary>
    /// The frame clock's seconds when it goes back.
    /// </summary>
    private double _until;

    public Handle<Material> Hit { get; set; }

    public float Seconds { get; set; } = 1.5f;

    public void Start() => _own = world.Get<Renderer>(owner).Materials[0];

    public void Update(in Frame frame)
    {
        foreach (Contact contact in world.Get<Contacts>(owner))
        {
            if (contact.Phase == ContactPhase.Enter) _until = frame.Seconds + Seconds;
        }

        Handle<Material> showing = frame.Seconds < _until ? Hit : _own;
        ref Renderer renderer = ref world.Get<Renderer>(owner);
        if (renderer.Materials[0] != showing) renderer.Materials[0] = showing;
    }
}
