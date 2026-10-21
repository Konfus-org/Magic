# Materials

What a surface looks like, or the sky: a `.mat` file names a surface shader, or a sky shader for the world's sky, and
gives values to the parameters it declares. It says which shader, a `.surf.hlsl` or a `.sky.hlsl`, the material is
drawn with; whether it is opaque, masked (pixels below the
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

## Writing a sky

A sky is written against its own contract, [Shaders/Include/Sky.hlsli](../Shaders/Include/Sky.hlsli), as a surface is
against the surface contract: it declares its parameter block as a surface does, and defines two functions: one that
says the sky's light along a direction, from what the renderer hands it, the world's sun, its moon and its weather
among them, and one that says how much of the sun's or the moon's light gets through the sky to a place of the world,
by colour, its shadow. Its light is in nits, not pre-exposed, as it lights the world: the renderer draws it in the view
and captures it for light from everywhere and for reflections, so a sky's colours reach everything it lights. It hands
back the sun's and the moon's discs apart from the rest, as the capture leaves them out, their own light standing for
them; so a sky draws them however it likes, the moon lit from wherever its sun is, and nothing counts either twice.
What it gives off pre-exposed, shown as it is whatever the exposure and never captured, is handed back apart too: the
failure sky shows its magenta that way, so a broken sky never lights the world magenta. The shadow lights the world,
the sun or moon reaching each place only by what gets through, so it must agree with what the sky shows of the light:
air that reddens the sun's disc near the horizon reddens the light on the ground, the ground hides a light below the
horizon, clouds that hide the disc darken the ground beneath them, and a sky with no air and nothing in front of the
light lets it all through. The weather is the world's state and the params are the sky's look: a
sky reads the clouds' coverage, colours and wind from its inputs and keeps its params for how it draws them. A sky that
integrates along the view in steps, through air or clouds, can afford few of them: its inputs hand each pixel of the
view a dither that moves each frame, and steps offset by it are blended by the view's temporal upscale into many more.
The engine's procedural sky, under `Shaders/Skies/`, is the example.

## What must agree

- The shader is a `.surf.hlsl` for what a mesh draws, or a `.sky.hlsl` for the world's sky, never a pass shader. A
  mesh that names a sky material is drawn as the failure, and so is a sky whose material is a surface's.
- Each params key is a member of the surface's parameter block, with the form its type takes.
- A texture reference names a texture or a `.rtex`, under the key for each.
- A masked material needs a surface that has an alpha cutoff.

## Adding a material

Write `Name.mat` anywhere under the project's `Assets/`, or in this folder for the engine, and name it from an
entity's `Renderer`, in the slot its model's mesh names ([Models](../Models/README.md)), or, for a sky, from the
world's `Sky`.
