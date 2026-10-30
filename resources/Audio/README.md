# Audios

A `.wav`, `.ogg`, `.mp3` or `.flac` file is an `Audio`: a sound, decoded in full at its own sample rate and channels.
Its `.meta` holds only the id. `Default.wav` is the default sound.

To play one, name it in a `Sound` component on an entity. How it plays, such as its volume, looping, or whether it is
heard in the world or everywhere, is set on the `Sound`, documented on that type.

- A sound heard in the world plays in mono. Save those sounds as mono files.
- Music and ambience heard everywhere keep their stereo.
- WAV and FLAC lose nothing. OGG and MP3 are much smaller; use OGG for music and long ambience.
