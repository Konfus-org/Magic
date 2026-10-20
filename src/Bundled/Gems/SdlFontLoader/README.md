# SdlFontLoader

Fonts to glyph atlases, through SDL_ttf, called through its generated API ([Gems](../README.md#native-libraries)):
provides `IAssetLoader<Font>`. What a font's `.meta` holds is in [Fonts](../../Resources/Fonts/README.md). SDL_ttf and
SDL come from the SDL bindings project, so SDL_ttf binds to the one SDL every gem shares
([Sharing a library](../README.md#sharing-a-library)).

## Behaviour

- SDL_ttf starts in the gem's constructor and stops in `Dispose`, once a file loading on a worker is done; a file asked
  for after that fails.
- Every character of printable ASCII and Latin-1 the font has is drawn at the `.meta`'s size, converted to RGBA8, and
  shelf-packed left to right in rows, a little apart so linear sampling never bleeds a neighbour in, into the smallest
  atlas, a power of two on each side, that holds them all.
- SDL_ttf shares state between its fonts, so files load one at a time, on whichever worker asks.
