# GemName

What GemName does for a project, and what it needs to: the services and other gems' interfaces its constructor takes,
the settings it reads, and the assets in its `Resources/` folder.

A project runs it by listing `GemName` among the gems in its `.magic` file. Building it puts it in the `Gems/` folder
of the Magic that `MagicDir` names, where Magic loads it from as it starts.
