# SdlBindings

SDL3's C API and those of SDL_image and SDL_ttf, generated from the headers SDL releases
([Gems](../README.md#native-libraries)), for the gems built on SDL: the window and input, the GPU, images and fonts.
No project lists it; a gem references it, and so carries the API and SDL beside itself.

## Natives

SDL and its two libraries are built here from SDL's repositories at the pinned releases, SDL first and the two against
it, once per commit. SDL_image reads its formats itself; SDL_ttf brings FreeType and HarfBuzz with it. The SDL that
runs is one copy for every gem, shared by name (`GemLoadContext`): the first gem to bind it loads it for all.
