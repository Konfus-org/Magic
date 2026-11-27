# Magic's materials as Blender materials: the colour, the maps, the emissive light and the glass.
#
# One table holds both directions. Each row of it says where one value of a Magic material stands in Blender: the
# import walks the table to build the nodes, and the export walks it to read the values back. A value stands in one
# place only, so the two directions cannot drift apart.
import os
from collections import namedtuple

import bpy

from .components import reads_as, same_value
from .project import LUMENS_PER_WATT, read_jsonc

# The nodes that an import makes. A node of another kind is one that a person added, and Magic's material holds no
# value for it, so the export names it instead of writing the material.
BUILT_NODES = {'ShaderNodeBsdfPrincipled', 'ShaderNodeOutputMaterial', 'ShaderNodeTexImage', 'ShaderNodeMix',
               'ShaderNodeSeparateColor', 'ShaderNodeMath', 'ShaderNodeNormalMap', 'ShaderNodeBsdfTransparent',
               'ShaderNodeMixShader', 'ShaderNodeMapping', 'ShaderNodeTexCoord'}

# The node that a material of another tool holds before an image to map the UVs of a mesh, which an import makes none
# of: each input of it, with the value that it holds while it maps nothing. Magic reads every map at the UVs of the
# mesh, so such a node that changes nothing is a node that no value of the material stands behind, and a node that
# changes the UVs is a value that Magic has no place for (plain_faults). The coordinates that feed it hold no value at
# all and stand in BUILT_NODES alone.
PLAIN_NODES = {'ShaderNodeMapping': (('Location', (0, 0, 0)), ('Rotation', (0, 0, 0)), ('Scale', (1, 1, 1)))}

# The member of a material that holds its params, which a row of the table names before the param's own name.
PARAMS = 'params.'

# One value of a material of Magic in Blender.
#
# `name` is the member of the material that holds the value, as the material's file and Magic's `set` name it, and
# nothing for a value of Blender that Magic has no place for. `kind` says what the value is. `sockets` names the input
# of the Principled BSDF that each part of the value stands behind, with the output that feeds that input for a map.
# `behind` names the nodes from that input back to the one which holds the value, and `held` the input of the last of
# them: the colour of a tint in its A, the factor of a map in the second input of its scale, and the image of a map in
# the input of the node that reads it. `absent` is the value that the shader starts with, which a material whose file
# leaves the value out has. `space` is the colour space of a map's image in Blender, and `usage` what the image holds
# for Magic, which the texture's `.meta` names and which picks the colour space that Magic reads it in.
Row = namedtuple('Row', 'name kind sockets behind held absent space usage', defaults=(None,))

# The maps of the standard material, which an import builds before the factors: a factor whose map feeds its input
# stands in the node that the map put there, and the rows of the factors find it.
COLOUR_MAP = Row('params.colorMap', 'map', (('Base Color', 'Result'),), ('ShaderNodeMix',), 'B', None, 'sRGB',
                 'color')
EMISSIVE_MAP = Row('params.emissiveMap', 'map', (('Emission Color', 'Result'),), ('ShaderNodeMix',), 'B', None,
                   'sRGB', 'color')
ORM_MAP = Row('params.ormMap', 'map', (('Roughness', 'Green'), ('Metallic', 'Blue')),
              ('ShaderNodeMath', 'ShaderNodeSeparateColor'), 0, None, 'Non-Color', 'mask')
MAPS = (
    COLOUR_MAP,
    ORM_MAP,
    Row('params.normalMap', 'map', (('Normal', 'Normal'),), ('ShaderNodeNormalMap',), 'Color', None, 'Non-Color',
        'normal'),
    EMISSIVE_MAP,
)

# The values that Blender holds and a material of Magic has no place for: the export names one that a person changed,
# instead of writing a material that lost it. A row with no node behind it reads the input of the BSDF itself.
UNMAPPED = (
    Row(None, 'unmapped', (('Normal', None),), ('ShaderNodeNormalMap',), 'Strength', 1, None),
    Row(None, 'unmapped', (('Transmission Weight', None),), None, None, 0, None),
    Row(None, 'unmapped', (('Subsurface Weight', None),), None, None, 0, None),
    Row(None, 'unmapped', (('Coat Weight', None),), None, None, 0, None),
    Row(None, 'unmapped', (('Sheen Weight', None),), None, None, 0, None),
)

# How a material is drawn, which every material of Magic holds: its transparency as the blend method of Blender, and
# its sides as the backface culling that Blender's own panel shows.
SIDES = (
    Row('transparency', 'choice', None, None, None, 'opaque', None),
    Row('isDoubleSided', 'switch', None, None, None, False, None),
)

# The table of Magic's standard material: each param of its shader, and how the material is drawn.
STANDARD = MAPS + (
    Row('params.color', 'colour', (('Base Color', None),), ('ShaderNodeMix',), 'A',
        {'r': 1, 'g': 1, 'b': 1, 'a': 1}, None),
    Row('params.roughness', 'factor', (('Roughness', None),), ('ShaderNodeMath',), 1, {'x': 1}, None),
    Row('params.metalness', 'factor', (('Metallic', None),), ('ShaderNodeMath',), 1, {'x': 0}, None),
    Row('params.emissive', 'light', (('Emission Color', None),), ('ShaderNodeMix',), 'A',
        {'x': 0, 'y': 0, 'z': 0}, None),
) + UNMAPPED + SIDES

# The value of the standard material that stands behind each input of the Principled BSDF, by that input: a map that
# the export builds puts a node between the two, and the value then stands in that node (factor_socket).
BEHIND = {row.sockets[0][0]: row for row in STANDARD if row.kind in ('factor', 'colour', 'light')}

# The value that changes nothing, by the kind of the value: an image straight in an input of the Principled BSDF
# drives that input alone in Blender, so the value behind the map that the export builds of it is this one.
ONE = {'factor': 1, 'colour': (1, 1, 1, 1), 'light': (1, 1, 1, 1)}

# The map of Magic that one image straight in an input of the Principled BSDF is, by that input: the shape of a
# material that another tool made, which holds the image where an import holds the node of the map.
STRAIGHT = {'Base Color': COLOUR_MAP, 'Emission Color': EMISSIVE_MAP}

# What a material of another tool holds its roughness and its metalness as: one image for each, straight in the input
# of the Principled BSDF. Magic reads both from one ORM map, and this names the channel of that map which its shader
# reads each one from (Material/Standard.slang: the occlusion in red, the roughness in green, the metalness in blue).
PACKED = {'Roughness': 1, 'Metallic': 2}

# The channels of one pixel of an image of Blender, which holds them as one row of floats.
CHANNELS = 4

# The table of Magic's glass, whose shader holds a pane of its own: its colour and its roughness stand where the
# standard material's stand, and its clarity is how much of the light behind it the pane lets through.
GLASS_COLOUR = Row('params.color', 'pane', (('Base Color', None),), None, None,
                   {'r': 1, 'g': 1, 'b': 1, 'a': 1}, None)
GLASS_CLARITY = Row('params.clarity', 'clarity', None, None, None, {'x': 1}, None)
GLASS = (
    GLASS_COLOUR,
    Row('params.roughness', 'factor', (('Roughness', None),), None, None, {'x': 1}, None),
    GLASS_CLARITY,
) + UNMAPPED + SIDES

# Magic's transparency as the render method of a material in Blender, which Blender's own panel shows. Blender has
# two methods for Magic's three transparencies: a material that is masked is drawn as a dithered one, so a read of
# Dithered keeps the transparency that the file holds and only Blended and Dithered tell each other apart.
RENDER_OF = {'opaque': 'DITHERED', 'masked': 'DITHERED', 'transparent': 'BLENDED'}

# What both directions of a table work on: the Blender material and its Principled BSDF, the project's assets by id,
# each asset's file as the id of that asset, and the Magic material's file as Magic wrote it.
Side = namedtuple('Side', 'material bsdf assets textures data')


def set_up(node):
    """What a node between a map and the Principled BSDF needs beyond its kind: a mix multiplies a colour by the map,
    and a maths node multiplies a factor into one channel of it."""
    if node.bl_idname == 'ShaderNodeMix':
        node.data_type = 'RGBA'
        node.blend_type = 'MULTIPLY'
        node.inputs['Factor'].default_value = 1
    elif node.bl_idname == 'ShaderNodeMath':
        node.operation = 'MULTIPLY'


def rows_of(assets, data):
    """The table of the material shader that a material's file names: Magic's glass holds a pane of its own, and every
    other shader is read as the standard material."""
    shader = assets.get(data.get('shader', {}).get('id'), ('',))[0]
    return GLASS if os.path.basename(shader).startswith('Glass') else STANDARD


def asset_paths(assets):
    """Each asset's file as the id of that asset, by which an image of Blender finds the texture that it reads."""
    return {os.path.normcase(os.path.abspath(path)): held for held, (path, meta) in assets.items()}


def image_file(image):
    """One spelling of the file that an image of Blender reads, which asset_paths names a texture by, and nothing for
    an image that no file holds yet: one that a person generated or packed."""
    path = bpy.path.abspath(image.filepath) if image.filepath else ''
    return os.path.normcase(os.path.abspath(path)) if path else None


def side_of(material, assets, data):
    """What the two directions work on, for one Blender material and the file of the Magic material behind it. A
    material that a person made in Blender takes the file that Magic made for it."""
    return Side(material, material.node_tree.nodes.get('Principled BSDF'), assets, asset_paths(assets), data)


def file_value(side, row):
    """The value that a material's file holds for one row of the table, or the value that the shader starts with for a
    row that the file leaves out. A map that the file leaves out is no map at all.

    A param that the file holds leaves out each part that is 0, so the parts of the row's own value say which parts to
    read: a colour of black is one alpha in the file and a black colour here. A map's param that holds no texture is
    no map."""
    if row.name is None:
        return row.absent
    if not row.name.startswith(PARAMS):
        return side.data.get(row.name, row.absent)
    held = side.data.get('params', {}).get(row.name[len(PARAMS):])
    if row.kind == 'map':
        # A map is the texture that its param names, and a param with no texture at all is no map: that is how a
        # material which lost its map is written, since a param is written over and never taken out.
        return held if (held or {}).get('texture', {}).get('id') else None
    if held is None:
        return row.absent
    return {part: held.get(part, 0) for part in row.absent}


def walk_back(socket, kinds):
    """The node that stands behind an input of the Principled BSDF, through the nodes that a map put between them, and
    a line when another node stands there. Each node between them reads its own first input."""
    node = socket.links[0].from_node
    for at, kind in enumerate(kinds):
        if node.bl_idname != kind:
            return None, (f'what {node.name} ({node.bl_idname}) gives its {socket.name}, which is no value of a '
                          'material of Magic')
        if at + 1 == len(kinds):
            return node, None
        if not node.inputs[0].is_linked:
            return None, f'the {node.name} of its {socket.name}, which reads nothing'
        node = node.inputs[0].links[0].from_node
    return node, None


def factor_socket(side, row):
    """The socket that holds one factor of a material: the input of the Principled BSDF itself while nothing feeds
    that input, and the input of the node that a map put behind it once one does. Both directions go through this, so
    a factor stands in one place only."""
    socket = side.bsdf.inputs[row.sockets[0][0]]
    if not socket.is_linked or row.behind is None:
        return socket, None
    node, fault = walk_back(socket, row.behind)
    return (None, fault) if fault else (node.inputs[row.held], None)


def read_space(image, space):
    """Puts one image in the colour space that the kind of its map needs. An image that reads a file takes the space
    at any time, since its file holds its pixels; an image that a person generated or packed holds its pixels itself,
    and Blender gives such an image the pixels of its own kind again at each change of its colour space, so that image
    takes the space once it has a file of its own (save_image)."""
    if image.source == 'FILE':
        image.colorspace_settings.name = space


def map_image(side, row, texture_id):
    """The image of one map of a material: the texture asset's own file, read one time and in the colour space that
    the kind of the map needs."""
    image = bpy.data.images.load(side.assets[texture_id][0], check_existing=True)
    read_space(image, row.space)
    return image


def image_node(side, image):
    """The node that reads one image in a material, which holds the image one time."""
    node = side.material.node_tree.nodes.new('ShaderNodeTexImage')
    node.image = image
    return node


def build_map(side, row, node):
    """The nodes of one map, for the node that reads its image: one node between that node and each input of the
    Principled BSDF that the map feeds. An input that the image fed straight takes the chain in its place."""
    tree = side.material.node_tree
    reader = tree.nodes.new(row.behind[-1])
    set_up(reader)
    tree.links.new(node.outputs['Color'], reader.inputs[row.held])
    for socket, output in row.sockets:
        node, out = reader, output
        for kind in reversed(row.behind[:-1]):
            made = tree.nodes.new(kind)
            set_up(made)
            tree.links.new(node.outputs[out], made.inputs[0])
            node, out = made, 0
        tree.links.new(node.outputs[out], side.bsdf.inputs[socket])


def build_glass(side):
    """Magic's glass over the lit picture: its clarity of the light behind it straight through, tinted by its colour
    and unbent, and the rest a lit pane. Camera, diffuse and shadow rays all get the same mix, which is what Magic's
    blend gives the picture and what its shadow rays read from the pane (AverageMaterials.slang: the colour times one
    less the alpha)."""
    tree = side.material.node_tree
    colour = file_value(side, GLASS_COLOUR)
    alpha = colour.get('a', 1)
    clear = tree.nodes.new('ShaderNodeBsdfTransparent')
    clear.inputs['Color'].default_value = tuple(colour.get(key, 1) for key in 'rgb') + (1,)
    mix = tree.nodes.new('ShaderNodeMixShader')
    mix.inputs[0].default_value = file_value(side, GLASS_CLARITY).get('x', 1) * alpha
    tree.links.new(side.bsdf.outputs[0], mix.inputs[1])
    tree.links.new(clear.outputs[0], mix.inputs[2])
    tree.links.new(mix.outputs[0], tree.nodes['Material Output'].inputs[0])


def write_row(side, row, value):
    """Puts one value of a Magic material into the nodes of its Blender material. It is the one direction of the
    table, which read_row reads back."""
    # The clear part of a glass holds its clarity (build_glass), and a value that Magic has no place for is one of
    # Blender's own, which no import writes.
    if row.kind in ('unmapped', 'clarity'):
        return
    if row.kind == 'choice':
        side.material.surface_render_method = RENDER_OF[str(value).lower()]
    elif row.kind == 'switch':
        side.material.use_backface_culling = not value
    elif row.kind == 'map':
        if value is not None:
            build_map(side, row, image_node(side, map_image(side, row, value['texture']['id'])))
    elif row.kind == 'factor':
        factor_socket(side, row)[0].default_value = value.get('x', 0)
    elif row.kind in ('colour', 'pane'):
        factor_socket(side, row)[0].default_value = tuple(value.get(key, 0) for key in 'rgb') + (1,)
        if row.kind == 'colour':
            side.bsdf.inputs['Alpha'].default_value = value.get('a', 1)
    else:
        # Magic's emissive is a luminance in nits; Blender's emission is a radiance in W/sr/m2. An emissive of 0 is a
        # material that gives no light, and Blender's emission colour has no meaning there.
        light = [value.get(key, 0) for key in 'xyz']
        bright = max(light)
        side.bsdf.inputs['Emission Strength'].default_value = bright / LUMENS_PER_WATT
        if bright:
            factor_socket(side, row)[0].default_value = tuple(part / bright for part in light) + (1,)


def map_node(side, row):
    """The node that reads the image of one map of a material, nothing for a map that a person took away, and a line
    for a node that gives the map no image."""
    socket = side.bsdf.inputs[row.sockets[0][0]]
    if not socket.is_linked:
        return None, None
    reader, fault = walk_back(socket, row.behind)
    if fault:
        return None, fault
    held = reader.inputs[row.held]
    if not held.is_linked:
        return None, None
    node = held.links[0].from_node
    if node.bl_idname != 'ShaderNodeTexImage':
        return None, f'the map that {node.name} ({node.bl_idname}) gives its {socket.name}, which is no image'
    if node.image is None:
        return None, f'the map of its {socket.name}, whose node {node.name} holds no image'
    return node, None


def map_images(side):
    """The node that reads the image of each map of a material, by the row of that map. A map whose nodes the export
    cannot read is not one here, and material_values gives the line of the report for it."""
    found = {}
    for row in rows_of(side.assets, side.data):
        node = map_node(side, row)[0] if row.kind == 'map' else None
        if node is not None:
            found[row] = node
    return found


def read_map(side, row):
    """The texture that one map of a material reads now, nothing for a map that a person took away, and a line for an
    image that is no asset of the project or for a node that gives the map no image."""
    node, fault = map_node(side, row)
    if fault or node is None:
        return None, fault
    file = image_file(node.image)
    if file not in side.textures:
        return None, (f'the image {node.image.name} of its {row.sockets[0][0]}, which is no asset of this project '
                      'yet')
    return {'texture': {'id': side.textures[file]}}, None


def image_in(side, name):
    """The node that reads the image straight in one input of the Principled BSDF, and nothing while another node or
    no node at all feeds that input. It is the shape of a material that another tool made."""
    socket = side.bsdf.inputs[name]
    if not socket.is_linked or socket.links[0].from_node.bl_idname != 'ShaderNodeTexImage':
        return None
    return socket.links[0].from_node


def straight_images(side):
    """The node that reads the image straight in each input of the Principled BSDF that Magic holds in its ORM map, by
    the channel of that map. It is the shape of a material that another tool made; the shape that an import builds
    holds one image behind a Separate Color node and gives nothing here."""
    found = {}
    for name, channel in PACKED.items():
        node = image_in(side, name)
        if node is not None:
            found[channel] = node
    return found


def packed_image(nodes, name):
    """One ORM image of the images that stand for its channels, with Blender's own image API: each channel the red
    channel of its own image, which is the value that a map of data holds, and white where no image stands, which
    multiplies the factor beside it by one. It gives the image, or the reason that the images are no one map.

    Each image is read as data and not as colour, so that its values come through as they stand."""
    images = [node.image for node in nodes.values()]
    names = ', '.join(sorted(image.name for image in images))
    for image in images:
        read_space(image, ORM_MAP.space)
    sizes = {tuple(image.size) for image in images}
    if len(sizes) > 1:
        return None, f'{names} are of {len(sizes)} sizes, and one ORM map holds one size'
    width, height = sizes.pop()
    if not width or not height:
        return None, f'{names} hold no pixels, as an image whose file Blender cannot read does'
    pixels = [1.0] * (width * height * CHANNELS)
    for channel, node in nodes.items():
        read = list(node.image.pixels)
        pixels[channel::CHANNELS] = read[0::CHANNELS]
    made = bpy.data.images.new(name, width, height, alpha=False)
    made.colorspace_settings.name = ORM_MAP.space
    made.pixels = pixels
    return made, None


def pack_orm(side):
    """Packs the roughness and the metalness that a material of another tool holds as one image each into the one ORM
    map that Magic reads: the image of both channels, behind the nodes that an import builds, and the factor of each
    input that it feeds. An input that the image drives takes the factor one, which is what the image alone in that
    input stands for in Blender, and an input that the map now feeds and had no image of its own keeps the value that
    it held.

    It gives the image that the export writes, nothing for a material that holds no such shape, and the reason that
    the images are no one map. The nodes of the two images are gone, so a person sees the one ORM map of the material
    in Blender."""
    nodes = straight_images(side)
    if not nodes:
        return None, None
    image, fault = packed_image(nodes, f'{side.material.name}Orm')
    if fault:
        return None, fault
    was = {name: side.bsdf.inputs[name].default_value for name in PACKED}
    for node in set(nodes.values()):
        side.material.node_tree.nodes.remove(node)
    build_map(side, ORM_MAP, image_node(side, image))
    for name, channel in PACKED.items():
        factor = ONE[BEHIND[name].kind] if channel in nodes else was[name]
        factor_socket(side, BEHIND[name])[0].default_value = factor
    return image, None


def convert_straight(side):
    """Builds the map that Magic holds for each image which stands straight in an input of the Principled BSDF: the
    nodes that an import puts between the image and the input, and the value that changes nothing behind them, since
    the image alone drives that input in Blender. The node of the image stays, so the alpha that it gives stays too.

    It is the shape of a material that another tool made. A material whose maps an import built holds an image behind
    those nodes already and keeps every one of them."""
    for name, row in STRAIGHT.items():
        node = image_in(side, name)
        if node is None:
            continue
        read_space(node.image, row.space)
        build_map(side, row, node)
        factor_socket(side, BEHIND[name])[0].default_value = ONE[BEHIND[name].kind]


def read_alpha(side, row):
    """The alpha that the colour of a material holds now, and a line when the export cannot read it.

    The clear part of a glass carries the alpha of its colour together with its clarity, so the pane of a glass holds
    no alpha of its own and keeps the alpha that its file holds. Magic multiplies the alpha of the colour map into the
    colour, so the alpha of that map's image in Alpha is the alpha that the material holds already, and there too the
    alpha of the file stands. Any other node in Alpha is no value of a material of Magic."""
    if row.kind == 'pane':
        return file_value(side, row).get('a', 1), None
    socket = side.bsdf.inputs['Alpha']
    if not socket.is_linked:
        return socket.default_value, None
    node, fault = map_node(side, COLOUR_MAP)
    if fault:
        return None, fault
    link = socket.links[0]
    if link.from_node is not node or link.from_socket.name != 'Alpha':
        return None, (f'what {link.from_node.name} ({link.from_node.bl_idname}) gives its Alpha, which a material of '
                      'Magic holds as the alpha of its colour map')
    return file_value(side, row).get('a', 1), None


def plain_faults(material):
    """A line for each node that reads the UVs before an image and now moves, turns or scales them, which Magic has no
    place for: it reads every map at the UVs of the mesh."""
    faults = []
    for node in material.node_tree.nodes:
        for name, value in PLAIN_NODES.get(node.bl_idname, ()):
            held = node.inputs[name]
            if held.is_linked or not all(same_value(part, was) for part, was in zip(value, held.default_value)):
                faults.append(f'the {name} of its node {node.name} ({node.bl_idname}), which Magic has no place for: '
                              'it reads every map at the UVs of the mesh')
    return faults


def read_unmapped(side, row):
    """A line for one value that Blender holds and a material of Magic has no place for, when a person changed it from
    the value that the import left it at. A value in a node that nothing feeds any more is no value of the
    material."""
    name = row.sockets[0][0]
    if row.behind is not None and not side.bsdf.inputs[name].is_linked:
        return None
    socket, fault = factor_socket(side, row)
    if fault:
        return fault
    return (None if same_value(row.absent, socket.default_value)
            else f'the {row.held or name} of its {name}, which Magic has no place for')


def read_inputs(rows):
    """Each input of the Principled BSDF that a table reads a node from: the input of each row that walks back from
    the link in it, and the Alpha that the colour of a material holds (read_alpha). Every other input of the BSDF
    holds its own value alone, so a node in one of them is no value of a material of Magic."""
    read = {socket for row in rows if row.behind is not None for socket, _ in row.sockets}
    return read | {'Alpha'} if any(row.kind == 'colour' for row in rows) else read


def linked_faults(side, rows):
    """A line for each input of the Principled BSDF that a node feeds and no row of the table reads: Magic's material
    holds no value for it, so the export names the input and the node instead of writing a material that loses
    them."""
    read = read_inputs(rows)
    return [f'what {socket.links[0].from_node.name} ({socket.links[0].from_node.bl_idname}) gives its {socket.name}, '
            'which is no value of a material of Magic'
            for socket in side.bsdf.inputs if socket.is_linked and socket.name not in read]


def read_row(side, row):
    """The value that one row of the table holds in Blender now, and a line when the export cannot read it. It is the
    inverse of write_row."""
    if row.kind == 'choice':
        if side.material.surface_render_method == 'BLENDED':
            return 'transparent', None
        # Dithered is how Blender draws a material that is opaque and one that is masked, so the file keeps the two
        # apart, and a material drawn as dithered that the file calls transparent is opaque now.
        was = str(file_value(side, row)).lower()
        return (was if RENDER_OF.get(was) == 'DITHERED' else 'opaque'), None
    if row.kind == 'switch':
        return not side.material.use_backface_culling, None
    if row.kind == 'map':
        return read_map(side, row)
    if row.kind == 'unmapped':
        return None, read_unmapped(side, row)
    if row.kind == 'clarity':
        mix = next((node for node in side.material.node_tree.nodes if node.bl_idname == 'ShaderNodeMixShader'), None)
        if mix is None:
            return None, 'the clarity of its glass, which no mix of a clear part holds any more'
        alpha = file_value(side, GLASS_COLOUR).get('a', 1)
        return {'x': mix.inputs[0].default_value / alpha if alpha else 1}, None
    socket, fault = factor_socket(side, row)
    if fault:
        return None, fault
    if row.kind == 'factor':
        return {'x': socket.default_value}, None
    if row.kind in ('colour', 'pane'):
        alpha, fault = read_alpha(side, row)
        if fault:
            return None, fault
        return {'r': socket.default_value[0], 'g': socket.default_value[1], 'b': socket.default_value[2],
                'a': alpha}, None
    # Magic's emissive is a luminance in nits; Blender's emission is a radiance in W/sr/m2.
    watts = side.bsdf.inputs['Emission Strength'].default_value * LUMENS_PER_WATT
    return {key: socket.default_value[at] * watts for at, key in enumerate('xyz')}, None


def build_material(side):
    """The nodes of one Blender material from the values of a Magic material: each row of its table in the place that
    the table gives it, and the clear part of a glass over them."""
    rows = rows_of(side.assets, side.data)
    for row in rows:
        write_row(side, row, file_value(side, row))
    if rows is GLASS:
        build_glass(side)


# The id of a slot that holds no material. Magic draws none of the faces of such a slot and casts no shadow from them.
EMPTY_SLOT = 0


def make_empty_slot():
    """The Blender material of a slot that holds no material: clear to the camera and to shadows, as Magic draws none
    of its faces."""
    material = bpy.data.materials.new('Empty slot')
    material['magic_id'] = str(EMPTY_SLOT)
    material.use_nodes = True
    tree = material.node_tree
    tree.nodes.clear()
    clear = tree.nodes.new('ShaderNodeBsdfTransparent')
    output = tree.nodes.new('ShaderNodeOutputMaterial')
    tree.links.new(clear.outputs['BSDF'], output.inputs['Surface'])
    return material


def make_material(assets, material_id, cache):
    """The Blender material of a Magic material, made one time and linked by every object that uses it."""
    if material_id in cache:
        return cache[material_id]
    if material_id == EMPTY_SLOT:
        cache[material_id] = make_empty_slot()
        return cache[material_id]
    path = assets[material_id][0]
    material = bpy.data.materials.new(os.path.basename(path))
    # The asset that it came from, by which the export tells a material of the project from one that a person made.
    material['magic_id'] = str(material_id)
    cache[material_id] = material
    build_material(side_of(material, assets, read_jsonc(path)))
    return material


def material_values(material, assets, data):
    """What one Blender material says of its Magic material now: each value that it says differently, by the member
    that holds it as Magic's `set` names it; a line for each value of the Blender material that Magic's material has
    no place for; and the name of each param of the Magic material that Blender does not show.

    Magic holds every value that this leaves out and writes it as it was, a param that Blender does not show among
    them. The caller writes nothing while one line stands. It is the inverse of build_material."""
    if material.node_tree is None:
        return {}, ['no nodes at all, which a material of Magic holds its values in'], []
    side = side_of(material, assets, data)
    faults = [f'the node {node.name} ({node.bl_idname}), which no material of Magic holds'
              for node in material.node_tree.nodes if node.bl_idname not in BUILT_NODES] + plain_faults(material)
    if side.bsdf is None:
        return {}, faults + ['no Principled BSDF, which holds the values of a material of Magic'], []
    rows = rows_of(assets, data)
    faults += linked_faults(side, rows)
    changed = {}
    for row in rows:
        value, fault = read_row(side, row)
        if fault:
            faults.append(fault)
        elif row.name is not None and not reads_as(row.kind, value, file_value(side, row)):
            # A map that a person took away is no map here and a param with no texture in Magic, which is how a
            # material loses a map: a param is written over, never taken out.
            changed[row.name] = {} if value is None else value
    shown = {row.name[len(PARAMS):] for row in rows if row.name and row.name.startswith(PARAMS)}
    return ({} if faults else changed), faults, [name for name in data.get('params', {}) if name not in shown]
