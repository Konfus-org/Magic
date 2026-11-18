# Magic domain, a Blender add-on

Reads a domain of a Magic project into Blender, and writes it back. A person lays out a scene with it, and the
reference renders of the visual tests build their scene with it
([Reference views](../../docs/DEBUGGING.md#reference-views)).

## Install

Run this from the repository's root, with the path of your `blender.exe`. The folder must exist.

```powershell
& 'C:/Program Files (x86)/Steam/steamapps/common/Blender/blender.exe' --command extension build --source-dir tools/Blender/magic_domain --output-dir <folder>
```

Then Edit > Preferences > Get Extensions > Install from Disk with that zip.

A script needs no install: put `tools/Blender` on the path and import the package.

```python
import sys
sys.path.insert(0, 'tools/Blender')
from magic_domain import import_domain, export_domain, set_hour
```

## The Magic program

The add-on asks Magic for what only Magic knows: the component types, the light of the domain, and the `.meta` of each
file that the export writes, with the import settings of a new texture in it. It starts Magic with no window and builds
nothing, so build Magic first (`tools/Scripts/Validate.ps1 -Only Build`) and give the add-on the program of that build.

The program is the first of these that is set:

1. The option of a script, such as `--magic` of `tools/Reference/BuildScene.py`.
2. Edit > Preferences > Add-ons > Magic Domain > Magic Executable.
3. The `MAGIC_EXE` environment variable.

With none of the three set, the run stops with an error that names these three places. The add-on looks for no program
of its own and needs no repository.

The add-on keeps what it reads under the engine's own cache folder, `%LOCALAPPDATA%/Magic/Cache/Blender`: the answers
of Magic, and the mesh of each model that an import read. A person deletes the folder at any time, and the next import
makes it again.

An answer of Magic is kept, so a second import of a domain that nothing changed, with the same program, starts Magic no
more. The key of an entry holds the program with each `.dll` under its folder, the lines that were asked, the write
times of the project's files and the write times of the project's settings, so an entry of another program, of a gem
that was built again, of other lines, of other settings or of a project that changed is never read.

## File > Import > Magic (.magic)

Pick the project's `.magic` file and the domain. All of the domain comes in. The scene gets every mesh that Magic
draws with its materials, every lamp, an instance of each prefab that the domain uses, an empty at each entity that
puts none of those in the scene, and the light that the domain's own files hold. The scene keeps the project and the
domain, so the export writes it back where it came from.

When the scene holds objects, Blender asks one question before the file browser opens. An empty scene asks nothing.
The question says how many objects the scene holds, and the two answers are Keep Objects and Clear Scene. The answer
of the last import is the default. The same choice is an option in the file browser's side panel, beside the domain.

With no window, the import asks nothing and clears the scene. A script that wants the objects of the scene kept
passes `objects='KEEP'` to `magic.import_domain`.

Clear Scene takes the objects of the scene away, with the collections that hold them, the data that only they used
and the record of the project that an earlier import left: the import then gives the same scene as an import into a
new file. Keep Objects adds the domain to what the scene holds.

A scene keeps the record of one project and one domain, which is the domain that came in last. The export writes
that one domain, and it knows the records of that domain's folder alone: it would write the objects of the other
domain into it as new entities. A keep import into a scene that already holds a domain says this in its report.
Import a second domain into a file of its own to write it back.

The import writes no step of undo. One step of Blender's undo holds the whole file, and a file of tens of thousands
of objects costs seconds and hundreds of megabytes to write. Ctrl+Z after an import steps back to the file as it
stood before the import, which takes the domain away. Esc stops the import and takes away the objects that it built,
and nothing that stood in the scene before it. After a clear, the clear is done: the scene is empty.

While the import runs, Blender takes no input but Esc. A progress bar in the middle of each 3D viewport holds the
part that is done and what is built now. The domain is built out of the view layer and comes into the view in one
step at the end, so Blender draws it one time. The import in a window takes the time of the plain call.

The models come first, each as a part of its own under its name, then the entities that draw them. A model is read
from its file one time on a machine: the first read builds its mesh and keeps it as a `.blend` of its own under the
cache folder (The Magic program), and each later import appends the mesh from there. The key of an entry holds the
content of the model's file, so a model that changed is read again.

At the end, a dialog says what the import did. Its first row holds the domain's name, the count of the objects that it
drew and the seconds that it took. Under it stands one row for each warning and for each error, with Blender's icon of
its kind. An import that failed, or that Esc stopped, shows the same dialog with what happened. The same lines go to
the Info editor and the status bar. A run with no window prints the lines and opens no dialog.

A new Blender draws from 0.01 m to 1000 m, and it looks at the world's origin. A domain is kilometres wide, so the
viewport can show nothing of it, although the scene holds all of it. The dialog warns of this with the width of the
domain and the distance that the viewport draws. It warns a second time when the scene holds more objects than Blender
draws at speed: the viewport, the selection and each edit can be slow.

The dialog shows the button Use Recommended Settings under one of these warnings. In each 3D viewport of the screen,
the button raises the clip end to the one that draws the whole domain, frames the scene (View > Frame All), sets
Viewport Shading to Solid, turns off Outline, Selected Outline, Extras, Relationship Lines and Statistics, and applies
the view distance of the Magic Domain panel from the middle of the domain. It never lowers a clip end, and it leaves
the clip end of a viewport that draws far enough of the domain as it is. The clip end and the middle come from the
boxes of the domain's chunks, because Blender moves the view of the frame over several frames. In the scene, it turns
on Simplify with Max Subdivision 0 and Child Particles 0 for the viewport. In the outliners of the screen, it closes
one level of the rows: Blender holds the open rows of an outliner in no field that an add-on reads, so it closes one
level of every row and not the children of the domain's collection alone. In Blender's own preferences, it sets
Viewport > Textures > Limit Size to 2048, which is a preference of that Blender and of no one file: it holds for every
file that the Blender opens, and it is not written to disk. The status bar then lists each setting that it changed. A
person turns each setting back in the Shading popover, the Overlays popover, Render Properties > Simplify or
Preferences > Viewport. The button changes no object, no material and no field that a render reads, so the export
writes what it wrote before.

A script builds the whole domain in one step with `import_domain`. It adds the domain to the scene, draws nothing and
says each stage on the console.

### What a thing of Magic is in Blender

| Magic | Blender |
| --- | --- |
| A model | One mesh datablock, which each object that draws the model links |
| A material | One material, which each object that uses it links |
| A prefab | One collection, which an empty brings in at each place that uses it |
| A chunk of the world | One collection named for the chunk, under the domain's, with that chunk's own objects |
| An entity that draws a model | One object that links the model's mesh, with its materials on the object's slots |
| An entity that brings a prefab in | One empty whose instance collection is that prefab's collection |

The import puts the domain under one collection of the domain's name. In it, the objects of each chunk of the world
stand in a collection of the chunk's name, and the objects of the prefabs that are no chunk stand in `Globals`.

The prefab collections stand under a collection named `MagicPrefabs`, which the view layer leaves out: Blender draws a
prefab where its instances stand and nowhere else. To edit a prefab, open its collection there and move, delete or add
objects in it. Every instance follows at once, and the export writes the change one time, in that prefab's own file.

Each object keeps the record that it came from and what of that record it is, which the export reads. The components
of the entity stand on the first object that the entity puts in the scene, and Object Properties > Magic shows them.

The light of the domain is its own: one sun lamp for each directional light at the rotation, the colour and the
strength that the files hold, and Blender's sky only when the domain has a Sky entity. Nothing is invented. Which of
the lights lights the sky, and what the sky's light is multiplied by, are Magic's: the import asks it for both in the
one run that gives the component types. A render script puts the light of one hour in their place with `set_hour`.

## The Magic Domain panel

Blender takes time for each object that it draws, and a domain holds tens of thousands of them. The sidebar of the 3D
viewport (N) > Magic > Magic Domain hides the chunks that stand far away, so that the viewport turns at speed.

| Field | What it is |
| --- | --- |
| View Distance | Metres from the point that the viewport turns around, past which a chunk is hidden |
| Far Chunks | Hide, which leaves the chunk out of the view layer, or Bounding Box, which draws its objects as boxes |
| Update | Hides the far chunks and shows the near ones, from the viewport that holds the panel |
| Show All | Shows every chunk again |

The distance and the choice stand in the scene, so the file keeps them. Nothing runs on its own: a person presses
Update after the view moved, and Use Recommended Settings presses it one time.

Hide costs nothing for the objects of a hidden chunk, and Bounding Box draws a box for each of them: Hide is the
faster of the two, and Bounding Box shows where the far parts of the domain stand.

A render draws no object of a chunk that Hide left out of the view layer. The panel says how many chunks are out, and
Show All brings them back: press it before a render.

## A material in Blender

One material of Magic is one Blender material: a Principled BSDF with the values of the material's params, and an
Image Texture node for each map that the material holds. The import and the export read one table, so each value
stands in one place in Blender and the two directions give the same value.

| Magic | Blender |
| --- | --- |
| `color` | Base Color, and its alpha in Alpha |
| `roughness` | Roughness |
| `metalness` | Metallic |
| `emissive` | Emission Color and Emission Strength, in watts for Blender and in nits for Magic |
| `colorMap` | An Image Texture through a Mix node that multiplies the base colour into Base Color |
| `ormMap` | An Image Texture through a Separate Color node: its green into Roughness and its blue into Metallic, each through a Math node of the factor |
| `normalMap` | An Image Texture through a Normal Map node into Normal |
| `emissiveMap` | An Image Texture through a Mix node that multiplies the emissive colour into Emission Color |
| `clarity` of a glass | The factor of a Mix Shader node between the pane and a Transparent BSDF |
| `transparency` | The render method of the material: Dithered for opaque and for masked, and Blended for transparent |
| `isDoubleSided` | Backface culling, which is on for a material of one side |
| The alpha of `colorMap` | The Alpha output of the colour map's own image in Alpha, which Magic multiplies into the colour |
| The texture of a map | The image of its Image Texture node, in sRGB for a colour map and as data for the others |

Blender has two render methods for Magic's three transparencies, so a material that is drawn as dithered keeps the one
of opaque and masked that its file holds.

A map's Image Texture node reads the texture asset's own file, so one material points at a texture of the project and
another material takes the same image.

The export reads these shapes of a material that another tool made, and writes the material that Magic reads from
them:

| In Blender | What the export writes |
| --- | --- |
| One image straight in Roughness, and one straight in Metallic | One ORM image of both: the roughness in its green channel and the metalness in its blue, as Magic's shader reads them. The add-on packs it with Blender's own image API, each input that the image drives takes the factor one, and the nodes of the two images are gone, so a person sees the one ORM map |
| An image through a Normal Map node into Normal | The `normalMap` of the material, which is the shape that an import builds |
| The alpha of the colour image in Alpha | Nothing: Magic multiplies the alpha of its colour map into the colour already, so the alpha of the material's own file stands |
| A Mapping node and a Texture Coordinate node before an image | Nothing, while the mapping moves, turns and scales nothing: Magic reads every map at the UVs of the mesh |

## Object Properties > Magic

The panel holds the components of the entity that the object stands for. Each component is a box that folds, with its
values as the fields that Blender draws them with: a number, a whole number, a yes or no, a text, a choice of a set,
a vector, a colour swatch, and the project path of a handle to an asset. Add Component adds one with the values of its
type, from the list of the types that Magic knows, and the X of a box takes that component away. The export writes
each change.

Magic says which components there are and what each one holds (`query components`), so the panel shows the components
of each loaded gem as well as Magic's own. The import runs Magic one time, with no window, and the scene keeps the
answer: a file that opens again runs Magic no more.

A value that Blender holds its own way is not in the panel a second time. The box of such a component names where
Blender holds it: the place of the object, its mesh and material slots, its lamp, its tags, and the strength of the
world's background. The box holds a field for each member of that component which Blender holds nowhere, such as the
material of the sky, the sun or the moon of a directional light, and whether a renderer or a point light casts no
shadow. The export writes those fields as it writes the fields of any other component.

A vector stands in Magic's own axes, which are left-handed with Y up, and not in Blender's: the panel shows the
numbers that the file holds.

A member whose type the panel has no field for, such as a struct of its own, and a component that the run of Magic did
not know, stand as the JSON that the import read, under Kept. A person cannot change it, and the export writes it back
as it was. The import's report names each component that the run of Magic did not know, with how many entities hold
it.

## File > Export > Magic (.magic)

It asks "overwrite the domain \<name\> of \<project\>?", and a field takes another folder. A scene that came from no
import asks for the project and the domain. The report says what it wrote and what it skipped, in the info bar and in
the console.

It writes:

- Each entity where its object stands now, in the prefab of the chunk that the place falls in. An object that moved
  over a chunk's border moves to the other chunk's prefab, and the collection that holds the object says nothing
  about which chunk it belongs to. Every chunk is written, a chunk that the Magic Domain panel hides as well.
- An entity gone for each object that is gone.
- A new entity for each copy of an object, which draws the same model with the same materials, and a new instance of
  a prefab for each copy of an instance.
- A new entity with a Transform, a name and the tags of the `tags` property for each new empty. A new prefab gets its
  `.meta` from Magic (`import`).
- A lamp's colour, strength, bulb and reach, a marker's name and its tags, the way that a sun lamp was turned with
  the colour and the intensity of the light that it gives, and the brightness of the domain's sky.
- Each value of a component that a person changed in Object Properties > Magic, each component that a person added,
  with the values of its type, and the components that a person took away.
- A change inside a prefab's collection, as one write of that prefab's own file. Every instance follows, and the
  report says `prefab <name> changed: N instances follow`.
- A prefab that nothing changed, as a copy of the file, which keeps its comments. A prefab that something changed is
  written again from its records, and the comments of that one file are lost.
- A model whose points a person changed, over the model's own file. Blender writes the mesh as an FBX and Magic
  imports it over the file (`import --replace`), which keeps the file's `.meta`, so each entity that draws the model
  keeps its handle. One model is one mesh, so one write holds for every object that draws it.
- A material whose values a person changed, in the material's own file: each value of the table of a material in
  Blender, a map that now reads the image of another texture of the project among them, and a map taken away, which
  writes the param with no texture. The engine writes it, in one run for every changed material: a `set` line for
  each value that changed, then an `export` of the material over its own file, which keeps its `.meta` and so its id.
  A param of the material that the table does not hold stays as it was read, and the report names it. The comments of
  that file are lost, and the params of a material stand in no order, so the written file can hold them in another
  order than before.
- The image of a map that is no texture of the project yet, as a new texture in the folder of the domain's textures:
  an image that a person generated or packed too. The add-on saves it with Blender's own image save, and Magic gives
  the file its `.meta` (`import --meta`), with the colour space that the kind of the map needs: sRGB for a colour map
  and for an emissive map, and the pixels as they stand for an ORM map and for a normal map. The material names the
  new handle.
- The pixels of a texture of the project that a person changed in Blender, over the texture's own file, in the format
  of that file. The file keeps its `.meta` and so its id, and Magic reads the file again (`import`).
- A material that a person made in Blender, as a new material in the folder of the domain's materials. The engine
  makes the file with the values of its own default material and gives it its `.meta` (`new asset`), and the values of
  the table go into it after that. Each entity whose slot holds the material names the new handle.
- The materials in the slots of an object, in that object's entity.
- A mesh that no model of Magic is behind, as a new model in the folder of the domain's models, with its `.meta` from
  Magic (`import`), and its object as an entity that draws it.

## What the export does not write

Each asset that the export cannot write stops at itself: its line of the report says which asset it is and what of it
the add-on does not know, and the rest of the export goes on. Set the Magic program (The Magic program), and export
into a folder of the project: Magic writes every model into it.

It does not write, and names each in its report:

- A model whose mesh draws fewer materials than the model names, or whose faces use its material slots in another
  order than Magic numbers them, and a model that more than one mesh of the scene now stands for. Such a mesh is no
  one set of points of that model.
- A new model with no material slot, which Magic would draw nothing of, and a new model whose name a file of the
  folder has already.
- A material with a node that no material of Magic holds, or a value that comes out of such a node. The report names
  the node, and that material is not written at all.
- A roughness image and a metalness image of two sizes, which are no one ORM map. The report names the images, and
  the material keeps the two images.
- A map whose image is no asset of the project yet and whose file the add-on cannot write: an image whose name a file
  of the textures folder has already. The report names the image, and that material is not written at all.
- A glossiness image, which comes through an Invert node, and any other node that no material of Magic holds: the
  add-on bakes nothing. The report names the node.
- A Mapping node that moves, turns or scales the UVs of a map. Magic reads every map at the UVs of the mesh, so the
  report names the node and its value, and that material is not written at all.
- Anything but the alpha of the colour map's own image in Alpha. The report names the node that gives it.
- A value that Blender holds and a material of Magic has no place for: the strength of a Normal Map node, and the
  transmission, subsurface, coat and sheen of the Principled BSDF. The report names it, and that material is not
  written at all.
- A node in an input of the Principled BSDF that no value of Magic's material stands behind: an image in Coat Weight,
  in the Base Color of a glass or in Emission Strength. The report names the input and the node, and that material is
  not written at all.
- The colour and the intensity of a directional light whose lamp is no sun lamp of Blender any more. The report names
  the object, and the way that the lamp points is written.
- A model whose file is of another format than FBX, which is the format that Blender writes here, and a model whose
  file stands outside the project, as a model of another sample does.
- The lamp of an hour and Blender's sky, which `set_hour` makes anew.
- A camera, or any other object of Blender that is no entity of Magic.
- An entity of a prefab that the domain brings in, while the export writes to another folder. That prefab stands
  outside the domain's folder, and every instance of it changes with it.
- An entity that puts more than one object in the scene, while only some of them are gone.
- The path of a handle that names no asset of the project. The report names the component and the member, and that one
  value stays as it was read.

A person who breaks a link writes no wrong data: an object whose mesh is now its own is a new entity that draws the
same model while its points are equal, and a new model of the project when they are not.
