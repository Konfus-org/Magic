# Builds one view of tools/Scripts/ReferenceViews.json as a Blender scene from the files Magic loads, renders it with
# Cycles and writes a linear HDR EXR. Run it with Blender's own Python:
#   blender -b --python tools/Reference/BuildScene.py -- --view cabin-wall --hour 12 --out picture.exr
# Magic is left-handed with Y up; Blender is right-handed with Z up. A point (x, y, z) in Magic is (x, z, y) in
# Blender, and a model imported by Blender's FBX importer is in Blender's axes, so its Magic axes are the same swap.
import argparse
import json
import math
import os
import re
import sys

import bmesh
import bpy
from mathutils import Matrix, Quaternion, Vector

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
LUMENS_PER_WATT = 683

# The multiple scattering sky's sun above the air gives 181.6 W/m2 at a strength of 1 (measured in Blender 5.3 with
# no air): this strength makes it Magic's 120,000 lux.
SKY_STRENGTH = 120000 / LUMENS_PER_WATT / 181.6

SWAP = Matrix(((1, 0, 0, 0), (0, 0, 1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))


def read_jsonc(path):
    """Reads a JSON file with // comments."""
    text = open(path, encoding='utf-8-sig').read()
    out, in_string, i = [], False, 0
    while i < len(text):
        c = text[i]
        if in_string:
            out.append(c)
            if c == '\\':
                out.append(text[i + 1])
                i += 1
            elif c == '"':
                in_string = False
        elif c == '"':
            in_string = True
            out.append(c)
        elif text.startswith('//', i):
            i = text.find('\n', i)
            i = len(text) if i < 0 else i
            continue
        else:
            out.append(c)
        i += 1
    return json.loads(re.sub(r',(\s*[}\]])', r'\1', ''.join(out)))


def index_assets():
    """Each asset's id, from its .meta file, to the path of the file beside it."""
    assets = {}
    for folder in ('src', 'samples'):
        for top, dirs, files in os.walk(os.path.join(ROOT, folder)):
            dirs[:] = [d for d in dirs if d not in ('bin', 'obj', 'Build')]
            for name in files:
                if name.endswith('.meta'):
                    path = os.path.join(top, name)
                    meta = read_jsonc(path)
                    if 'id' in meta:
                        assets[meta['id']] = (path[:-5], meta)
    return assets


def magic_matrix(transform):
    p = transform.get('position', {})
    r = transform.get('rotation', {})
    s = transform.get('scale', {})
    rotation = Quaternion((r.get('w', 1), r.get('x', 0), r.get('y', 0), r.get('z', 0)))
    scale = Matrix.Diagonal((s.get('x', 1), s.get('y', 1), s.get('z', 1), 1))
    return Matrix.Translation((p.get('x', 0), p.get('y', 0), p.get('z', 0))) @ rotation.to_matrix().to_4x4() @ scale


def entities(assets, prefab_path, parent):
    """Each entity of a prefab with its world matrix in Magic's axes, prefab instances and children expanded."""
    for entity in read_jsonc(prefab_path)['entities']:
        components = entity.get('components', {})
        world = parent @ magic_matrix(components.get('Transform', {}))
        if 'prefab' in entity:
            yield from entities(assets, assets[entity['prefab']['id']][0], world)
        yield entity, components, world
        stack = [(child, world) for child in entity.get('children', [])]
        while stack:
            child, above = stack.pop()
            child_world = above @ magic_matrix(child.get('components', {}).get('Transform', {}))
            yield child, child.get('components', {}), child_world
            stack += [(grandchild, child_world) for grandchild in child.get('children', [])]


def rotate(q, v):
    return q @ Vector(v)


DAY_CYCLE = 100421


def light_rotation(components, hour, latitude=35):
    """The rotation of a directional light: the one that the samples' DayCycle script gives it at the hour (samples/Shared
    DayCycle.cs), or its own when it has no DayCycle or no hour is given. A moon's DayCycle runs 12 hours behind."""
    r = components.get('Transform', {}).get('rotation', {})
    rotation = Quaternion((r.get('w', 1), r.get('x', 0), r.get('y', 0), r.get('z', 0)))
    if hour is None or not any(s.get('id') == DAY_CYCLE for s in components.get('ScriptContainer', [])):
        return rotation
    if components['DirectionalLight'].get('mode') == 'moon':
        hour -= 12
    travel = rotate(rotation, (0, 0, 1)).normalized()
    ground = Vector((travel.x, 0, travel.z))
    axis = Vector((0, 1, 0)).cross(ground).normalized() if ground.length > 0 else Vector((1, 0, 0))
    west = axis.cross(Vector((0, 1, 0)))

    def turn(q, radians):
        return (Quaternion(axis, radians) @ q).normalized()

    noon = turn(rotation, math.acos(max(-1, min(1, -travel.y))))
    day = turn(noon, (hour - 12) * math.tau / 24)
    return (Quaternion(west, math.radians(latitude)) @ day).normalized()


def image_node(tree, assets, texture_id, colour):
    path = assets[texture_id][0]
    node = tree.nodes.new('ShaderNodeTexImage')
    node.image = bpy.data.images.load(path, check_existing=True)
    node.image.colorspace_settings.name = 'sRGB' if colour else 'Non-Color'
    return node


def make_material(assets, material_id, cache):
    if material_id in cache:
        return cache[material_id]
    path = assets[material_id][0]
    data = read_jsonc(path)
    params = data.get('params', {})
    shader_path = assets.get(data['shader']['id'], ('',))[0]
    material = bpy.data.materials.new(os.path.basename(path))
    tree = material.node_tree
    bsdf = tree.nodes['Principled BSDF']
    links = tree.links
    c = params.get('color', {'r': 1, 'g': 1, 'b': 1, 'a': 1})
    colour = (c['r'], c['g'], c['b'], 1)
    roughness = params.get('roughness', {'x': 1})['x']
    if os.path.basename(shader_path).startswith('Glass'):
        # Glass: clear for the shadow rays as far as its clarity lets light through, as Magic's shadows treat it.
        glass = tree.nodes.new('ShaderNodeBsdfPrincipled')
        glass.inputs['Base Color'].default_value = colour
        glass.inputs['Roughness'].default_value = roughness
        glass.inputs['Transmission Weight'].default_value = 1
        glass.inputs['IOR'].default_value = 1.5
        clear = tree.nodes.new('ShaderNodeBsdfTransparent')
        clear.inputs[0].default_value = [params.get('clarity', {'x': 1})['x']] * 3 + [1]
        path_node = tree.nodes.new('ShaderNodeLightPath')
        mix = tree.nodes.new('ShaderNodeMixShader')
        links.new(path_node.outputs['Is Shadow Ray'], mix.inputs[0])
        links.new(glass.outputs[0], mix.inputs[1])
        links.new(clear.outputs[0], mix.inputs[2])
        links.new(mix.outputs[0], tree.nodes['Material Output'].inputs[0])
        cache[material_id] = material
        return material

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
    cache[material_id] = material
    return material


def import_model(path, cache):
    """The model's mesh in Magic's model axes, its material slots in the order Assimp numbers them: the order in which
    the file's faces first use each material."""
    if path in cache:
        return cache[path]
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path)
    imported = sorted((o for o in bpy.data.objects if o not in before), key=lambda o: o.name)
    meshes = [o for o in imported if o.type == 'MESH']
    order = []
    for o in meshes:
        for polygon in o.data.polygons:
            slot = o.material_slots[polygon.material_index].material if o.material_slots else None
            name = slot.name if slot else ''
            if name not in order:
                order.append(name)
    combined = bpy.data.meshes.new(os.path.basename(path))
    target = bmesh.new()
    for o in meshes:
        piece = bmesh.new()
        piece.from_mesh(o.data)
        piece.transform(SWAP @ o.matrix_world)
        names = [s.material.name if s.material else '' for s in o.material_slots] or ['']
        for face in piece.faces:
            face.material_index = order.index(names[face.material_index] if face.material_index < len(names) else '')
        scratch = bpy.data.meshes.new('piece')
        piece.to_mesh(scratch)
        piece.free()
        target.from_mesh(scratch)
        bpy.data.meshes.remove(scratch)
    target.to_mesh(combined)
    target.free()
    for o in imported:
        bpy.data.objects.remove(o)
    cache[path] = (combined, len(order))
    return cache[path]


def place_renderer(assets, name, renderer, world, models, materials):
    mesh, slots = import_model(assets[renderer['model']['id']][0], models)
    mesh = mesh.copy()
    ids = [m['id'] for m in renderer.get('materials', [])]
    for i in range(slots):
        mesh.materials.append(make_material(assets, ids[i], materials) if i < len(ids) else None)
    if slots > len(ids):
        # A slot with no material draws nothing in Magic.
        b = bmesh.new()
        b.from_mesh(mesh)
        bmesh.ops.delete(b, geom=[f for f in b.faces if f.material_index >= len(ids)], context='FACES')
        b.to_mesh(mesh)
        b.free()
    o = bpy.data.objects.new(name, mesh)
    # The swap back to Blender's axes mirrors, and the mirror in the mesh undoes it.
    o.matrix_world = SWAP @ world
    bpy.context.scene.collection.objects.link(o)


def place_point_light(name, light, world):
    c = light.get('color', {'r': 1, 'g': 1, 'b': 1})
    data = bpy.data.lights.new(name, 'POINT')
    data.color = (c['r'], c['g'], c['b'])
    # Candela to Blender's watts: a point light of P W gives P / 4pi W/sr.
    data.energy = 4 * math.pi * light.get('intensity', 64) / LUMENS_PER_WATT
    data.shadow_soft_size = light.get('radius', 0.025)
    o = bpy.data.objects.new(name, data)
    o.location = (SWAP @ world).to_translation()
    bpy.context.scene.collection.objects.link(o)


def build(view, hour, width, height):
    assets = index_assets()
    project = json.load(open(os.path.join(ROOT, view['project'])))
    domain = assets[project['entryPoint']['id']][0]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    models, materials = {}, {}
    sun_direction = None
    folder = os.path.dirname(domain)
    for prefab in sorted(f for f in os.listdir(folder) if f.endswith('.prefab')):
        for entity, components, world in entities(assets, os.path.join(folder, prefab), Matrix.Identity(4)):
            name = entity.get('name', 'entity')
            if 'Renderer' in components:
                place_renderer(assets, name, components['Renderer'], world, models, materials)
            if 'PointLight' in components:
                place_point_light(name, components['PointLight'], world)
            light = components.get('DirectionalLight')
            if light:
                travel = rotate(light_rotation(components, hour), (0, 0, 1))
                towards = Vector((-travel.x, -travel.z, -travel.y))
                if light.get('mode') == 'sun':
                    sun_direction = towards
                elif towards.z > 0:
                    c = light.get('color', {'r': 1, 'g': 1, 'b': 1})
                    data = bpy.data.lights.new(name, 'SUN')
                    data.color = (c['r'], c['g'], c['b'])
                    data.energy = light.get('intensity', 0.3) / LUMENS_PER_WATT
                    data.angle = math.radians(0.5)
                    o = bpy.data.objects.new(name, data)
                    o.rotation_euler = towards.to_track_quat('Z', 'Y').to_euler()
                    scene.collection.objects.link(o)

    # The sky and its sun, at the sun's direction.
    scene.world = bpy.data.worlds.new('Sky')
    tree = scene.world.node_tree
    sky = tree.nodes.new('ShaderNodeTexSky')
    sky.sky_type = 'MULTIPLE_SCATTERING'
    sky.sun_disc = True
    sky.sun_elevation = math.asin(max(-1, min(1, sun_direction.z)))
    # A rotation of 0 puts the sun toward +Y and 90 degrees toward +X (measured in Blender 5.3).
    sky.sun_rotation = math.atan2(sun_direction.x, sun_direction.y)
    tree.links.new(sky.outputs[0], tree.nodes['Background'].inputs[0])
    tree.nodes['Background'].inputs[1].default_value = SKY_STRENGTH

    # The camera, as tools/Scripts/CompareReference.ps1 poses Magic's: its yaw about up, then its pitch about its right.
    yaw = math.radians(view['yaw']) / 2
    pitch = math.radians(view['pitch']) / 2
    q = Quaternion((math.cos(yaw) * math.cos(pitch), math.cos(yaw) * math.sin(pitch),
                    math.sin(yaw) * math.cos(pitch), -math.sin(yaw) * math.sin(pitch)))
    right, up, forward = (rotate(q, axis) for axis in ((1, 0, 0), (0, 1, 0), (0, 0, 1)))
    swap = Matrix(((1, 0, 0), (0, 0, 1), (0, 1, 0)))
    basis = Matrix((swap @ right, swap @ up, -(swap @ forward))).transposed()
    camera = bpy.data.objects.new('Camera', bpy.data.cameras.new('Camera'))
    p = view['position']
    camera.matrix_world = Matrix.Translation((p['x'], p['z'], p['y'])) @ basis.to_4x4()
    camera.data.sensor_fit = 'VERTICAL'
    camera.data.angle_y = math.radians(view.get('fieldOfView', 60))
    camera.data.clip_start = 0.05
    camera.data.clip_end = 20000
    scene.collection.objects.link(camera)
    scene.camera = camera

    scene.render.engine = 'CYCLES'
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.view_settings.view_transform = 'Standard'
    scene.render.image_settings.file_format = 'OPEN_EXR'
    scene.render.image_settings.color_depth = '32'
    cycles = scene.cycles
    cycles.max_bounces = 16
    cycles.diffuse_bounces = 12
    cycles.glossy_bounces = 8
    cycles.transmission_bounces = 12
    cycles.transparent_max_bounces = 16
    cycles.sample_clamp_direct = 0
    cycles.sample_clamp_indirect = 0
    cycles.use_adaptive_sampling = True
    cycles.adaptive_threshold = 0.005
    cycles.film_exposure = 1
    cycles.caustics_refractive = True
    cycles.caustics_reflective = True
    preferences = bpy.context.preferences.addons['cycles'].preferences
    for kind in ('OPTIX', 'CUDA', 'HIP', 'ONEAPI', 'METAL'):
        try:
            preferences.compute_device_type = kind
            preferences.get_devices()
            if any(d.type == kind for d in preferences.devices):
                for d in preferences.devices:
                    d.use = d.type == kind
                cycles.device = 'GPU'
                break
        except TypeError:
            continue
    return scene


def print_lights(view, hour):
    """Prints 'sun x y z w' and 'moon x y z w': the rotation that DayCycle gives each light at the hour, which Magic is
    set to with its DayCycle removed, since a new DayCycle starts from the rotation the light has then."""
    assets = index_assets()
    project = json.load(open(os.path.join(ROOT, view['project'])))
    folder = os.path.dirname(assets[project['entryPoint']['id']][0])
    for prefab in sorted(f for f in os.listdir(folder) if f.endswith('.prefab')):
        for entity, components, world in entities(assets, os.path.join(folder, prefab), Matrix.Identity(4)):
            light = components.get('DirectionalLight')
            if light:
                q = light_rotation(components, hour)
                print(f"{light.get('mode', 'sun')} {q.x:.6f} {q.y:.6f} {q.z:.6f} {q.w:.6f}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--view', required=True)
    parser.add_argument('--hour', type=float, help='The hour of a light with a DayCycle; without it, its own rotation.')
    parser.add_argument('--out')
    parser.add_argument('--sun', action='store_true',
                        help="Print the sun's and the moon's rotation in Magic at the hour, from the domain, and stop.")
    parser.add_argument('--samples', type=int, default=4096)
    parser.add_argument('--width', type=int, default=1280)
    parser.add_argument('--height', type=int, default=658)
    parser.add_argument('--blend', help='Also save the scene as this .blend file.')
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    views = json.load(open(os.path.join(ROOT, 'tools', 'Scripts', 'ReferenceViews.json')))
    view = next(v for v in views if v['name'] == args.view)
    if args.sun:
        print_lights(view, args.hour)
        return
    scene = build(view, args.hour, args.width, args.height)
    scene.cycles.samples = args.samples
    scene.cycles.use_denoising = True
    scene.cycles.denoiser = 'OPENIMAGEDENOISE'
    scene.render.filepath = os.path.abspath(args.out)
    if args.blend:
        bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(args.blend))
    bpy.ops.render.render(write_still=True)


main()
