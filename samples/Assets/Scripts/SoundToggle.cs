using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Pauses its entity's sound and plays it again on a key, <see cref="Key"/>: a script playing and pausing a sound by
/// writing its <see cref="Sound"/>. <c>{ "id": 3437, "key": "key_m" }</c>.
/// </summary>
internal sealed class SoundToggle(Entity owner, World world, IInput input) : IBehavior
{
    public Control Key { get; set; } = Control.KeyM;

    public void Update(in Frame frame)
    {
        if (!input[Key].WasPressed) return;

        ref Sound sound = ref world.Get<Sound>(owner);
        sound.Playback = sound.Playback == Playback.Playing ? Playback.Paused : Playback.Playing;
    }
}
