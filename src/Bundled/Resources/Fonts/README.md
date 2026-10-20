# Fonts

A `.ttf` or `.otf` file is a `Font`: the font loader draws its characters once, at the pixel size its `.meta` asks
for, into an atlas whatever draws text uploads once and draws a quad per character from. Which characters, and how the
atlas is laid out, is the loader's ([its README](../../Gems/SdlFontLoader/README.md)).

Beside the id, the sidecar holds the pixel size the characters are drawn at; the key and its default are the font
loader's import-settings type's properties, documented on it, and the sidecars beside the fonts in this folder are the
examples. The atlas is made once per file, so the same font at another size is another copy of the file, with a
`.meta` of its own.

A font is its designer's work, shipped under its licence: a font the engine ships keeps its licence in a text file
beside it, which has no `.meta` and so is no asset.
