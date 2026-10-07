# Credits

Every model and texture here that the engine did not make itself came from these packs. All are CC0 (public
domain, https://creativecommons.org/publicdomain/zero/1.0/): no attribution is required, but it is given.

| Files | Pack | Author | Where |
|---|---|---|---|
| `Shared/Models/Village/*.glb` (but `Paving.glb`), `Shared/Textures/Village/*.png` | Medieval Village MegaKit (Standard) | Quaternius | https://quaternius.com/packs/medievalvillagemegakit.html |
| `Shared/Models/Interior/CouchMedium*.glb`, `LCouch`, `Rug`, `RoundRug`, `TableLamp`, `LightFloor`, `TableRoundSmall`, `Houseplant`, `ShelfSmall`, `NightStand` | Ultimate House Interior Pack | Quaternius | https://quaternius.com/packs/ultimatehomeinterior.html (GLBs via https://poly.pizza) |
| `Shared/Models/Interior/Television.glb`, `TelevisionVintage.glb`, `CabinetTelevision.glb` | Furniture Kit | Kenney | https://kenney.nl/assets/furniture-kit (GLBs via https://poly.pizza) |

What was changed: the kit's `.gltf` + `.bin` files were packed into single `.glb` files without their texture
references; its 2048 textures were resized to 1024, and its roughness maps written as ORM maps (occlusion 1,
roughness, metallic 0, or 1 for the metal ornaments). `Paving.glb` was made for the samples: a 16 m square whose uvs
repeat the kit's floor brick every 2 m. Materials (`Materials/Village`, `Materials/Interior`) are the samples' own,
from the packs' material names and colours.
