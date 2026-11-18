# Writes the assets of a project that a person changed in Blender: a model whose points moved, a material whose values
# changed, and a mesh that becomes a new model. Magic puts every model into the project itself (`import`), so no model
# format is written here: Blender's own exporter makes the file, and Magic copies it in, gives a new file its .meta and
# keeps the .meta of a file that it replaces, so each entity that draws the model keeps its handle.
#
# One write of a model is one write for every object that draws it, since one model is one mesh datablock (the meshes).
# An asset whose change the add-on cannot resolve is written not at all: its line in the report says what it is and
# what of it the add-on does not know, and the rest of the export goes on.
import json
import os
import tempfile

import bpy

from .materials import (asset_paths, convert_straight, image_file, map_images, material_values, pack_orm,
                        read_space, side_of)
from .meshes import mesh_hash, model_fault
from .project import SWAP, comment_lines, read_jsonc, run_magic

# The format that a new model and a replaced model are written in: Magic's model importer reads it, and Blender writes
# it with an exporter of its own.
MODEL_FORMAT = '.fbx'

# The format of a material, which Magic's `new asset` reads as the type of a new asset in that file.
MATERIAL_FORMAT = '.mat'

# The format that a new texture is written in: Blender writes it, Magic's texture importer reads it, and it keeps each
# channel of an ORM map and the alpha of a colour map as it stands.
TEXTURE_FORMAT = '.png'


def write_model_file(mesh, path):
    """Writes one mesh as a model file, with Blender's own exporter. Its points go back through the swap of the axes
    that the import brought them through, so that a read of the file gives the mesh again. Each slot takes a material
    of its own, in the order of the slots, so that the file names as many materials as the mesh has slots and a read
    of it keeps that order (the meshes)."""
    scene = bpy.context.scene
    copy = mesh.copy()
    copy.transform(SWAP)
    copy.materials.clear()
    holders = [bpy.data.materials.new(f'MagicSlot{index}') for index in range(len(mesh.materials))]
    for material in holders:
        copy.materials.append(material)
    o = bpy.data.objects.new(mesh.name, copy)
    # The one object in a collection of its own, which the export writes alone: a selection of it would take the
    # selection of the person's own objects away.
    collection = bpy.data.collections.new('MagicWrite')
    scene.collection.children.link(collection)
    collection.objects.link(o)
    layer = bpy.context.view_layer
    was = layer.active_layer_collection
    layer.active_layer_collection = next(child for child in layer.layer_collection.children
                                         if child.collection is collection)
    try:
        bpy.ops.export_scene.fbx(filepath=path, use_active_collection=True, object_types={'MESH'},
                                 use_mesh_modifiers=False, use_triangles=True, path_mode='COPY')
    finally:
        layer.active_layer_collection = was
        bpy.data.objects.remove(o, do_unlink=True)
        bpy.data.meshes.remove(copy, do_unlink=True)
        for material in holders:
            bpy.data.materials.remove(material, do_unlink=True)
        bpy.data.collections.remove(collection, do_unlink=True)


def project_folder(export):
    """The folder that holds the project's own file, which every folder of an asset of it stands under."""
    return os.path.dirname(os.path.abspath(export.scene['magic_project']))


def models_folder(export):
    """The folder of the project that its models stand in, by the models that the domain draws already."""
    return assets_folder(export, lambda mesh: int(mesh['magic_model']) if 'magic_model' in mesh else None,
                         bpy.data.meshes, 'Models')


def materials_folder(export):
    """The folder of the project that its materials stand in, by the materials that the domain uses already."""
    return assets_folder(export, lambda material: int(material['magic_id']) if 'magic_id' in material else None,
                         bpy.data.materials, 'Materials')


def assets_folder(export, held, things, keep):
    """The folder of the project that assets of one kind stand in, under the project, by the assets of that kind that
    the domain uses already: `held` gives the id that one thing of Blender came from, over `things` of Blender. A
    domain whose assets of that kind stand outside the project keeps `keep`."""
    for thing in things:
        found = export.assets.get(held(thing))
        if found is None:
            continue
        under = os.path.relpath(os.path.dirname(found[0]), project_folder(export))
        if not under.startswith('..'):
            return under
    return keep


def textures_folder(export):
    """The folder of the project that its textures stand in, by the images that the domain's materials read already. A
    domain whose textures stand outside the project keeps the folder of its materials."""
    paths = asset_paths(export.assets)
    return assets_folder(export, lambda image: paths.get(image_file(image)), bpy.data.images,
                         materials_folder(export))


def changed_models(export):
    """Each mesh of a model of Magic whose points a person changed, by the model's id, and a line in the report for
    each model that more than one mesh of the scene now stands for: such a model has no one set of points to write."""
    changed = {}
    for mesh in bpy.data.meshes:
        if 'magic_hash' in mesh and 'magic_model' in mesh and mesh['magic_hash'] != mesh_hash(mesh):
            changed.setdefault(int(mesh['magic_model']), []).append(mesh)
    models = {}
    for model, meshes in changed.items():
        if len(meshes) > 1:
            names = ', '.join(mesh.name for mesh in meshes)
            export.skip(names, f'{len(meshes)} meshes of the scene changed the one model {model}, '
                               'so the model has no one set of points to write')
            continue
        models[model] = meshes[0]
    return models


def new_meshes(export):
    """Each mesh of the scene that no model of Magic is behind, by the name that its model file takes."""
    new = {}
    for o in export.scene.collection.all_objects:
        if o.type == 'MESH' and 'magic_model' not in o.data:
            new[o.data.name] = o.data
    return new


def write_models(export, folder):
    """Writes the model file of each model that changed and of each new mesh, into the folder of a run, and gives the
    lines that put them into the project: a changed model over the file that it came from, with its .meta kept, and a
    new model as a file of its own in the folder of the domain's models."""
    lines, made = [], {}
    for model, mesh in changed_models(export).items():
        fault = model_fault(mesh)
        if fault:
            export.skip(mesh.name, f'its points changed, and {fault}')
            continue
        held = export.assets.get(model)
        if held is None:
            export.skip(mesh.name, f'its points changed, and the model {model} that it came from is '
                                   'no asset of this project')
            continue
        path = held[0]
        if os.path.splitext(path)[1].lower() != MODEL_FORMAT:
            export.skip(mesh.name, f'its points changed, and its file {os.path.basename(path)} is '
                                   f'no {MODEL_FORMAT}, which is the one format that this writes')
            continue
        into = os.path.relpath(os.path.dirname(path), project_folder(export))
        if into.startswith('..'):
            export.skip(mesh.name, f'its points changed, and its file {path} stands outside the '
                                   'project, which Magic imports a model into alone')
            continue
        write_model_file(mesh, os.path.join(folder, os.path.basename(path)))
        lines.append(f'import "{os.path.join(folder, os.path.basename(path))}" "{into}" --replace')
        export.report.assets.append(f'model {mesh.name} written to {path}')
        made[mesh.name] = mesh

    into = models_folder(export)
    for name, mesh in new_meshes(export).items():
        fault = model_fault(mesh)
        if fault:
            export.skip(name, f'it is a new model, and {fault}')
            continue
        file = os.path.join(project_folder(export), into, name + MODEL_FORMAT)
        if os.path.exists(file):
            export.skip(name, f'it is a new model, and {file} is a file of the project already: '
                              f'name the mesh {name} of the scene another way')
            continue
        write_model_file(mesh, os.path.join(folder, name + MODEL_FORMAT))
        lines.append(f'import "{os.path.join(folder, name + MODEL_FORMAT)}" "{into}"')
        export.report.assets.append(f'new model {name} written to {file}')
        made[name] = mesh
    return lines, made


def read_handles(export, made):
    """Gives each mesh that the run wrote the model that its file now is: a new model takes the id that Magic gave its
    .meta, and every mesh takes the points it was written with, so that the next export sees no change of it."""
    for name, mesh in made.items():
        if 'magic_model' not in mesh:
            path = os.path.join(project_folder(export), models_folder(export), name + MODEL_FORMAT)
            meta = read_jsonc(path + '.meta')
            mesh['magic_model'] = str(meta['id'])
            mesh['magic_slots'] = str(len(mesh.materials))
            export.report.assets.append(f'new model {name} has the id {meta["id"]}')
        mesh['magic_hash'] = mesh_hash(mesh)


def scene_materials(export):
    """Each Blender material that an object of the scene uses, by its name. A material that nothing uses is one that a
    person took off every object, and Magic keeps its file as it is."""
    found = {}
    for o in export.scene.collection.all_objects:
        for slot in o.material_slots:
            if slot.material is not None:
                found[slot.material.name] = slot.material
    return found


def written_materials(export):
    """Each Blender material that the export writes: one that an object of the scene uses, and one that a material of
    the project is behind, by its name."""
    found = dict(scene_materials(export))
    for material in bpy.data.materials:
        if 'magic_id' in material:
            found[material.name] = material
    return found


def material_data(export, material):
    """The file of the Magic material behind one Blender material, and nothing for a material that a person made in
    Blender, which Magic's default material is behind."""
    held = export.assets.get(int(material['magic_id'])) if 'magic_id' in material else None
    return read_jsonc(held[0]) if held else {}


def save_image(image, path, space):
    """Writes one image of Blender to a file of its own, with Blender's own image save: an image that a person
    generated or packed too. The image then reads that file, so the material that uses it names the texture which the
    file becomes, and it takes the colour space that the kind of its map needs (read_space)."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    image.filepath_raw = path
    image.file_format = TEXTURE_FORMAT[1:].upper()
    image.save()
    read_space(image, space)


def texture_lines(export, name, material, into):
    """The lines that put the images of one material into the project, and the image of each new texture with the file
    that it took: an image that the project holds no texture for is saved into the folder of the domain's textures and
    comes in with the import settings that the kind of its map needs (`--meta`), and an image that a person changed and
    that is a texture already goes back over its own file."""
    paths = asset_paths(export.assets)
    lines, made = [], []
    for row, node in map_images(side_of(material, export.assets, material_data(export, material))).items():
        image = node.image
        held = paths.get(image_file(image))
        if held is not None:
            # A texture of the project whose pixels a person changed goes back in its own format, over its own file,
            # which keeps its .meta and so its id.
            if image.is_dirty:
                path = export.assets[held][0]
                image.save()
                lines.append(f'import "{path}"')
                export.report.assets.append(f'texture {image.name} written to {path}')
            continue
        file = os.path.join(project_folder(export), into, os.path.splitext(image.name)[0] + TEXTURE_FORMAT)
        if os.path.exists(file):
            export.skip(name, f'its {row.name} reads the image {image.name}, which is no texture of '
                              f'this project, and {file} is a file of the project already: name the '
                              'image another way')
            continue
        save_image(image, file, row.space)
        lines.append(f'import "{file}" --meta \'{json.dumps({"usage": row.usage})}\'')
        made.append((image, file))
        export.report.assets.append(f'new texture {image.name} written to {file}')
    return lines, made


def write_textures(export):
    """Writes the images that the materials of the scene read as the textures of the project, through one run of
    Magic. Magic gives each new file its `.meta`, with the import settings that the kind of its map needs, so no
    format and no rule of Magic stands here.

    A material that another tool made holds an image straight in an input of the Principled BSDF, and its roughness
    and its metalness as one image each: the add-on builds the map that Magic holds for each straight image
    (convert_straight), and packs the roughness and the metalness into the one ORM map that Magic reads (pack_orm).

    It runs before the materials are written, so that a material which reads a new image names its new handle."""
    lines, made = [], []
    into = textures_folder(export)
    for name, material in sorted(written_materials(export).items()):
        side = side_of(material, export.assets, material_data(export, material))
        if side.bsdf is None:
            continue
        convert_straight(side)
        fault = pack_orm(side)[1]
        if fault:
            export.skip(name, f'the export cannot pack its roughness and its metalness into one ORM map: {fault}')
        held, written = texture_lines(export, name, material, into)
        lines += held
        made += written
    if not lines:
        return
    run_magic(export.scene['magic_project'], lines)
    for image, file in made:
        meta = read_jsonc(file + '.meta')
        export.assets[meta['id']] = (file, meta)
        export.report.assets.append(f'new texture {image.name} has the id {meta["id"]}')


def make_materials(export):
    """Makes the file of each material that a person made in Blender, through one run of Magic: `new asset` writes it
    with the values of Magic's default material and gives it a .meta, so that no format of Magic is written here. It
    gives each material the id that Magic gave it, and the material whose nodes it cannot read gets no file."""
    lines, made = [], []
    into = materials_folder(export)
    for name, material in sorted(scene_materials(export).items()):
        if 'magic_id' in material:
            continue
        faults = material_values(material, export.assets, {})[1]
        if faults:
            for fault in faults:
                export.skip(name, f'it is a new material, and the export cannot read {fault}')
            continue
        file = os.path.join(project_folder(export), into, name + MATERIAL_FORMAT)
        if os.path.exists(file):
            export.skip(name, f'it is a new material, and {file} is a file of the project already: '
                              f'name the material {name} of the scene another way')
            continue
        lines.append(f'new asset "{file}"')
        made.append((material, file))
    if not lines:
        return
    run_magic(export.scene['magic_project'], lines)
    for material, file in made:
        meta = read_jsonc(file + '.meta')
        material['magic_id'] = str(meta['id'])
        export.assets[meta['id']] = (file, meta)
        export.report.assets.append(f'new material {material.name} written to {file}')


def write_materials(export):
    """Writes each material that a person changed back into its own file, through one run of Magic: a `set` line for
    each value that the Blender material now says differently, then an `export` of the material over its own file,
    which keeps its .meta and so its id. Magic holds every other value of the material and writes it as it was. A
    value of the Blender material that Magic's material has no place for is a line of the report for that material,
    and that material gets no line of the run."""
    lines, written = [], []
    for material in bpy.data.materials:
        if 'magic_id' not in material:
            continue
        asset = int(material['magic_id'])
        held = export.assets.get(asset)
        if held is None:
            export.skip(material.name, f'the material {asset} that it came from is no asset of this project')
            continue
        path = held[0]
        values, faults, kept = material_values(material, export.assets, read_jsonc(path))
        for fault in faults:
            export.skip(material.name, f'it changed, and the export cannot read {fault}')
        if faults or not values:
            continue
        lines += [f"set {asset}.{name} '{json.dumps(value)}'" for name, value in values.items()]
        lines.append(f'export {asset}')
        comments = comment_lines(path)
        written.append((material, path, comments, kept))
    if not lines:
        return
    run_magic(export.scene['magic_project'], lines)
    for material, path, comments, kept in written:
        export.report.assets.append(f'material {material.name} written to {path}'
                                    + (f', without its {comments} comment lines' if comments else ''))
        for name in kept:
            export.report.assets.append(f'material {material.name} keeps its {name}, which Blender does not show')


def write_assets(export):
    """Writes back each asset that a person changed in Blender: the points of a changed model over its own file, the
    values of a changed material in its own file, a mesh that no model is behind as a new model of the project, a
    material that no material of the project is behind as a new material of it, and the image that a map reads as a
    new texture of it. Magic takes every model in with one run of its own, so the models are written first and that
    run comes one time. The textures come before the materials, so that a map names the handle of its new texture.

    It runs before the entities are written, so that an entity which draws a new model or uses a new material names
    the new id."""
    with tempfile.TemporaryDirectory(prefix='magic-models-') as folder:
        lines, made = write_models(export, folder)
        if lines:
            run_magic(export.scene['magic_project'], lines)
        read_handles(export, made)
    write_textures(export)
    make_materials(export)
    write_materials(export)
