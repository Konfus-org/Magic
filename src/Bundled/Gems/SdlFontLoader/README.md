# SdlFontLoader

Loads fonts with SDL_ttf. Provides `IAssetLoader<Font>`.

A font's `.meta` sets the pixel size its characters are drawn at ([Fonts](../../Resources/Fonts/README.md)). The
loader draws them into one atlas texture. SDL_ttf comes from the SDL bindings project.

- Only printable ASCII and Latin-1 characters are drawn.
