# Icons

An `Icon` is the application's icon: a `.icon` file naming the [texture](../Textures/README.md) it shows.

```json
{ "image": { "id": 7436761896624509111 } }
```

That is `Default.icon` (id 730), the default icon: the engine's mage, `Textures/Mage.svg`, whose `.meta` rasterises the
SVG at 256 pixels without mips.

- The window shows the default icon's image (SdlPlatform asks for it).
- The loading domain shows the same image: `Materials/LoadingIcon.mat` samples the mage, and the `LoadingIcon`
  script swaps in the default icon's image, so a project's own shows there too.

To brand a project, put its image anywhere under `Assets/` and write `Assets/Icons/Default.icon` naming it: the
project's file comes before the engine's. Nothing in the engine's gems needs to change.
