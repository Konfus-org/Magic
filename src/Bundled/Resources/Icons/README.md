# Icons

An `Icon` is the application's icon: a `.icon` file naming the [texture](../Textures/README.md) it shows.

```json
{ "image": { "id": 7436761896624509111 } }
```

The window shows the icon `IWindow.Icon` names, the default icon until a gem sets another, from its texture's first
mip level: the platform gem loads both and hands the image to the OS. To brand a project, put its image anywhere under
`Assets/` and write `Assets/Icons/Default.icon` naming it: the project's file comes before the engine's.
