# SdlBindings

SDL3's C API and those of SDL_image, SDL_ttf and SDL_mixer, generated from the headers SDL releases
([Gems](../README.md#native-libraries)), for the gems built on SDL: the window and input, the GPU, images, fonts and
audio.
No project lists it; a gem references it, and so carries the API and SDL beside itself, and the one SDL every such gem
runs on is loaded as [Sharing a library](../README.md#sharing-a-library) says.

SDL and its three libraries are built from SDL's repositories at the releases the project pins
([Building a library](../README.md#building-a-library)), SDL first and the three against it. SDL_image reads its formats
itself; SDL_ttf brings FreeType and HarfBuzz with it; SDL_mixer decodes WAV itself and Ogg Vorbis, MP3 and FLAC with the
single-file decoders in its own source, so it needs no other library.
