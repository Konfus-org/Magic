# SteamAudio

Plays sounds with [Steam Audio](https://github.com/ValveSoftware/steam-audio), Valve's spatial audio library, on the
machine's audio device through SDL. It provides nothing to other gems.

An entity with a `Transform` and a `Sound` component is a sound. Scripts play, pause, stop and seek it by writing the
component ([Audio](../../../Core/README.md#audio)). Steam Audio is a native library, used as Valve builds it for each
release ([Native libraries](../README.md#native-libraries)). SDL comes from the SDL bindings project.

- **Listener.** Sounds are heard from the camera tagged `player`, else the camera drawing into the window.
- **Sounds in the world** get quieter and duller with distance, come from their side of the listener's head, and are
  muffled by solid things in the way. The nearest few also echo off what is around them.
- **Sounds everywhere**, such as music, sound the same wherever the listener is.
- **Solids.** Static and kinematic bodies near the player block and echo sound, shaped as their models. An
  `Acoustics` component sets what a solid is made of.

Its settings, such as the volume and how many sounds play at once, are on `SteamAudioSettings` and show in the
settings window (F4), under Audio. Some of them restart the engine when changed.

- Only the nearest sounds are heard, up to the settings' limit. The rest keep playing silently.
- A sound in the world isn't heard past its range.
- Dynamic bodies, which fall and get pushed, don't block or echo sound.
- Headless, or with no audio device, nothing is heard. Sounds still play on silently.
