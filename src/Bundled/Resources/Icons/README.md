# Icons

An `Icon` is the application's icon: a `.icon` file naming the [texture](../Textures/README.md) it shows.

```json
{ "image": { "id": 7436761896624509111 } }
```

The window shows the icon `IWindow.Icon` names, the default icon until a gem sets another; the platform gem loads it
and hands its texture's image to the OS. The default icon is the engine's; to brand a project, put its image anywhere
under `Assets/`, write an `.icon` naming it, and set `IWindow.Icon` to it.
