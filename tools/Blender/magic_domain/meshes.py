# Magic's models as Blender meshes, and the objects that draw them. One model is one mesh datablock, made one time,
# which every object that draws the model links: a person who edits the points of one tree edits every tree of that
# model, and the export sees one changed model.
import array
import os
import zlib

import bmesh
import bpy

from .cache import read_mesh, write_mesh
from .materials import make_material
from .project import SWAP, swap_axes

# Metres to a tile of the texture coordinates that Magic lays over a mesh that brings none: the model importer's
# PlanarUvMetres.
PLANAR_UV_METRES = 4


def add_planar_uvs(piece):
    """Lays over a piece, whose points are already in Magic's axes, the texture coordinates that Magic gives a mesh
    that brings none: x and z give the uv, a tile to every PlanarUvMetres (the model importer). Magic reads a texture
    with v down the image and Blender reads it with v up, so the v here runs the other way."""
    layer = piece.loops.layers.uv.new('UVMap')
    for face in piece.faces:
        for loop in face.loops:
            point = loop.vert.co
            loop[layer].uv = (point.x / PLANAR_UV_METRES, -point.z / PLANAR_UV_METRES)


def mesh_hash(mesh):
    """A number over a mesh's points, by which the export sees that a person changed them. A property of Blender holds
    a whole number of 32 bits, so it is text."""
    points = array.array('f', bytes(len(mesh.vertices) * 12))
    mesh.vertices.foreach_get('co', points)
    return f'{zlib.crc32(points.tobytes()):08x}'


def face_materials(mesh):
    """The slot of each face of a mesh, read in one call. A loop over the faces themselves costs a second for each
    hundred thousand faces, which a model of a tree has."""
    indices = array.array('i', bytes(len(mesh.polygons) * 4))
    mesh.polygons.foreach_get('material_index', indices)
    return indices


def first_uses(indices):
    """The slots that a mesh uses, in the order that its faces first use each one."""
    order = []
    seen = set()
    for index in indices:
        if index not in seen:
            seen.add(index)
            order.append(index)
    return order


def slot_names(o):
    """The name of the material in each slot of an object, and one empty name for an object with no slot: a face of
    such an object draws the material that the model names nowhere."""
    return [slot.material.name if slot.material else '' for slot in o.material_slots] or ['']


def import_model(path, cache):
    """The model's mesh in Magic's model axes, with one empty material slot for each material that the model names, in
    the order Assimp numbers them: the order in which the file's faces first use each material. The materials of a
    renderer go on its object, so this mesh holds none.

    A model is read from its file one time on a machine: the first read keeps the mesh as Blender data (the cache),
    and every later import appends it from there."""
    if path in cache:
        return cache[path]
    mesh = read_mesh(path)
    if mesh is None:
        mesh = build_model(path)
        write_mesh(path, mesh)
    cache[path] = (mesh, len(mesh.materials))
    return cache[path]


def build_model(path):
    """The mesh of a model, built from the pieces that Blender's FBX importer reads: every piece joined into one mesh,
    in Magic's model axes, with one empty slot for each material that the model names."""
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path)
    imported = sorted((o for o in bpy.data.objects if o not in before), key=lambda o: o.name)
    meshes = [o for o in imported if o.type == 'MESH']
    faces = {o: face_materials(o.data) for o in meshes}
    order = []
    for o in meshes:
        names = slot_names(o)
        for index in first_uses(faces[o]):
            name = names[index] if index < len(names) else ''
            if name not in order:
                order.append(name)
    combined = bpy.data.meshes.new(os.path.basename(path))
    target = bmesh.new()
    for o in meshes:
        # One name for every piece's texture coordinates, so that the pieces join into one set.
        if o.data.uv_layers:
            o.data.uv_layers[0].name = 'UVMap'
        # The slots of the pieces hold one material each, and the joined mesh holds them in one order, so each face
        # takes the place of its material in that order. One call for the whole piece: a loop over its faces is
        # minutes over a model of a hundred thousand faces.
        names = slot_names(o)
        slots = max(max(faces[o], default=0) + 1, len(names), 1)
        table = [order.index(names[index] if index < len(names) else '') for index in range(slots)]
        o.data.polygons.foreach_set('material_index', array.array('i', [table[i] for i in faces[o]]))
        piece = bmesh.new()
        piece.from_mesh(o.data)
        piece.transform(SWAP @ o.matrix_world)
        if len(piece.loops.layers.uv) == 0:
            add_planar_uvs(piece)
        scratch = bpy.data.meshes.new('piece')
        piece.to_mesh(scratch)
        piece.free()
        target.from_mesh(scratch)
        bpy.data.meshes.remove(scratch)
    target.to_mesh(combined)
    target.free()
    for o in imported:
        bpy.data.objects.remove(o)
    for _ in order:
        combined.materials.append(None)
    return combined


def make_mesh(assets, renderer, models, meshes):
    """The mesh that a renderer draws: the model's own mesh, made one time and linked by every object that draws the
    model. The materials go on the object, so two entities that draw one model with different materials share these
    points. A slot that no material fills draws nothing in Magic, so a model drawn with fewer materials than it has
    slots is a mesh of its own, with those faces gone. The mesh keeps the model it came from and a hash of its points,
    by which the export tells a model of Magic from a new one and sees a model that changed (ids are text: a property
    of Blender holds a whole number of 32 bits alone)."""
    model_id = renderer['model']['id']
    source, slots = import_model(assets[model_id][0], models)
    filled = min(len(renderer.get('materials', [])), slots)
    key = (model_id, filled)
    if key in meshes:
        return meshes[key]
    mesh = source if filled == slots else source.copy()
    if filled < slots:
        b = bmesh.new()
        b.from_mesh(mesh)
        bmesh.ops.delete(b, geom=[f for f in b.faces if f.material_index >= filled], context='FACES')
        b.to_mesh(mesh)
        b.free()
        while len(mesh.materials) > filled:
            mesh.materials.pop()
    mesh['magic_model'] = str(model_id)
    # How many materials the model itself names, by which the export tells the model's own mesh from a mesh of fewer
    # materials, whose faces of the others are gone.
    mesh['magic_slots'] = str(slots)
    mesh['magic_hash'] = mesh_hash(mesh)
    meshes[key] = mesh
    return mesh


def place_renderer(collection, assets, name, renderer, world, models, materials, meshes):
    # Magic reads the renderer's isShadowless nowhere, so every mesh casts a shadow here, as every mesh does there.
    o = bpy.data.objects.new(name, make_mesh(assets, renderer, models, meshes))
    for slot, material in zip(o.material_slots, renderer.get('materials', [])):
        # The material on the object and not on the mesh, so that the mesh stays the model's own.
        slot.link = 'OBJECT'
        slot.material = make_material(assets, material['id'], materials)
    # The swap back to Blender's axes mirrors, and the mirror in the mesh undoes it.
    o.matrix_world = swap_axes(world)
    collection.objects.link(o)
    return o


def model_fault(mesh):
    """Why the add-on cannot write a mesh back as the model of a file, or nothing when it can. A model keeps one
    material for each slot of its mesh, in the order that its faces first use them (import_model), so a mesh that
    reads back as another set of slots is one that the add-on does not write."""
    slots = len(mesh.materials)
    if slots == 0:
        return 'it has no material slot, and a model whose file names no material draws nothing in Magic'
    if 'magic_slots' in mesh and int(mesh['magic_slots']) != slots:
        return (f"it draws {slots} of the {mesh['magic_slots']} materials of its model, so a write of it would take "
                'the faces of the others away')
    used = list(first_uses(face_materials(mesh)))
    if used != list(range(slots)):
        return (f'its faces use the slots {used} of the {slots} that it has, and a model names the materials that its '
                'faces use, in that order')
    return None


def mesh_fault(o):
    """Why the export cannot write what an object draws, or nothing when it can: a mesh that Magic has no model for,
    and a material that no asset of the project is behind. A mesh whose points a person changed is a changed model,
    which the export reports of the model itself."""
    mesh = o.data
    if 'magic_model' not in mesh:
        return 'its mesh is no model of Magic'
    for slot in o.material_slots:
        if slot.material is not None and 'magic_id' not in slot.material:
            return f'its material {slot.material.name} is no material of the project'
    return None
