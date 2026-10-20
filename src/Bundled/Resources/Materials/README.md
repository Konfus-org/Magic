# Materials

What a surface looks like: a `.mat` file names a surface shader and gives values to the parameters it declares. It
says which surface shader, a `.surf.hlsl`, the material is drawn with; whether it is opaque, masked (pixels below the
surface's alpha cutoff are discarded) or transparent (blended, drawn after everything opaque); whether its back faces
are drawn too; and its params, values by the names the surface's parameter block declares, where one left out takes
the shader's default. The keys and their defaults are the `Material` type's properties, documented on it, and the
files beside this README are the examples.

A param's JSON form follows the type the surface declares it as: a scalar (a float, an integer or a bool, where
non-zero is true) and a vector are objects with a key per component, a colour is linear with a key per channel, and a
texture reference names either a texture or a render texture, which a camera draws into, by its handle. What a
material can set is its surface's parameter declaration, so the surface is where to look.

## Writing a surface

A surface is written against the contract, [Shaders/Include/Surface.hlsli](../Shaders/Include/Surface.hlsli), and
nothing else: no bindings, no frame block, so any renderer can draw it. It says what it is made of and never lights
itself: the renderer lights it, its reflections included ([Shaders](../Shaders/README.md#lighting-a-pixel)). Read the
contract header, and the engine's surfaces under `Shaders/Surfaces/` as the examples: a surface includes the
contract, declares its parameter block, each member with a default and, where it matters to global illumination, a
role saying which member is the surface's colour, how metal it is and its light, and defines the one function that
turns the inputs and the params into what the surface is made of. The defaults and the roles are read by the renderer
and stripped before compilation.

The contract offers helpers for what every surface does: sampling a texture at the mesh uv, or at any other uv with
derivatives the surface passes itself, and reading a normal map into world space, rebuilding z, so two-channel (BC5)
maps serve as well as three-channel ones, through the vertex frame as MikkTSpace bakers expect.

What a surface hands back is what it is made of, and the contract header documents each member: linear colour and
coverage, a world-space unit normal, perceptual roughness and how metallic, a dielectric's reflectance (where 0 turns
reflection off, as an unlit surface does), baked or authored occlusion, light given off, pre-exposed, so 1 reads as
white whatever the lighting, the alpha cutoff a masked material discards below, and which shading model lights it
with what that model takes ([Shaders](../Shaders/README.md#lighting-a-pixel)). The inputs give it the unit direction
from the surface toward the camera, among the rest.

A transparent surface is compiled as a forward surface and may read what was drawn behind it, pre-exposed and blurred
by roughness, through the contract's helpers for that, as the engine's glass surface does: it emits what it lets
through, and the renderer adds its reflection at the reflectance its index of refraction gives.

## What must agree

- The shader is a `.surf.hlsl`, not a pass shader.
- Each params key is a member of the surface's parameter block, with the form its type takes.
- A texture reference names a texture or a `.rtex`, under the key for each.
- A masked material needs a surface that has an alpha cutoff.

## Adding a material

Write `Name.mat` anywhere under the project's `Assets/`, or in this folder for the engine, and name it from an
entity's `Renderer`, in the slot its model's mesh names ([Models](../Models/README.md)).
