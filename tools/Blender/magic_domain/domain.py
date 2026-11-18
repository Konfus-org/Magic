# Builds a domain of a Magic project in the scene that Blender has open, and writes the scene back as that domain.
#
# Blender's own data model says what each thing of Magic is, so that a person sees what changed and the export writes
# each change one time:
#
# - A model is one mesh datablock, which every object that draws the model links (the meshes).
# - A material is one Blender material, which every object that uses it links (the materials).
# - A prefab is one collection, built one time, and each place that uses it is an empty whose instance_collection is
#   that collection. The collections stand under one collection that the view layer leaves out, so Blender draws a
#   prefab where its instances stand and nowhere else.
# - A chunk of the world is one collection, under the collection of the domain, which holds the objects of the
#   entities that the chunk's own prefab places. The view distance hides a whole chunk at one time (the chunks).
#
# An import builds the collections of the domain out of the scene, and links them into it in one step at the end.
import copy
import os
import shutil
import time

import bpy
from mathutils import Matrix

from .chunks import BOX, box_of
from .lights import (SUN_NAME, lamp_illuminance, light_values, lit_by, place_point_light, place_sky, place_sun,
                     point_light_as_read, point_light_values, reach_node, sky_strength, sky_world, sun_direction,
                     sun_illuminance, sun_light_values, sun_towards)
from .assets import write_assets
from .cache import read_cost
from .components import (MAPPED, handle_names, held, keep_schema, put, read_schema, same_value, schema_of,
                         write as write_values)
from .meshes import import_model, mesh_fault, mesh_hash, place_renderer
from .project import (Address, chunk_at, chunk_corner, chunk_name, chunk_of_name, comment_lines, domain_folder,
                      domain_prefabs, file_path, find_root, index_assets, model_ids, own_placements, path_of,
                      path_text, prefab_of, read_prefab, record_at, run_magic, run_queries, same_matrix, swap_axes,
                      swap_pose, transform_values, write_prefab)

__all__ = ['Build', 'Domain', 'Report', 'export_domain', 'import_domain', 'set_hour']

# The collection that holds one collection for each prefab.
PREFABS = 'MagicPrefabs'

# The collection that holds the objects of the prefabs of a domain that are no chunk of the world, which no view
# distance hides.
GLOBALS = 'Globals'

# The name that an entity with no name of its own gets, which its empty carries and the export reads back as the
# same.
UNNAMED = 'entity'

# Entities between two writes of the text that the progress bar shows. One part of an import is one object, which is
# shorter than an eye sees, and a count that goes up 500 at a time reads as well as one that counts every entity.
TEXT_EVERY = 500


class Domain:
    """A domain in the scene, and what the hour still needs: the scene it was built in, the collection that holds its
    chunks, the pose in Magic's axes of each marker, and the lamps that the import made of the domain's own lights,
    which set_hour takes away."""

    def __init__(self, scene, collection, markers, suns):
        self.scene = scene
        self.collection = collection
        self.markers = markers
        self.suns = suns


class Report:
    """What an export wrote and what it did not: the prefabs it wrote, how many entities it changed, added and took
    away, each prefab whose own file changed with the count of instances that follow it, each asset that it wrote, and
    a line for each object and each asset that it could not write."""

    def __init__(self, folder):
        self.folder = folder
        self.files = []
        self.changed = 0
        self.added = 0
        self.removed = 0
        self.prefabs = []
        self.assets = []
        self.skipped = []

    def lines(self):
        """The report as the lines that an operator shows and a script prints."""
        return ([f'{len(self.files)} prefabs written to {self.folder}: {self.changed} entities changed, '
                 f'{self.added} added, {self.removed} taken away']
                + [f'prefab {name} changed: {count} instances follow' for name, count in self.prefabs]
                + self.assets
                + [f'skipped {name}: {reason}' for name, reason in self.skipped])


def object_kinds(placement):
    """The objects that an entity puts in the scene: one for what it draws, one for its lamp, one for its directional
    light, one instance of the prefab that it brings in, and an empty for an entity that only stands somewhere. The
    export asks the same question of the files, so that a missing object is an entity taken away and an extra object
    is a copy."""
    kinds = []
    if 'Renderer' in placement.components:
        kinds.append('mesh')
    if 'PointLight' in placement.components:
        kinds.append('lamp')
    if 'DirectionalLight' in placement.components:
        kinds.append('sun')
    if 'prefab' in placement.entity:
        kinds.append('instance')
    # An entity that puts no object of its own in the scene stands in it as an empty, so that a person sees it, moves
    # it, and edits the components that it holds.
    return kinds or ['marker']


def place_marker(collection, name, tags, world):
    """An entity that only stands somewhere, as an empty that holds its name and its tags: a script finds a camera
    pose, or any other point of the domain, by one of the two."""
    o = bpy.data.objects.new(name, None)
    o.empty_display_type = 'ARROWS'
    o.empty_display_size = 0.5
    o.matrix_world = swap_pose(world)
    o['tags'] = list(tags)
    collection.objects.link(o)
    return o


def place_instance(collection, name, prefab, world):
    """One use of a prefab, as an empty that brings the prefab's collection in at the entity's place. Every use of one
    prefab brings in the same collection, so a change inside the collection is a change of the prefab."""
    o = bpy.data.objects.new(name, None)
    o.instance_type = 'COLLECTION'
    o.instance_collection = prefab
    o.empty_display_size = 0.2
    o.matrix_world = swap_pose(world)
    collection.objects.link(o)
    return o


def marker_values(o):
    """An empty of Blender as the name and the tags of an entity: the inverse of place_marker."""
    return o.name, [str(tag) for tag in o.get('tags', [])]


def slot_materials(o):
    """The material of each slot of an object as Magic's Renderer holds it, and a line for a material that is no asset
    of the project: the write of the assets makes a file for each material that a person made, and names the ones
    whose nodes it could not read, so a material with no file is named in the report already."""
    held = [slot.material for slot in o.material_slots if slot.material]
    unknown = [material.name for material in held if 'magic_id' not in material]
    if unknown:
        return None, f'its material {", ".join(unknown)} is no material of the project'
    return [{'id': int(material['magic_id'])} for material in held], None


def renderer_values(o):
    """A mesh object of Blender as Magic's Renderer component: the model that its mesh came from, and the materials
    that its slots hold. It is nothing, with a line, for a slot whose material is no asset of the project."""
    materials, fault = slot_materials(o)
    if fault:
        return None, fault
    return {'model': {'id': int(o.data['magic_model'])}, 'materials': materials}, None


def world_of(o):
    """Where an object of Blender stands, in Magic's axes. A mesh's object carries the mirror of the two hands in its
    points, and an object with no mesh carries it in no place, so the two swap differently."""
    return swap_axes(o.matrix_world) if o.type == 'MESH' else swap_pose(o.matrix_world)


def stamp(o, where, kind):
    """What an object came from, which the export reads back: the record that holds it, and what of that record it is.
    An object that a person copies brings these with it, which makes the copy a new entity of the same record."""
    o['magic_file'] = where.file
    o['magic_path'] = path_text(where.path)
    o['magic_kind'] = kind


def stamped(o):
    """Where an object came from: the record that holds it, and what of that record it is. An object that a person
    made in Blender has neither."""
    if 'magic_file' not in o:
        return None
    return Address(file_path(o['magic_file']), path_of(o['magic_path'])), o['magic_kind']


def record_count(record):
    """How many entities a record of a prefab stands for: itself and its children under it. The parts of an import
    count up to this, which makes its progress true."""
    return 1 + sum(record_count(child) for child in record.get('children', []))


def exclude_collection(scene, collection):
    """Leaves a collection out of the view layers, so that Blender draws its objects where an instance brings them in
    and nowhere else."""
    for layer in scene.view_layers:
        for child in layer.layer_collection.children:
            if child.collection is collection:
                child.exclude = True


class Build:
    """One import, which runs in parts. `run` builds until a deadline and gives back whether the domain is finished:
    the plain import runs it to the end, and the modal operator runs it for part of each tick of a timer, so that
    Blender draws and answers while the domain fills in."""

    def __init__(self, project_path, domain=None, scene=None):
        self.scene = scene or bpy.context.scene
        self.done = 0.0
        self.total = 1.0
        self.stage = 'the assets of the project'
        self.text = self.stage
        self.domain = None
        self.holder = None
        self.collection = None
        self.chunks = {}
        self.models, self.materials, self.meshes, self.prefabs = {}, {}, {}, {}
        self.markers, self.skies, self.suns = {}, [], []
        self.schema, self.names, self.unknown = {}, {}, {}
        self.parts = self.build(project_path, domain)

    def run(self, deadline):
        """Builds parts until the deadline, a time of time.monotonic, and gives back whether the domain is finished."""
        for _ in self.parts:
            if time.monotonic() >= deadline:
                return False
        return True

    @property
    def factor(self):
        """The part of the work that is done, from 0 to 1, which the progress bar shows. It never falls: the work is
        the models and the entities, which the read of the prefab files gives the measure of. The steps before them
        are under a second, and the bar stands at its start while their names show."""
        return min(self.done / self.total, 1.0)

    def drop(self):
        """Takes away what is built so far, for an import that a person stopped. It takes away its own objects alone:
        an object that stood in the scene before the import stays."""
        for collection in list(self.prefabs.values()) + list(self.chunks.values()) + [self.holder, self.collection]:
            if collection is None:
                continue
            for o in list(collection.objects):
                bpy.data.objects.remove(o, do_unlink=True)
            bpy.data.collections.remove(collection, do_unlink=True)

    def show(self, name):
        """Puts the collections of the domain into the scene, under one collection of the name of the domain, which
        draws the whole domain one time in this one part.

        A collection out of the scene is out of the view layer, and Blender draws the scene again after each part of
        the import: an object in the view layer makes that drawing cost about two seconds a part near the end of a
        large domain, and out of it the import pays nothing for it."""
        self.collection = bpy.data.collections.new(name)
        self.scene.collection.children.link(self.collection)
        for chunk in sorted(self.chunks):
            self.collection.children.link(self.chunks[chunk])

    def chunk_collection(self, prefab_name):
        """The collection that holds the objects of one chunk of the world, built the first time a prefab of that
        chunk places an entity. It carries the box that the chunk covers, which the view distance measures to (the
        chunks). Every prefab of the domain that is no chunk shares one collection of globals."""
        box = box_of(prefab_name)
        name = GLOBALS if box is None else os.path.splitext(prefab_name)[0]
        if name not in self.chunks:
            collection = bpy.data.collections.new(name)
            if box is not None:
                collection[BOX] = box
            self.chunks[name] = collection
        return self.chunks[name]

    def prefab_collection(self, record):
        """The collection of the prefab that a record brings in, built the first time a record uses it. A prefab that
        another prefab brings in is a collection instance inside this collection. It gives one part for each object
        that it makes, and gives back the collection."""
        path = prefab_of(self.assets, record)
        if path in self.prefabs:
            return self.prefabs[path]
        collection = bpy.data.collections.new(os.path.basename(path))
        collection['magic_prefab'] = path
        collection['magic_asset'] = str(record['prefab']['id'])
        self.prefabs[path] = collection
        self.holder.children.link(collection)
        for placement in own_placements(path, read_prefab(path), Matrix.Identity(4)):
            yield from self.place(collection, placement)
        return collection

    def place(self, collection, placement):
        """The objects of one entity, in the collection of the domain or of the prefab that holds it. Each object is
        one part of the import, so that the longest part is the one object that costs the most: the first that draws
        a model, which reads the model's file."""
        name = placement.entity.get('name', UNNAMED)
        own = None
        for kind in object_kinds(placement):
            if kind == 'mesh':
                o = place_renderer(collection, self.assets, name, placement.components['Renderer'], placement.world,
                                   self.models, self.materials, self.meshes)
            elif kind == 'lamp':
                o = place_point_light(collection, name, placement.components['PointLight'], placement.world)
            elif kind == 'sun':
                o = place_sun(collection, name, placement.components, placement.world)
                self.suns.append(o)
            elif kind == 'instance':
                prefab = yield from self.prefab_collection(placement.entity)
                o = place_instance(collection, name, prefab, placement.world)
            else:
                self.markers[name] = placement.world
                o = place_marker(collection, name, placement.entity.get('tags', []), placement.world)
            stamp(o, placement.where, kind)
            own = own or o
            yield
        # The first object of an entity carries its components, so that each component stands in one place.
        put(own, placement.entity, self.schema, self.names, self.unknown)
        if 'Sky' in placement.components:
            self.skies.append(placement.components['Sky'])

    def build(self, project_path, domain):
        """The parts of one import: the assets of the project, the prefab files, one model read, and one object of
        the domain. The caller runs them part by part, or to the end."""
        read_prefab.cache_clear()
        root = find_root(project_path)
        self.assets = index_assets(root)
        self.names = handle_names(self.assets, root)
        folder = domain_folder(self.assets, project_path, domain)
        names = domain_prefabs(folder)
        here = os.path.basename(folder)
        yield

        # The component types, the light that lights the domain and the sky's own factor, from one run of Magic:
        # nothing that Magic knows is worked out here. A domain that the project does not open is opened for the run.
        # The scene keeps the component types for the panel and for the export, so a file that opens again runs Magic
        # no more.
        self.stage = self.text = 'the component types and the light of Magic'
        yield
        opened = [f'open {domain}', 'wait loaded'] if domain else []
        printed = run_queries(project_path, [(*opened, 'query components'), ('query DeferredRendering.Sun',),
                                             ('query ProceduralSky.Brightness',)])
        self.schema = read_schema(printed[0])
        keep_schema(self.scene, self.schema)
        self.sun = lit_by(printed[1]['value'])
        self.brightness = printed[2]['value']

        datas = {}
        self.stage = 'the prefab files'
        for name in names:
            datas[name] = read_prefab(os.path.join(folder, name))
            self.text = f'{here} {len(datas):,} of {len(names):,} prefab files'
            yield
        models = model_ids(self.assets, datas.values())
        costs = [read_cost(self.assets[model][0]) for model in models]
        entities = sum(record_count(record) for data in datas.values() for record in data['entities'])
        self.total = sum(costs) + entities

        # Every model before the entities that draw it, so that reading one model is a part of its own under that
        # model's name: the first read builds its mesh from the file, and each later one appends it from the cache.
        for number, (model, cost) in enumerate(zip(models, costs), start=1):
            path = self.assets[model][0]
            self.stage = f'the model {os.path.basename(path)}'
            self.text = f'models {number} of {len(models)}: {os.path.basename(path)}'
            yield
            import_model(path, self.models)
            self.done += cost

        self.holder = bpy.data.collections.new(PREFABS)
        self.scene.collection.children.link(self.holder)
        exclude_collection(self.scene, self.holder)
        self.stage = 'the entities'
        placed = 0
        for name in names:
            path = os.path.join(folder, name)
            collection = self.chunk_collection(name)
            for placement in own_placements(path, datas[name], chunk_corner(name)):
                yield from self.place(collection, placement)
                self.done += 1
                placed += 1
                if placed % TEXT_EVERY == 0:
                    self.text = f'{here} {placed:,} of {entities:,}'
        self.text = f'{here} {placed:,} of {entities:,}'
        self.stage = self.text = 'the domain in the view'
        yield
        self.show(here)

        self.scene['magic_project'] = os.path.abspath(project_path)
        self.scene['magic_folder'] = os.path.abspath(folder)
        # A domain with no Sky entity and no light of its own gets no sky: the import invents none.
        if self.skies and self.suns:
            sky_world(self.scene, self.sun[0], sky_strength(self.sun, self.brightness))
            # The strength that the sky would have at a brightness of 1, by which the export reads a changed
            # brightness back (write_sky). set_hour writes it again for the light of its hour.
            self.scene['magic_sky_at_one'] = sky_strength(self.sun, 1.0)
        self.domain = Domain(self.scene, self.collection, self.markers, self.suns)


def import_domain(project_path, domain=None, scene=None):
    """Builds the domain of a project into a scene, all at once: every mesh that Magic draws with its materials, every
    lamp, one collection for each prefab with an instance of it at each place that uses it, an empty at each marker,
    and the light that the domain's own files hold. The objects of each chunk of the world stand in a collection of
    the chunk's own (the chunks). The scene keeps the project and the domain's folder, so that
    export_domain writes it back where it came from. It draws nothing, and says each stage on the console."""
    build = Build(project_path, domain, scene)
    said = None
    for _ in build.parts:
        if build.stage != said:
            said = build.stage
            print(f'Magic: {said}')
    return build.domain


def set_hour(domain, sun, brightness, altitude):
    """The light of one hour, in the place of the light that the domain's files hold, from what Magic's
    `query DeferredRendering.Sun` prints at that hour (lights.lit_by): the sun or the moon as a lamp, with the sky that
    it lights, so many metres over the ground. It gives back which of the two lights the world. A slice that
    exaggerates the look of an hour changes this one function."""
    scene = domain.scene
    for o in domain.suns + ([bpy.data.objects[SUN_NAME]] if SUN_NAME in bpy.data.objects else []):
        bpy.data.objects.remove(o, do_unlink=True)
    domain.suns = []
    if scene.world:
        bpy.data.worlds.remove(scene.world, do_unlink=True)
    place_sky(scene, sun, altitude, brightness)
    # The strength that the sky would have at a brightness of 1, by which the export reads a changed brightness back.
    scene['magic_sky_at_one'] = sky_strength(sun, 1.0)
    return sun[2]


class Export:
    """One export: the domain's files as they were read, the changes that the scene asks for, and the report."""

    def __init__(self, scene, folder):
        self.scene = scene
        self.source = os.path.abspath(scene['magic_folder'])
        self.folder = os.path.abspath(folder or self.source)
        root = find_root(scene['magic_project'])
        self.assets = index_assets(root)
        self.schema = schema_of(scene)
        # The panel shows a handle as the path of its asset, and a path that a person picked reads back
        # as the id that the prefab holds.
        self.ids = {name: asset for asset, name in handle_names(self.assets, root).items()}
        self.files = {}
        self.touched = set()
        self.worlds = {}
        self.dropped = set()
        self.extra = {}
        self.report = Report(self.folder)

    def data(self, path):
        """A prefab's records, which the export changes."""
        if path not in self.files:
            self.files[path] = read_prefab(path)
        return self.files[path]

    def touch(self, path):
        """Says that a record of a prefab changed, so that the write writes the file instead of copying it."""
        self.touched.add(os.path.abspath(path))

    def source_of(self, path, corner):
        """Each entity that one prefab file places itself, by the object that the import made for it. It keeps the
        world matrix of each record, which a child's place is read from."""
        found = {}
        for placement in own_placements(path, self.data(path), corner):
            self.worlds[placement.where] = placement.world
            for kind in object_kinds(placement):
                found[(placement.where, kind)] = placement
        return found

    def writable(self, path):
        """Whether the export may write a file: the domain's own files always, and a prefab that the domain brings in
        only while the export writes over the domain it read, since such a prefab stands outside the folder and every
        instance of it changes with it."""
        return os.path.dirname(os.path.abspath(path)) == self.source or self.folder == self.source

    def add(self, path, record):
        """Puts a record in a prefab, which the write makes when it is not there yet."""
        self.extra.setdefault(os.path.abspath(path), []).append(record)
        self.report.added += 1

    def skip(self, name, reason):
        """Says in the report that the export could not write one object or one asset, and why. Every line of the
        report that names a thing of Blender goes through here."""
        self.report.skipped.append((name, reason))

    def comments(self, path, name):
        """Says in the report that a file which the export wrote again holds comments, which its records do not and
        the write cannot keep."""
        lines = comment_lines(path)
        if lines:
            self.skip(name, f'{lines} comment lines of the file, which its entities do not hold')


def chunk_destination(export):
    """The file and the corner of a record of the domain: the prefab of the chunk that its place falls in, which holds
    the places of its records from its own corner. An entity that moved over a chunk's border belongs to the other
    chunk's prefab."""
    def destination(world):
        name = chunk_name(chunk_at(world.to_translation()))
        return os.path.join(export.source, name), chunk_corner(name)
    return destination


def file_destination(path):
    """The file and the corner of a record inside a prefab: that prefab, which holds the places of its records from
    its own origin."""
    def destination(world):
        return path, Matrix.Identity(4)
    return destination


def corner_of(export, where, world, destination):
    """The file that a record belongs to now, and the place that the file keeps it from. A child stands where its
    parent puts it, so its corner is the parent's place and its file the parent's file. A prefab of the domain whose
    name is no chunk's keeps its own records, wherever they stand."""
    if len(where.path) > 1:
        return where.file, export.worlds[Address(where.file, where.path[:-1])]
    if chunk_of_name(where.file) is None:
        return where.file, chunk_corner(os.path.basename(where.file))
    return destination(world)


def set_place(record, corner, world):
    """Writes where a record stands into it: its place from the corner that its own file keeps it from."""
    record.setdefault('components', {})['Transform'] = transform_values(corner.inverted_safe() @ world)


def new_record(o):
    """The entity for an object that a person made in Blender, with no place yet, or why the export cannot write it.
    A new model and a new material are assets of the project, which this writes none of."""
    if o.type == 'EMPTY':
        collection = o.instance_collection
        if collection is not None:
            if 'magic_asset' not in collection:
                return None, f'the collection {collection.name} that it brings in is no prefab of the project'
            return {'name': o.name, 'components': {}, 'prefab': {'id': int(collection['magic_asset'])}}, None
        name, tags = marker_values(o)
        record = {'name': name, 'components': {}}
        if tags:
            record['tags'] = tags
        return record, None
    if o.type == 'LIGHT':
        if o.name == SUN_NAME:
            return None, 'it is the lamp of the hour, which the hour makes anew'
        if o.data.type != 'POINT':
            return None, f'Magic has no {o.data.type.lower()} light of its own'
        if reach_node(o.data) is None:
            return None, 'it is a lamp of Blender, which holds no reach of Magic'
        return {'name': o.name, 'components': {'PointLight': point_light_values(o)}}, None
    if o.type == 'MESH':
        fault = mesh_fault(o)
        if fault:
            return None, fault
        if o.data['magic_hash'] != mesh_hash(o.data):
            return None, ('its mesh is a changed model whose file the export could not write, so the points of the '
                          'object and the points of the model are not the same')
        renderer, fault = renderer_values(o)
        if fault:
            return None, fault
        return {'name': o.name, 'components': {'Renderer': renderer}}, None
    return None, f"Blender's {o.type.lower()} is no entity of Magic"


def changed_values(export, where, kind, o):
    """Writes back what a person changes at an entity that is not where it stands: the values that Blender holds its
    own way, and the components that the panel shows. It gives the number of entities that it changed."""
    record = record_at(export.data(where.file), where.path)
    if not (changed_kind(export, record, kind, o) | changed_components(export, record, o)):
        return 0
    export.touch(where.file)
    return 1


def changed_components(export, record, o):
    """Writes the components that the panel shows into an entity's record, and says in the report each value that it
    could not write. An object that carries none of them, such as the second object of one entity, changes nothing.
    It gives whether the record changed."""
    if MAPPED not in o and not held(o):
        return False
    changed, faults = write_values(record, o, export.schema, export.ids)
    for fault in faults:
        export.skip(o.name, fault)
    return changed


def changed_sun(export, record, o):
    """Writes back what a person changes at the sun lamp of a directional light: the way that it points, and the
    colour and the intensity that its illuminance holds. A lamp that is no sun lamp any more holds neither of the two,
    and the export names it and the values in the report. It gives whether the record changed."""
    components = record['components']
    light = components['DirectionalLight']
    # The lamp holds the way it points as an angle of 32 bits, so the direction comes back a ten-thousandth away. A
    # turn by hand is thousands of times wider than that.
    changed = (sun_direction(o) - sun_towards(components)).length >= 1e-4
    if changed:
        components.setdefault('Transform', {}).update(light_values(o))
    if o.data.type != 'SUN':
        export.skip(o.name, f'the colour and the intensity of its directional light, which a {o.data.type.lower()} '
                            'lamp of Blender does not hold: give the object a sun lamp again')
        return changed
    if all(same_value(part, was) for part, was in zip(lamp_illuminance(o), sun_illuminance(light))):
        return changed
    light.update(sun_light_values(o))
    return True


def changed_kind(export, record, kind, o):
    """Writes back what a person changes at the values that Blender holds its own way: a lamp's colour, strength, bulb
    and reach, the materials in the slots of a mesh, a marker's name and its tags, and the way a directional light was
    turned with the light that it gives. It gives whether the record changed."""
    if kind == 'lamp':
        was = point_light_as_read(record['components']['PointLight'])
        changed = {key: value for key, value in point_light_values(o).items() if not same_value(value, was[key])}
        if not changed:
            return False
        record['components']['PointLight'].update(changed)
    elif kind == 'sun':
        return changed_sun(export, record, o)
    elif kind == 'mesh':
        materials, fault = slot_materials(o)
        if fault or materials == record['components']['Renderer'].get('materials', []):
            return False
        record['components']['Renderer']['materials'] = materials
    elif kind == 'marker':
        name, tags = marker_values(o)
        if name == record.get('name', UNNAMED) and tags == [str(tag) for tag in record.get('tags', [])]:
            return False
        record['name'] = name
        if tags or 'tags' in record:
            record['tags'] = tags
    else:
        return False
    return True


def moved_record(export, where, world, destination):
    """Writes where a record stands now. A record of the domain whose place left its chunk moves to the prefab of the
    chunk that it stands in now."""
    file, corner = corner_of(export, where, world, destination)
    record = record_at(export.data(where.file), where.path)
    set_place(record, corner, world)
    export.worlds[where] = world
    export.touch(where.file)
    if os.path.abspath(file) == os.path.abspath(where.file):
        export.report.changed += 1
        return
    export.dropped.add(where)
    export.add(file, copy.deepcopy(record))
    export.report.removed += 1


def copied_record(export, where, o, destination):
    """A new entity for an object that a person copied: the same record, at the place of the copy, with the components
    that the copy carries. A copy of a collection instance is a new instance of that prefab."""
    world = world_of(o)
    file, corner = corner_of(export, where, world, destination)
    record = copy.deepcopy(record_at(export.data(where.file), where.path))
    set_place(record, corner, world)
    changed_components(export, record, o)
    export.add(file, record)


def export_group(export, objects, source, destination):
    """Writes one group of objects back: each entity where its object stands now, what a person changed at it, a new
    entity for each object that a person made or copied, and an entity gone for each object that is gone. A group is
    the domain's own prefabs, or the objects inside one prefab's collection."""
    found = {}
    for o in objects:
        mark = stamped(o)
        if mark is not None and mark in source:
            found.setdefault(mark, []).append(o)
            continue
        record, fault = new_record(o)
        if fault:
            export.skip(o.name, fault)
            continue
        world = world_of(o)
        file, corner = destination(world)
        set_place(record, corner, world)
        changed_components(export, record, o)
        export.add(file, record)

    # The records nearest the top come first, since a child stands where its parent puts it.
    for (where, kind), group in sorted(found.items(), key=lambda item: len(item[0][0].path)):
        placement = source[(where, kind)]
        kept = next((o for o in group if same_matrix(world_of(o), placement.world)), group[0])
        for o in group:
            if o is not kept:
                copied_record(export, where, o, destination)
        export.report.changed += changed_values(export, where, kind, kept)
        world = world_of(kept)
        # Magic reads a directional light's direction alone, so where its lamp stands writes nothing: changed_values
        # writes the way that a person turned it.
        if kind != 'sun' and not same_matrix(world, placement.world):
            moved_record(export, where, world, destination)

    # Each entity that no object stands for any more. An entity that puts more than one object in the scene goes only
    # when every one of them is gone.
    by_record = {}
    for where, kind in source:
        by_record.setdefault(where, []).append((where, kind))
    for where, keys in sorted(by_record.items()):
        gone = [key for key in keys if key not in found]
        if not gone:
            continue
        if len(gone) < len(keys):
            export.skip(source[gone[0]].entity.get('name', UNNAMED),
                        'its entity still puts another object in the scene, so the entity stays')
            continue
        export.dropped.add(where)
        export.touch(where.file)
        export.report.removed += 1


def export_prefabs(export, holder):
    """Writes each prefab whose collection a person changed: one write of that prefab's own file, which every instance
    of it follows. It gives back the objects that live in a prefab's collection, which are no objects of the domain."""
    inside = set()
    for collection in (holder.children if holder else []):
        path = collection.get('magic_prefab')
        if path is None:
            continue
        inside.update(collection.objects)
        if not export.writable(path):
            continue
        before = report_state(export.report)
        export_group(export, list(collection.objects), export.source_of(path, Matrix.Identity(4)),
                     file_destination(path))
        if report_state(export.report) != before:
            instances = sum(1 for o in export.scene.collection.all_objects if o.instance_collection is collection)
            export.report.prefabs.append((os.path.basename(path), instances))
    return inside


def report_state(report):
    """The counts of a report, by which the export sees that a group of objects asked for a change."""
    return report.changed, report.added, report.removed, len(report.skipped)


def write_sky(export):
    """The brightness of the domain's sky, when a person changed the strength of Blender's."""
    at_one = export.scene.get('magic_sky_at_one', 0)
    if not at_one or export.scene.world is None:
        return
    brightness = export.scene.world.node_tree.nodes['Background'].inputs[1].default_value / at_one
    for name in domain_prefabs(export.source):
        for record in export.data(os.path.join(export.source, name))['entities']:
            sky = record.get('components', {}).get('Sky')
            if sky is not None and not same_value(brightness, sky.get('brightness', 1.0)):
                sky['brightness'] = brightness
                export.touch(os.path.join(export.source, name))
                export.report.changed += 1


def kept_records(records, file, path, dropped):
    """The records of a prefab that the export keeps, each record that is gone taken out of its children too."""
    out = []
    for index, record in enumerate(records):
        here = path + (index,)
        if Address(file, here) in dropped:
            continue
        if 'children' in record:
            record['children'] = kept_records(record['children'], file, here, dropped)
        out.append(record)
    return out


def write_files(export):
    """Writes every prefab of the domain, and each prefab outside it that a change reached. A prefab that nothing
    changed is copied, which keeps the comments that the records do not hold."""
    os.makedirs(export.folder, exist_ok=True)
    made = []
    if export.folder != export.source:
        # The .domain file and its sidecar, which make the folder a domain that Magic opens.
        for name in os.listdir(export.source):
            if not name.endswith(('.prefab', '.prefab.meta')):
                shutil.copyfile(os.path.join(export.source, name), os.path.join(export.folder, name))
    own = {os.path.join(export.source, name) for name in domain_prefabs(export.source)}
    for path in sorted(own | {path for path in export.extra if os.path.dirname(path) == export.source}):
        name = os.path.basename(path)
        out = os.path.join(export.folder, name)
        source = os.path.isfile(path)
        if source and path not in export.touched and path not in export.extra:
            if out != path:
                shutil.copyfile(path, out)
                shutil.copyfile(path + '.meta', out + '.meta')
            continue
        data = export.data(path) if source else {'entities': []}
        data['entities'] = kept_records(data['entities'], path, (), export.dropped) + export.extra.get(path, [])
        if source:
            export.comments(path, name)
        write_prefab(out, data)
        export.report.files.append(name)
        if source and out != path:
            shutil.copyfile(path + '.meta', out + '.meta')
        elif not os.path.isfile(out + '.meta'):
            made.append(out)
    # Magic gives each new prefab its .meta, because it alone knows which id is free, and the file stands in the
    # project already, so Magic writes the .meta where it is and copies nothing.
    if made:
        run_magic(export.scene['magic_project'], ['import "%s"' % path for path in made])
    for path in sorted(export.touched | set(export.extra)):
        if os.path.dirname(path) == export.source:
            continue
        name = os.path.relpath(path, os.path.dirname(export.source))
        data = export.data(path)
        data['entities'] = kept_records(data['entities'], path, (), export.dropped) + export.extra.get(path, [])
        export.comments(path, name)
        write_prefab(path, data)
        export.report.files.append(name)


def export_domain(scene=None, folder=None):
    """Writes the scene back as the domain's prefabs: every entity where it stands now, an entity gone for each object
    that a person deleted, a new entity for each copy and each empty, and the values that Blender can change. A change
    inside a prefab's collection is one change of that prefab's own file, which every instance of it follows. A model
    whose points moved, a material whose values changed and a mesh that is a new model are written as assets of the
    project first (write_assets). It gives back a Report of what it wrote, and of each object and each asset that it
    could not write.

    The folder is the domain's own folder when the caller gives none, which writes over the domain that was read."""
    scene = scene or bpy.context.scene
    if 'magic_folder' not in scene:
        raise ValueError('This scene came from no import of a Magic domain, so there is no domain to write back.')
    read_prefab.cache_clear()
    export = Export(scene, folder)
    # The assets first: an entity that draws a new model names the id that the write of that model gave it.
    write_assets(export)
    inside = export_prefabs(export, bpy.data.collections.get(PREFABS))
    source = {}
    for name in domain_prefabs(export.source):
        source.update(export.source_of(os.path.join(export.source, name), chunk_corner(name)))
    export_group(export, [o for o in scene.collection.all_objects if o not in inside], source,
                 chunk_destination(export))
    write_sky(export)
    write_files(export)
    return export.report
