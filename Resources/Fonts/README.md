# Fonts

`.otf` and `.ttf` files are `Font` assets, rasterised by the SDLFonts gem into one white atlas (alpha is coverage)
with a glyph per character, Latin-1 (codepoints 32–126 and 160–255).

| File | Id | Used by |
| --- | --- | --- |
| `MontserratMedium.otf` | 600 | the debug UI (ImGuiOverlay), found by its path |
| `Montserrat-OFL.txt` | random | nothing: the font's licence, kept beside it |

## The `.meta`

```json
{ "id": 600, "version": 1, "size": 13 }
```

`size` is the pixel height the font is rasterised at (default 32). The atlas is made once at that size, so a
different size is a different `.meta`.

## Lifetime

Loaded once when the debug UI starts and kept for the run; editing it takes a restart. Pooled under the default
budget.

## Adding a font

Drop the `.otf`/`.ttf` beside a `.meta` with the `size` you want, and load it from a gem by path or id
(`assets.Find<Font>("Fonts/Mine.ttf")`). The debug UI always uses `Fonts/MontserratMedium.otf` from Resources (a
project file at the same path does not replace it: Resources wins on a shared path).
