using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Pauses the world on a key, <see cref="Key"/>, and lets it go on on the next press, playing its entity's sound while
/// it is paused: pause music. Its entity is tagged <see cref="Tag.Unpausable"/>, so it runs and its sound plays while
/// the world is paused, while every other script and sound waits. <c>{ "id": 3453, "key": "key_p" }</c>.
/// </summary>
internal sealed class Pause(Entity owner, World world, IInput input, IDebugUI? debugUI = null) : IBehavior
{
    public Control Key { get; set; } = Control.KeyP;

    public void Update(in Frame frame)
    {
        if (input[Key].WasPressed) world.IsPaused = !world.IsPaused;

        // The sound plays from where it was while the world is paused, and holds there while it is not.
        ref Sound sound = ref world.Get<Sound>(owner);
        sound.Playback = world.IsPaused ? Playback.Playing : Playback.Paused;

        if (world.IsPaused && debugUI is { IsEnabled: true }) debugUI.Text("Paused (P goes on)");
    }
}
