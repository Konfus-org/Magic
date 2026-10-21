# SteamAudio

Sound through Steam Audio, Valve's spatial audio library, as Valve builds it for each release
([Gems](../README.md#native-libraries)), played on the machine's audio device through SDL, whose API the SDL bindings
project carries. It takes the world, to read sounds and solids, the assets, to load what sounds play and what solids
are shaped like, and the threads, for the thread it simulates on. It provides nothing to other gems: a sound is
driven entirely through Core's `Sound` component, and what is solid through its `Physics` and `Acoustics`
([Core's README](../../../Core/README.md#audio)), so this README says what this gem makes of them.

## Hearing a sound

Every entity with a `Transform` and a `Sound` is a voice. Once per frame, in `LateUpdate`, after scripts and physics
have moved things, each voice is compared with the copy of its sound kept from the last frame, as the physics gem
compares a body with its components:

- **What a script writes is carried over.** Playing, pausing or stopping it, writing its time, giving it another
  audio or moving it between the world and everywhere. A voice writes back how far in it is, and that a sound that
  does not loop has ended, so a script reads where it is and knows when it is done.
- **The voice waits for its audio**, which loads on a worker as every asset does, and plays from where its time says
  once it has. An audio that does not load is an error, and the sound is silent.
- **The nearest are heard.** A sound in the world past its range is not heard, and a sound everywhere always is; of
  the rest, as many as the settings allow are heard, those everywhere first, then the nearest. A sound not heard goes
  on in silence, its time running at its pitch, a sound that does not loop still ending, so it plays on from the right
  place when it is heard again, and a script sees nothing different. Nothing is dropped for being too many, and nothing
  is logged: a crowd is heard nearest first.
- **Headless, or with no audio device,** nothing is heard, and every sound goes on in silence the same way.

A heard sound has a slot of the device's: an SDL stream that converts its audio, at the file's own rate and channels,
to the device's rate, mono for a sound in the world and stereo for one everywhere, and plays it faster or slower for
its pitch. A sound everywhere is mixed in as it is. A sound in the world goes through Steam Audio:

- **Direct.** Each frame the main thread says where the listener is, the camera tagged `player`, else the camera
  drawing into the window, else the first, and where each sound is and which way it faces, and Steam Audio works out
  what reaches the listener straight from it: quieter with distance past where it is heard in full, duller as the
  air takes the high notes over distance, quieter off its front as much as its focus says, and muffled by what is solid
  between them, through which some passes as the material lets it, unless it is unoccluded. That sound is then placed
  around the listener's head, from the side it is on, through the head Steam Audio ships with.
- **Echoes.** The nearest few sounds in the world that are not dry echo. Rays are traced from the listener through the
  solids, bouncing as the settings allow, a few times a second, and Steam Audio turns what comes
  back into each sound's echo, played through its own effect into one mixer, heard around the head once a frame.
  Which sounds echo changes as they move; an echo given to another sound starts clean.
- **Fading out.** Steam Audio's fall-off never reaches silence, so a sound fades out over the far end of its range
  and is not heard past it.

## What is solid

Every entity with a `Transform` and a `Physics` that stays put or is moved by code, within the settings' range of the
player's cameras, is in the acoustic scene, or every one when there is no player: shaped as its model's surface at its
transform, rather than the simple shape its body collides as, so a sound is muffled by a tower's walls rather than its
bounding box. A body that falls and is pushed is left out: it moves every step, and each move placed in Steam Audio's
scene costs far more than what a crate or a ball does to what is heard. Each model is fitted into triangles once, on a worker; each model and material is one sub-scene,
shared by every entity alike; and each entity is placed as an instance of it, moved as its transform moves. An entity
with no model is a unit cube, as its body is. `Acoustics` says what the solid is made of, one of Steam Audio's measured
materials, from carpet that soaks up most of what reaches it to glass that reflects it and lets a little through;
without one it is generic.

As with physics, what is near is told from the transform alone for the many far-off solids, against the farthest any
model reaches at the entity's scale, so a world of tens of thousands of solids costs a few operations each, and only
what could be near is looked up. A solid joins the scene within the range and leaves it a little past it, so one at
the edge is not added and taken out every other frame. What stays put needs no look every frame, so the solids are
gathered a few times a second, which is most of what the gem costs the main thread. Placing a solid in Steam Audio's
scene costs about a millisecond, so a domain's thousands come in over a moment, nearest first, rather than holding up
the direct sound.

The audio files and models a domain's sounds and solids name are loaded and fitted before it spawns (Core's
`IPreloader`), so its sounds are heard, muffled and echoed, from its first frame. A domain that replaces the world is
then readied out of sight: its sounds are held, none heard and none's time running, until the loading is told over,
the frame after the world is first shown, while the solids near the listener are placed with no limit on how long an
update takes, as no sound waits on it then; it is ready once every one is in the scene.

## Threads

Three threads touch sound, and none waits on another while the run goes on:

- **The main thread** reads the world: it follows the components, tells the audio thread what starts, seeks and stops
  through a channel, writes what changes continuously, each sound's volume, pitch and place, to its slot whole, and
  hands the simulation thread a frame of where the listener and each sound are and what is solid near the player,
  whole, swapped between three so neither ever waits for the other. It calls nothing of Steam Audio's simulation.
- **The simulation thread**, the gem's own, has Steam Audio's scene and simulator to itself, as Steam Audio asks that
  changing the scene and simulating never meet: woken by each frame handed over, it takes the newest, brings the scene
  up to date, works out every slot's direct sound, and traces the echoes when they are due.
- **SDL's audio thread** fills the device a frame at a time, taking in the main thread's commands first, and says back
  how far in each slot is and when it has let one go. A slot's stream and the samples it reads are freed only once
  the audio thread has let it go, so nothing it reads is ever freed under it; everything else it uses is made as the
  gem starts and kept for the run, so filling the device allocates nothing.

As the run ends, Stop closes the device, which waits for the audio thread, and ends the simulation thread, waiting for
what it is working out; Dispose then releases everything of Steam Audio and SDL's audio.

## Settings

Each is documented on `SteamAudioSettings` and shown in the settings window, under Audio. The volume, whether echoes
are heard, how far rays bounce and how often echoes are traced are followed as the run goes on. What sizes what is
made as the gem starts, the device's rate and frame, how many sounds are heard and echo, how many rays a trace casts,
and how long and how finely an echo is kept, restarts the engine when changed. The range trades how far from the player
solids are heard against the cost of keeping them in the scene, as physics' range trades its step.

## What must hold

- Steam Audio's space is right-handed and the engine's left-handed, so the gem hands it the world mirrored along Z,
  the listener, the sounds and every solid alike, with each triangle's winding turned to match: in a mirror image
  turned the same way, what is to the left stays to the left.
- The audio thread must never wait or throw: what it reads that the main thread writes, it reads whole enough for sound,
  where a frame read halfway hears no difference, and what goes wrong on it is logged once and heard as silence.
- A frame of sound must take far less than the time it plays, in a Debug build too, or the device hears gaps, which
  the gem counts as late fills on its meter beside how long each fill, each simulation and each trace took. Steam Audio's own work is
  Valve's release build whatever the engine's configuration; the gem's is a few loops over a frame's samples.
- Steam Audio is pinned to a release in the csproj, by the SHA-256 of Valve's archive: moving it means the new
  archive's digest, generating the bindings again and reading every name the gem uses against them.
