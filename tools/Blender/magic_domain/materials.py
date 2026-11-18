# Magic's materials as Blender materials: the colour, the maps, the emissive light and the glass.
import os
import zlib

import bpy

from .project import LUMENS_PER_WATT, read_jsonc

# The nodes that build_material makes. A node of another kind is one that a person added, and Magic's material holds
# no value for it, so the export names it instead of writing the material.
BUILT_NODES = {'ShaderNodeBsdfPrincipled', 'ShaderNodeOutputMaterial', 'ShaderNodeTexImage', 'ShaderNodeMix',
               'ShaderNodeSeparateColor', 'ShaderNodeMath', 'ShaderNodeNormalMap', 'ShaderNodeBsdfTransparent',
               'ShaderNodeMixShader'}


def image_node(tree, assets, texture_id, colour):
    path = assets[texture_id][0]
    node = tree.nodes.new('ShaderNodeTexImage')
    node.image = bpy.data.images.load(path, check_existing=True)
    node.image.colorspace_settings.name = 'sRGB' if colour else 'Non-Color'
    return node


def make_glass(params, material):
    """Magic's glass over the lit picture: its clarity of the light behind it straight through, tinted by its colour
    and unbent, and the rest a lit pane. Camera, diffuse and shadow rays all get the same mix, which is what Magic's
    blend gives the picture and what its shadow rays read from the pane (AverageMaterials.slang: the colour times one
    less the alpha)."""
    tree = material.node_tree
    c = params.get('color', {'r': 1, 'g': 1, 'b': 1, 'a': 1})
    colour = (c['r'], c['g'], c['b'], 1)
    pane = tree.nodes['Principled BSDF']
    pane.inputs['Base Color'].default_value = colour
    pane.inputs['Roughness'].default_value = params.get('roughness', {'x': 1})['x']
    pane.inputs['Metallic'].default_value = 0
    clear = tree.nodes.new('ShaderNodeBsdfTransparent')
    clear.inputs['Color'].default_value = colour
    mix = tree.nodes.new('ShaderNodeMixShader')
    mix.inputs[0].default_value = params.get('clarity', {'x': 1})['x'] * c.get('a', 1)
    tree.links.new(pane.outputs[0], mix.inputs[1])
    tree.links.new(clear.outputs[0], mix.inputs[2])
    tree.links.new(mix.outputs[0], tree.nodes['Material Output'].inputs[0])


def material_hash(material):
    """A number over the values of a Blender material, by which the export sees that a person changed it: each node
    with the values of its inputs and the image it reads, and each link between the nodes."""
    parts = []
    for node in sorted(material.node_tree.nodes, key=lambda node: node.name):
        image = getattr(node, 'image', None)
        parts.append(node.bl_idname + (f':{image.name}' if image else ''))
        for socket in node.inputs:
            value = getattr(socket, 'default_value', None)
            parts.append(repr(tuple(value)) if hasattr(value, '__len__') else repr(value))
    parts += sorted(f'{link.from_node.name}.{link.from_socket.name}>{link.to_node.name}.{link.to_socket.name}'
                    for link in material.node_tree.links)
    return f'{zlib.crc32("|".join(parts).encode()):08x}'


def make_material(assets, material_id, cache):
    """The Blender material of a Magic material, made one time and linked by every object that uses it."""
    if material_id in cache:
        return cache[material_id]
    material = build_material(assets, material_id, cache)
    # The hash of the material as the import made it, by which the export sees a material that a person changed.
    material['magic_hash'] = material_hash(material)
    return material


def build_material(assets, material_id, cache):
    path = assets[material_id][0]
    data = read_jsonc(path)
    params = data.get('params', {})
    shader_path = assets.get(data['shader']['id'], ('',))[0]
    material = bpy.data.materials.new(os.path.basename(path))
    # The asset that it came from, by which the export tells a material of the project from a new one.
    material['magic_id'] = str(material_id)
    cache[material_id] = material
    if os.path.basename(shader_path).startswith('Glass'):
        make_glass(params, material)
        return material

    tree = material.node_tree
    bsdf = tree.nodes['Principled BSDF']
    links = tree.links
    c = params.get('color', {'r': 1, 'g': 1, 'b': 1, 'a': 1})
    colour = (c['r'], c['g'], c['b'], 1)
    roughness = params.get('roughness', {'x': 1})['x']
    bsdf.inputs['Base Color'].default_value = colour
    bsdf.inputs['Roughness'].default_value = roughness
    bsdf.inputs['Metallic'].default_value = params.get('metalness', {'x': 0})['x']
    if 'colorMap' in params:
        image = image_node(tree, assets, params['colorMap']['texture']['id'], True)
        tint = tree.nodes.new('ShaderNodeMix')
        tint.data_type = 'RGBA'
        tint.blend_type = 'MULTIPLY'
        tint.inputs['Factor'].default_value = 1
        tint.inputs['A'].default_value = colour
        links.new(image.outputs['Color'], tint.inputs['B'])
        links.new(tint.outputs['Result'], bsdf.inputs['Base Color'])
    if 'ormMap' in params:
        image = image_node(tree, assets, params['ormMap']['texture']['id'], False)
        split = tree.nodes.new('ShaderNodeSeparateColor')
        links.new(image.outputs['Color'], split.inputs[0])
        for channel, name, factor in ((1, 'Roughness', roughness),
                                      (2, 'Metallic', params.get('metalness', {'x': 0})['x'])):
            scale = tree.nodes.new('ShaderNodeMath')
            scale.operation = 'MULTIPLY'
            scale.inputs[1].default_value = factor
            links.new(split.outputs[channel], scale.inputs[0])
            links.new(scale.outputs[0], bsdf.inputs[name])
    if 'normalMap' in params:
        image = image_node(tree, assets, params['normalMap']['texture']['id'], False)
        normal = tree.nodes.new('ShaderNodeNormalMap')
        links.new(image.outputs['Color'], normal.inputs['Color'])
        links.new(normal.outputs['Normal'], bsdf.inputs['Normal'])
    if 'emissive' in params:
        # Magic's emissive is a luminance in nits; Blender's emission is a radiance in W/sr/m2.
        e = params['emissive']
        bright = max(e['x'], e['y'], e['z'])
        bsdf.inputs['Emission Strength'].default_value = bright / LUMENS_PER_WATT
        bsdf.inputs['Emission Color'].default_value = (e['x'] / bright, e['y'] / bright, e['z'] / bright, 1)
        if 'emissiveMap' in params:
            image = image_node(tree, assets, params['emissiveMap']['texture']['id'], True)
            tint = tree.nodes.new('ShaderNodeMix')
            tint.data_type = 'RGBA'
            tint.blend_type = 'MULTIPLY'
            tint.inputs['Factor'].default_value = 1
            tint.inputs['A'].default_value = bsdf.inputs['Emission Color'].default_value
            links.new(image.outputs['Color'], tint.inputs['B'])
            links.new(tint.outputs['Result'], bsdf.inputs['Emission Color'])
    return material


def value_behind(socket, node_kind, held):
    """The value that a socket of the Principled BSDF stands for, and a line when the export cannot read it. A socket
    that nothing links holds the value itself. A socket that the import linked holds it in the node behind it, whose
    kind the caller names with the input that keeps it: the colour of a tint in its A, the factor of a map in the
    second input of its scale."""
    if not socket.is_linked:
        return socket.default_value, None
    behind = socket.links[0].from_node
    if behind.bl_idname != node_kind:
        return None, (f'what {behind.name} ({behind.bl_idname}) gives its {socket.name}, which is no value of a '
                      'material of Magic')
    return behind.inputs[held].default_value, None


def colour_values(colour, alpha):
    """A colour of Blender as the color of a Magic material, which keeps the alpha that it was read with: the import
    reads the alpha as the clarity of a glass alone and puts 1 in Blender's colour."""
    return {'r': colour[0], 'g': colour[1], 'b': colour[2], 'a': alpha}


def material_values(material, params):
    """The params that a Magic material needs to hold what a Blender material now says: the params as they were read,
    with each value that build_material put into the nodes read back from the same node. It gives them with a line for
    each value of the Blender material that Magic's material has no place for, and the caller writes nothing while one
    line stands. It is the inverse of build_material, and it reads back no map: a map stays as the file holds it."""
    tree = material.node_tree
    faults = [f"the node {node.name} ({node.bl_idname}), which no material of Magic holds"
              for node in tree.nodes if node.bl_idname not in BUILT_NODES]
    bsdf = tree.nodes.get('Principled BSDF')
    if bsdf is None:
        return params, faults + ['no Principled BSDF, which holds the values of a material of Magic']
    values = dict(params)
    alpha = params.get('color', {}).get('a', 1)
    reads = [(bsdf.inputs['Base Color'], 'ShaderNodeMix', 'A'), (bsdf.inputs['Roughness'], 'ShaderNodeMath', 1),
             (bsdf.inputs['Metallic'], 'ShaderNodeMath', 1)]
    (colour, one), (roughness, two), (metalness, three) = [value_behind(*read) for read in reads]
    faults += [line for line in (one, two, three) if line]
    if faults:
        return params, faults

    mix = next((node for node in tree.nodes if node.bl_idname == 'ShaderNodeMixShader'), None)
    if mix is not None:
        # A glass: the pane's colour and roughness, and the clarity that the mix of the clear part was made from.
        values['color'] = colour_values(colour, alpha)
        values['roughness'] = {'x': roughness}
        values['clarity'] = {'x': mix.inputs[0].default_value / alpha if alpha else 1}
        return values, faults

    values['color'] = colour_values(colour, alpha)
    values['roughness'] = {'x': roughness}
    values['metalness'] = {'x': metalness}
    emission, line = value_behind(bsdf.inputs['Emission Color'], 'ShaderNodeMix', 'A')
    if line:
        return params, faults + [line]
    # Magic's emissive is a luminance in nits; Blender's emission is a radiance in W/sr/m2.
    watts = bsdf.inputs['Emission Strength'].default_value * LUMENS_PER_WATT
    if watts:
        values['emissive'] = {'x': emission[0] * watts, 'y': emission[1] * watts, 'z': emission[2] * watts}
    else:
        values.pop('emissive', None)
        values.pop('emissiveMap', None)
    return values, faults
