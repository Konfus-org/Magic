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

from .materials import material_hash, material_values
from .meshes import mesh_hash, model_fault
from .project import SWAP, read_jsonc, rounded, run_magic

# The format that a new model and a replaced model are written in: Magic's model importer reads it, and Blender writes
# it with an exporter of its own.
MODEL_FORMAT = '.fbx'


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
    """The folder of the project that its models stand in, under the project, by the models that the domain draws
    already. A domain whose models stand outside the project keeps Models."""
    for mesh in bpy.data.meshes:
        if 'magic_model' not in mesh:
            continue
        held = export.assets.get(int(mesh['magic_model']))
        if held is None:
            continue
        folder = os.path.relpath(os.path.dirname(held[0]), project_folder(export))
        if not folder.startswith('..'):
            return folder
    return 'Models'


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
            export.report.skipped.append((names, f'{len(meshes)} meshes of the scene changed the one model {model}, '
                                                 'so the model has no one set of points to write'))
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
            export.report.skipped.append((mesh.name, f'its points changed, and {fault}'))
            continue
        held = export.assets.get(model)
        if held is None:
            export.report.skipped.append((mesh.name, f'its points changed, and the model {model} that it came from is '
                                                     'no asset of this project'))
            continue
        path = held[0]
        if os.path.splitext(path)[1].lower() != MODEL_FORMAT:
            export.report.skipped.append((mesh.name, f'its points changed, and its file {os.path.basename(path)} is '
                                                     f'no {MODEL_FORMAT}, which is the one format that this writes'))
            continue
        into = os.path.relpath(os.path.dirname(path), project_folder(export))
        if into.startswith('..'):
            export.report.skipped.append((mesh.name, f'its points changed, and its file {path} stands outside the '
                                                     'project, which Magic imports a model into alone'))
            continue
        write_model_file(mesh, os.path.join(folder, os.path.basename(path)))
        lines.append(f'import "{os.path.join(folder, os.path.basename(path))}" "{into}" --replace')
        export.report.assets.append(f'model {mesh.name} written to {path}')
        made[mesh.name] = mesh

    into = models_folder(export)
    for name, mesh in new_meshes(export).items():
        fault = model_fault(mesh)
        if fault:
            export.report.skipped.append((name, f'it is a new model, and {fault}'))
            continue
        file = os.path.join(project_folder(export), into, name + MODEL_FORMAT)
        if os.path.exists(file):
            export.report.skipped.append((name, f'it is a new model, and {file} is a file of the project already: '
                                                f'name the mesh {name} of the scene another way'))
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


def write_values(path, data):
    """Writes an asset that holds JSON values, as a material does, with the numbers cut to the places that a file of
    the project keeps."""
    with open(path, 'w', encoding='utf-8', newline='\n') as file:
        json.dump(rounded(data), file, indent=4, ensure_ascii=False)
        file.write('\n')


def write_materials(export):
    """Writes each material that a person changed back into its own file, which keeps its .meta and so its id. A value
    of the Blender material that Magic's material has no place for is a line of the report for that material, and that
    material is not written at all."""
    for material in bpy.data.materials:
        if 'magic_hash' not in material or material['magic_hash'] == material_hash(material):
            continue
        held = export.assets.get(int(material['magic_id']))
        if held is None:
            export.report.skipped.append((material.name, f'it changed, and the material {material["magic_id"]} that '
                                                         'it came from is no asset of this project'))
            continue
        path = held[0]
        data = read_jsonc(path)
        values, faults = material_values(material, data.get('params', {}))
        if faults:
            for fault in faults:
                export.report.skipped.append((material.name, f'it changed, and the export cannot read {fault}'))
            continue
        comments = sum(1 for line in open(path, encoding='utf-8-sig') if line.lstrip().startswith('//'))
        data['params'] = values
        write_values(path, data)
        material['magic_hash'] = material_hash(material)
        export.report.assets.append(f'material {material.name} written to {path}'
                                    + (f', without its {comments} comment lines' if comments else ''))


def write_assets(export):
    """Writes back each asset that a person changed in Blender: the points of a changed model over its own file, the
    values of a changed material in its own file, and a mesh that no model is behind as a new model of the project.
    Magic takes every model in with one run of its own, so the models are written first and that run comes one time.

    It runs before the entities are written, so that an entity which draws a new model names the model's new id."""
    with tempfile.TemporaryDirectory(prefix='magic-models-') as folder:
        lines, made = write_models(export, folder)
        if lines:
            run_magic(export.scene['magic_project'], lines)
        read_handles(export, made)
    write_materials(export)
