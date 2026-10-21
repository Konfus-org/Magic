using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Rings its entity's sound from the start each time something strikes its body, harder than
/// <see cref="Threshold"/>: a bell a thrown ball rings. Collisions reach a script as data on its entity, its
/// <see cref="Contacts"/>, read in Update, so the entity carries one beside its <see cref="Physics"/>; the sound does not
/// loop and is louder the harder the strike. <c>{ "id": 3436, "threshold": 0.5 }</c>.
/// </summary>
internal sealed class Strike(Entity owner, World world) : IBehavior
{
    /// <summary>
    /// The impulse, in newton-seconds, a touch must carry to ring it; a ball rolling against it does not.
    /// </summary>
    public float Threshold { get; set; } = 0.5f;

    /// <summary>
    /// The impulse that rings it at full volume.
    /// </summary>
    public float Loudest { get; set; } = 8;

    public void Update(in Frame frame)
    {
        float hardest = 0;
        foreach (Contact contact in world.Get<Contacts>(owner))
        {
            if (contact.Phase == ContactPhase.Enter) hardest = MathF.Max(hardest, contact.Impulse);
        }

        if (hardest < Threshold) return;

        ref Sound sound = ref world.Get<Sound>(owner);
        sound.Time = 0;
        sound.Volume = Math.Clamp(hardest / Loudest, 0.2f, 1);
        sound.Playback = Playback.Playing;
    }
}
