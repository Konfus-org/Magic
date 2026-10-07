# Domains (LoadingScreen)

The loading screen is a domain: what is shown over the world while a domain opened in its place fills. The format of
a domain and its chunks is in [Core's Domains README](../../../../../Core/Resources/Domains/README.md).

```
Domains/Loading/
    Loading.domain    { "chunkSize": 64 }                                    (id 700)
    globals.chunk     camera, backdrop, the icon, the bar and its back      (id 701)
```

The bar is an entity whose script grows it as the world loads:

```json
{
    "name": "BarFill",
    "scripts": [ { "id": 720, "width": 0.6 } ],
    "components": {
        "Transform": { "position": { "x": -0.6, "y": -0.45, "z": -0.02 }, "scale": { "x": 0.001, "y": 0.03, "z": 0.005 } },
        "Renderer": { "model": { "id": 520 }, "materials": { "slot0": { "id": 713 } } }
    }
}
```

`LoadingBar` (720) scales its entity along X by how far the world is; on `Models/Bar.fbx` (520, Core's), whose origin
is its left edge, that is a bar filling from left to right. `LoadingIcon` (721) puts the default icon's image on the
icon's material. Both are in [Scripts](../Scripts/README.md).

## Which loading domain

The `LoadingScreen.Domain` setting (`--loading`), else the project's `"loading"`, else this one,
`Domains/Loading/Loading.domain`. A project's own loading screen is a domain like any other, named as `"loading"`.
