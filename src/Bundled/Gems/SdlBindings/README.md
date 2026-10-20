# SdlBindings

SDL3's C API and those of SDL_image and SDL_ttf, generated from the headers SDL releases
([Gems](../README.md#native-libraries)), for the gems built on SDL: the window and input, the GPU, images and fonts.
No project lists it; a gem references it, and so carries the API and SDL beside itself, and the one SDL every such gem
runs on is loaded as [Sharing a library](../README.md#sharing-a-library) says.

SDL and its two libraries are built from SDL's repositories at the pinned releases
([Natives](../../../../natives/README.md)), SDL first and the two against it. SDL_image reads its formats itself;
SDL_ttf brings FreeType and HarfBuzz with it.
