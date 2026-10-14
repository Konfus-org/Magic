# Fonts

A `.ttf` or `.otf` file is a `Font`, which the font loader gem draws once, at the size its `.meta` asks for, into one
atlas: white RGBA8 pixels whose alpha is coverage, and where each character is in it, with how far it sits from the pen
and moves it on. The characters are printable ASCII and Latin-1. Whatever draws text uploads the atlas once and draws a
quad per character.

## The `.meta`

```json
{ "id": 600, "size": 32 }
```

| Key | Default | What it does |
| --- | --- | --- |
| `size` | `32` | The pixel size the characters are drawn at. The atlas is made once, so the same font at another size is another file. |

## Licences

A font is its designer's work, shipped under its licence: a font the engine ships keeps its licence in a text file
beside it, which has no `.meta` and so is no asset.
