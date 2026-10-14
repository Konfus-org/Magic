# SdlFontLoader

Fonts to glyph atlases, through SDL_ttf ([ppy.SDL3_ttf-CS](https://github.com/ppy/SDL3-CS)): provides
`IAssetLoader<Font>`. What a font's `.meta` holds is in [Fonts](../../Resources/Fonts/README.md).

## Behaviour

- SDL_ttf starts in the gem's constructor and stops in `Dispose`, once a file loading on a worker is done; a file asked
  for after that fails.
- Every character of printable ASCII and Latin-1 the font has is drawn at the `.meta`'s size, converted to RGBA8, and
  shelf-packed left to right in rows, a pixel apart so linear sampling never bleeds a neighbour in, into the smallest
  atlas, a power of two on each side from 256, that holds them all.
- SDL_ttf shares state between its fonts, so files load one at a time, on whichever worker asks.

## Natives

ppy.SDL3_ttf-CS carries SDL_ttf, and the SDL it is built on, for every platform it supports, and the gem's load
context picks the machine's. SDL is called before SDL_ttf starts, so SDL_ttf binds to the SDL every gem shares
(`GemLoadContext`).
