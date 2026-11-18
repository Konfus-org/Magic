# Keeps each model that an import read as Blender data, so that the next import appends its mesh in the place of a
# read of the model's file through Blender's FBX importer. That read is one call of Blender's own importer, which
# blocks for seconds over a big model.
#
# One entry is one .blend file that holds one mesh. Its key is the content of the model's file and the version of the
# mesh that the add-on builds from it: a model that changed, or a change of that build, makes a new key, and no older
# entry is read again. The folder holds data that is made again, so a person deletes it at any time.
import hashlib
import os

import bpy

# The version of the mesh that meshes.py builds from a model's file. Raise it with each change of that build, so that
# no entry of an older build is read.
MESH_VERSION = 1

# Bytes of a model's file that one read of the key takes.
BLOCK = 1 << 20

# What one megabyte of a model's file costs to read, in entities placed: the build of its mesh from the file, and the
# append of its mesh from an entry. An import weighs the parts of its progress by these, so that its bar moves at an
# even speed over a step that is not an entity. Measured over the models of the cabin domain, where one entity placed
# is 0.21 ms.
BUILT_COST = 3000
APPENDED_COST = 120


def folder():
    """The folder of the entries: the add-on's own folder under the cache folder that the engine keeps for this user
    (Files/Caches.cs). The key holds the model's content, so the entries of every project stand together."""
    local = os.environ.get('LOCALAPPDATA') or os.path.join(os.path.expanduser('~'), '.cache')
    return os.path.join(local, 'Magic', 'Cache', 'Blender')


def entry_of(path):
    """The entry that holds the mesh of a model's file: the model's name, which a person reads, and the key of its
    content and of the build, which says that the entry is still the model's."""
    key = hashlib.sha1(MESH_VERSION.to_bytes(4, 'little'))
    with open(path, 'rb') as file:
        for block in iter(lambda: file.read(BLOCK), b''):
            key.update(block)
    name = os.path.splitext(os.path.basename(path))[0]
    return os.path.join(folder(), f'{name}-{key.hexdigest()[:16]}.blend')


def read_mesh(path):
    """The mesh of a model's file from its entry, or nothing while no entry holds it."""
    entry = entry_of(path)
    if not os.path.isfile(entry):
        return None
    with bpy.data.libraries.load(entry) as (source, target):
        target.meshes = list(source.meshes)
    return target.meshes[0]


def write_mesh(path, mesh):
    """Keeps a mesh as the entry of the model's file that it was built from. The write goes to a name of its own and
    takes the entry's place in one step, so that a run which ends in the middle leaves no half entry behind."""
    entry = entry_of(path)
    os.makedirs(os.path.dirname(entry), exist_ok=True)
    half = f'{entry}.{os.getpid()}'
    bpy.data.libraries.write(half, {mesh}, compress=True)
    os.replace(half, entry)


def read_cost(path):
    """What reading one model costs, in entities placed. A model whose entry is there costs a small part of one that
    the add-on must build from the file, so an import that weighs its parts by this holds its speed either way."""
    megabytes = os.path.getsize(path) / BLOCK
    return megabytes * (APPENDED_COST if os.path.isfile(entry_of(path)) else BUILT_COST)
