# Magic domain, a Blender add-on

Reads a domain of a Magic project into Blender, and writes it back. The reference renders of the visual tests build
their scene with it ([Reference views](../../docs/DEBUGGING.md#reference-views)), and a person lays out a scene with
it.

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

## File > Import > Magic (.magic)

Pick the project's `.magic` file and the domain. All of the domain comes in. The scene gets every mesh that Magic
draws with its materials, every lamp, an instance of each prefab that the domain uses, an empty at each entity that
only stands somewhere, and the light that the domain's own files hold. The scene keeps the project and the domain, so
the export writes it back where it came from.

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

The import is one step of undo, with its clear in it. Esc stops it and takes away the objects that it built, and
nothing that stood in the scene before it. After a clear, the clear is done: the scene is empty.

The import builds the domain in parts, so Blender answers while the scene fills in. A progress bar in the status
bar holds the part that is done and what is built now. Blender shows the progress of its own long jobs in the same
place. The domain is built out of the view layer and comes into the view in one step at the end, so Blender draws it
one time. The import in a window takes the time of the plain call.

At the end, a dialog says what the import did. Its first row holds the domain's name, the count of the objects that it
drew and the seconds that it took. Under it stands one row for each warning and for each error, with Blender's icon of
its kind. An import that failed, or that Esc stopped, shows the same dialog with what happened. The same lines go to
the Info editor and the status bar. A run with no window prints the lines and opens no dialog.

A new Blender draws from 0.01 m to 1000 m, and it looks at the world's origin. A domain is kilometres wide, so the
viewport can show nothing of it, although the scene holds all of it. The dialog warns of this with the width of the
domain and the distance that the viewport draws. To bring the domain into view by hand, press N in the 3D viewport,
open the View tab, and raise Clip End to the value that the warning gives. Then press Home (View > Frame All). The
button Set Clip End and Frame All in the dialog does the same for each 3D viewport of the screen.

A script builds the whole domain in one step with `import_domain`. It adds the domain to the scene, draws nothing and
says each stage on the console.

The models come first, each as a part of its own under its name, then the entities that draw them. A model is read
from its file one time on a machine: the first read builds its mesh and keeps it as a `.blend` of its own under the
engine's own cache folder, `%LOCALAPPDATA%/Magic/Cache/Blender`. Each later import appends the mesh from there. The
key of an entry holds the content of the model's file, so a model that changed is read again. A person deletes the
folder at any time, and the next import makes it again.

Each object keeps the record that it came from and what of that record it is, which the export reads. A component that
the add-on does not understand, such as a script, stays in the file and comes back out as it was.

### What a thing of Magic is in Blender

| Magic | Blender |
| --- | --- |
| A model | One mesh datablock, which each object that draws the model links |
| A material | One material, which each object that uses it links |
| A prefab | One collection, which an empty brings in at each place that uses it |
| An entity that draws a model | One object that links the model's mesh, with its materials on the object's slots |
| An entity that brings a prefab in | One empty whose instance collection is that prefab's collection |

The prefab collections stand under a collection named `MagicPrefabs`, which the view layer leaves out: Blender draws a
prefab where its instances stand and nowhere else. To edit a prefab, open its collection there and move, delete or add
objects in it. Every instance follows at once, and the export writes the change one time, in that prefab's own file.

The light of the domain is its own: one sun lamp for each directional light at the rotation, the colour and the
strength that the files hold, and Blender's sky only when the domain has a Sky entity. Nothing is invented. A render
script puts the light of one hour in their place with `set_hour`.

## File > Export > Magic (.magic)

It asks "overwrite the domain \<name\> of \<project\>?", and a field takes another folder. A scene that came from no
import asks for the project and the domain. The report says what it wrote and what it skipped, in the info bar and in
the console.

It writes:

- Each entity where its object stands now, in the prefab of the chunk that the place falls in. An object that moved
  over a chunk's border moves to the other chunk's prefab. A new prefab gets its `.meta` from Magic (`new handle`), so
  build Magic first, and export into a folder of the project.
- An entity gone for each object that is gone.
- A new entity for each copy of an object, which draws the same model with the same materials, and a new instance of
  a prefab for each copy of an instance.
- A new entity with a Transform, a name and the tags of the `tags` property for each new empty.
- A lamp's colour, strength, bulb and reach, a marker's name and its tags, the way that a sun lamp was turned, and
  the brightness of the domain's sky.
- A change inside a prefab's collection, as one write of that prefab's own file. Every instance follows, and the
  report says `prefab <name> changed: N instances follow`.
- A prefab that nothing changed, as a copy of the file, which keeps its comments. A prefab that something changed is
  written again from its records, and the comments of that one file are lost.
- A model whose points a person changed, over the model's own file. Blender writes the mesh as an FBX and Magic
  imports it over the file (`import --replace`), which keeps the file's `.meta`, so each entity that draws the model
  keeps its handle. One model is one mesh, so one write holds for every object that draws it.
- A material whose values a person changed, in the material's own file: the base colour, the roughness, the metalness
  and the emissive light of a material, and the colour, the roughness and the clarity of a glass. The maps stay as the
  file holds them, and the comments of that file are lost.
- A mesh that no model of Magic is behind, as a new model in the folder of the domain's models, with its `.meta` from
  Magic (`import`), and its object as an entity that draws it.

It does not write, and names each in its report:

- A model whose mesh draws fewer materials than the model names, or whose faces use its material slots in another
  order than Magic numbers them, and a model that more than one mesh of the scene now stands for. Such a mesh is no
  one set of points of that model.
- A new model with no material slot, which Magic would draw nothing of, and a new model whose name a file of the
  folder has already.
- A material with a node that no material of Magic holds, or a value that comes out of such a node. The report names
  the node, and that material is not written at all.
- A model whose file is of another format than FBX, which is the format that Blender writes here, and a model whose
  file stands outside the project, as a model of another sample does.
- The lamp of an hour and Blender's sky, which `set_hour` makes anew.
- A camera, or any other object of Blender that is no entity of Magic.
- An entity of a prefab that the domain brings in, while the export writes to another folder. That prefab stands
  outside the domain's folder, and every instance of it changes with it.
- An entity that puts more than one object in the scene, while only some of them are gone.

Each asset that the export cannot write stops at itself: its line of the report says which asset it is and what of it
the add-on does not know, and the rest of the export goes on. Build Magic first, and export into a folder of the
project: Magic writes every model into it.

A person who breaks a link writes no wrong data: an object whose mesh is now its own is a new entity that draws the
same model while its points are equal, and a new model of the project when they are not.
