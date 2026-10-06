# Icons

`Mage.svg` is the engine's window icon, and the picture on the loading screen. It is a [texture](../Textures/README.md)
like any other; its `.meta` rasterises the SVG at 256 pixels without mips:

```json
{ "id": 7436761896624509111, "version": 1, "size": 256, "mipmaps": false }
```

- The window takes the project's icon: `"icon": { "id": N }` in the `.magic` file, any texture. Without one it is
  `Icons/Mage.svg`.
- The loading domain shows the same icon: `Materials/LoadingIcon.mat` samples it, and the `LoadingIcon` script
  swaps in the project's icon when the project has one.

To brand a project, put its icon anywhere under `Assets/` and name it in the `.magic` file. Nothing in `Resources/`
needs to change.
