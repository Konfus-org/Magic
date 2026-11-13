# Shaders

The shader library is the Slang code that each renderer and each game shader imports. It is in `src/Resources/Shaders/`.
The rules for shader code are in [Shader Standards](SHADER_STANDARDS.md).

## Modules

A module is one file, and its path is its name. `Core/Math.slang` starts with `module "Core/Math";`. Another module
imports it with `import Core.Math;`.

The shader compiler loads a module and its imports from the assets. A module in the `Shaders/` folder of a game adds
to the library.

- `Core/` has what each module needs: the heap that each resource is read through (`Heap`), the data of a pass
  (`Pass`), the data that each pass of a frame reads (`Frame`), math, colour, packing, sampling and hashing.
- `Surface/` has the data that a material gets for a pixel, and the data that it gives back.
- `Material/` has the material contract, `IMaterial`, and the materials of each project: `Standard`, and `Glass`,
  which is translucent.
- `Sky/` has the sky contract, `ISky`, and `OpenSky`, the sky of a frame that has no sky tables.
- `Post/` has the pixel effect contract, `IPixelEffect`, and the post effects of the engine.

The tables and techniques of a renderer are in the `Resources/Shaders/` folder of its gem ([Rendering](RENDERING.md)).

## Contracts

A contract between shaders is an interface, named `I` and a noun. A pass that works with each implementation is
generic over the interface.

To add a material, a sky or a post effect to a game:

1. Implement the contract in a module of the game.
2. Name the module in the asset that uses it.

The step of a sky gem hands on its type with its tables. A pass that is generic over `ISky` uses that type where there
are tables, and `OpenSky` where there are none. It has the two built before a domain shows.

## Post effects

A post effect implements `IPixelEffect`. The effects run in the order of their list.

- An effect before `Exposure` works in the light of the world. An effect after it works in exposed light. An effect
  after the tonemap works in the colours of the screen.
- An effect that needs a target of its own declares levels. Each level can read the level before it. `Blur` and
  `Bloom` show this.
- An effect that reads the picture of the last frame declares a history. `Exposure` shows this.
- An effect that blends frames declares jitter. `TemporalAntiAliasing` shows this.
- An effect reads the frame and its lights itself.

Code from another engine keeps its licence, in `src/Resources/Shaders/THIRD-PARTY-NOTICES.md`.
