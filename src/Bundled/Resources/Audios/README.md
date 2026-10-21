# Audios

A `.wav`, `.ogg`, `.mp3` or `.flac` file is an `Audio`, which the audio loader gem decodes whole as it loads: every
sample as a float, at the rate and with the channels the file was saved with. Nothing is resampled on load; whatever
plays a sound converts it to its device as it plays, so a file keeps its own quality and a project saves its sounds
at whatever rate suits them.

## The `.meta`

An audio file's `.meta` holds its id alone: how it plays is the business of the `Sound` that names it, never the
file's.

## Choosing a format

A sound is held decoded for as long as it is loaded, so its format decides only its size on disk and how long it
takes to load: WAV and FLAC lose nothing, OGG and MP3 are a fraction of the size. Music and long ambiences are best as
OGG. A sound played in the world is heard from one point and played mono, its channels mixed down, so it is best saved
mono; music and ambience played everywhere keep their stereo.

## The default

The default is a short plucked string, made by the engine rather than recorded: a sharp, broad attack is what lets the
ear tell where a sound comes from, which a pure tone hides.
