# Icons

An `Icon` is the application's icon: a `.icon` file naming the [texture](../Textures/README.md) it shows, by its
handle. The keys are the `Icon` type's properties, documented on it, and the icon beside this README is the example.

The window shows the icon its window service names, the default icon until a gem sets another; the platform gem loads
it and hands its texture's image to the OS. The default icon is the engine's; to brand a project, put its image
anywhere under `Assets/`, write an `.icon` naming it, and set it as the window's icon.
