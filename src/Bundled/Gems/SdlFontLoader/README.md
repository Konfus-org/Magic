# SdlFontLoader

Fonts to glyph atlases, through SDL_ttf, called through its generated API ([Gems](../README.md#native-libraries)), which the SDL bindings gem provides:
provides
`IAssetLoader<Font>`. What a font's `.meta` holds is in [Fonts](../../Resources/Fonts/README.md).

## Behaviour

- SDL_ttf starts in the gem's constructor and stops in `Dispose`, once a file loading on a worker is done; a file asked
  for after that fails.
- Every character of printable ASCII and Latin-1 the font has is drawn at the `.meta`'s size, converted to RGBA8, and
  shelf-packed left to right in rows, a pixel apart so linear sampling never bleeds a neighbour in, into the smallest
  atlas, a power of two on each side from 256, that holds them all.
- SDL_ttf shares state between its fonts, so files load one at a time, on whichever worker asks.

## Natives

The SDL bindings gem carries SDL_ttf and SDL for this machine, which the gem's load context loads. SDL is called before SDL_ttf starts, so SDL_ttf binds to the SDL every gem shares
(`GemLoadContext`).
