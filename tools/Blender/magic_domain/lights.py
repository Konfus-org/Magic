# Magic's light in Blender: the lamps of a domain, the air over it, and the sun or the moon of an hour with the sky
# that it lights.
import math

import bpy
from mathutils import Matrix, Quaternion, Vector

from .project import LUMENS_PER_WATT, swap_pose

# Blender's multiple scattering sky, with no air in the way, gives 181.6 W/m2 at a strength of 1 (measured in Blender
# 5.3): the strength at which its sky stands for a light of one watt per square metre above the air.
SKY_WATTS_AT_ONE = 181.6

# The height, as the sine of the angle, down to which the sun still lights the world: the renderer's TwilightEnd.
TWILIGHT_END = -0.139

# Degrees from the middle to the edge of the disc that Magic aims its shadow rays through: Scene/Lights.slang's
# SunAngularRadius.
SHADOW_DISC_DEGREES = 1

# The illuminance in lux under which Magic takes the light of a point or spot light to be gone: SpotLight.FaintestLux.
FAINTEST_LUX = 0.02

# What Magic gives a point light that names neither: PointLight's own defaults.
DEFAULT_INTENSITY = 64
DEFAULT_RADIUS = 0.05

# The name of the lamp that stands for the light of the hour, which set_hour makes anew for each hour.
SUN_NAME = 'MagicSun'

# The node of a lamp that holds the metres it reaches, which the export reads back.
REACH_NODE = 'MagicReach'


def read_sun(light, components):
    """A directional light as a file of the domain holds it: the unit direction towards it in Magic's axes, its colour
    times its intensity in lux above the air, and what it is to the sky. The light of an hour is not here: Magic turns
    the light through the day, and `query sky` prints it (lit_by)."""
    r = components.get('Transform', {}).get('rotation', {})
    rotation = Quaternion((r.get('w', 1), r.get('x', 0), r.get('y', 0), r.get('z', 0)))
    towards = -(rotation @ Vector((0, 0, 1)))
    c = light.get('color', {'r': 1, 'g': 1, 'b': 1})
    return towards, Vector((c['r'], c['g'], c['b'])) * light.get('intensity', 0), light.get('mode', 'default')


def pick_sun(suns):
    """The one directional light that lights the world, of each that read_sun gives: the sun while it is up or its
    twilight lights the sky, else the moon while it is up, else the first (the renderer's GetSun). The import uses it
    for the sky of the domain's own light, with no run of Magic."""
    best, best_rank = None, -1
    for sun in suns:
        towards, _, mode = sun
        rank = 0
        if mode == 'sun' and towards.y > TWILIGHT_END:
            rank = 2
        elif mode == 'moon' and towards.y > 0:
            rank = 1
        if rank > best_rank:
            best, best_rank = sun, rank
    return best


def sky_brightness(skies):
    """What the domain's Sky component multiplies its sky's light by: Magic's Sky.Brightness, 1 where the prefab leaves
    it out and where the domain has no sky. Magic takes the brightness of one sky entity alone, so a domain with more
    than one cannot be copied."""
    if len(skies) > 1:
        raise ValueError(f'The domain holds {len(skies)} Sky components, and Magic takes the brightness of one of '
                         'them alone: leave one sky in the domain.')
    return float(skies[0].get('brightness', 1.0)) if skies else 1.0


# The Earth's air, as Magic's sky integrates it: Sky/Atmosphere.slang's own constants. Distances are in kilometres.
GROUND_RADIUS = 6360.0
TOP_RADIUS = 6460.0
RAYLEIGH_SCATTERING = Vector((5.802, 13.558, 33.1)) * 0.001
RAYLEIGH_HEIGHT = 8.0
MIE_SCATTERING = 3.996 * 0.001
MIE_ABSORPTION = 4.4 * 0.001
MIE_HEIGHT = 1.2
OZONE_ABSORPTION = Vector((0.650, 1.881, 0.085)) * 0.001
OZONE_CENTER = 25.0
OZONE_WIDTH = 15.0


def air_extinction(radius):
    """What the air at a radius takes out of the light that crosses it, per kilometre and per channel."""
    height = max(radius - GROUND_RADIUS, 0.0)
    rayleigh = math.exp(-height / RAYLEIGH_HEIGHT)
    mie = math.exp(-height / MIE_HEIGHT)
    ozone = max(0.0, min(1.0, 1.0 - abs(height - OZONE_CENTER) / OZONE_WIDTH))
    return Vector(
        [
            RAYLEIGH_SCATTERING[channel] * rayleigh
            + (MIE_SCATTERING + MIE_ABSORPTION) * mie
            + OZONE_ABSORPTION[channel] * ozone
            for channel in range(3)
        ]
    )


def distance_to_sphere(r, mu, radius):
    """Where a ray from a point at radius r, at cosine mu to the zenith, leaves a sphere, negative for never."""
    discriminant = r * r * (mu * mu - 1.0) + radius * radius
    if discriminant < 0.0:
        return -1.0
    root = math.sqrt(discriminant)
    near = -r * mu - root
    return near if near > 0.0 else -r * mu + root


def air_transmittance(altitude, height):
    """What is left of the light of a sun at that sine of height above the horizon when it reaches a point so many
    metres over the ground, per channel: Atmosphere.slang's GetAirTransmittance in its 40 steps. A sun under the
    horizon is behind the Earth, and gives the ground nothing."""
    r = GROUND_RADIUS + max(altitude * 0.001, 0.001)
    ground = distance_to_sphere(r, height, GROUND_RADIUS)
    if ground > 0.0:
        return Vector((0.0, 0.0, 0.0))
    span = distance_to_sphere(r, height, TOP_RADIUS)
    steps = 40
    optical = Vector((0.0, 0.0, 0.0))
    for step in range(steps):
        t = (step + 0.5) / steps * span
        optical += air_extinction(math.sqrt(r * r + t * t + 2.0 * r * height * t)) * (span / steps)
    return Vector([math.exp(-optical[channel]) for channel in range(3)])


def add_range_window(data, reach):
    """Fades a light's emission to nothing at the metres that it reaches, as Magic's falloff does: one less the fourth
    power of the part of the reach, clamped, and squared (Light/LocalLights.slang). The light falloff node's constant
    output is the square of the distance to the surface, which keeps a square root out of the chain."""
    data.use_nodes = True
    tree = data.node_tree
    distance = tree.nodes.new('ShaderNodeLightFalloff')
    distance.inputs['Strength'].default_value = 1
    part = tree.nodes.new('ShaderNodeMath')
    part.name = REACH_NODE
    part.operation = 'DIVIDE'
    part.inputs[1].default_value = reach * reach
    fourth = tree.nodes.new('ShaderNodeMath')
    fourth.operation = 'POWER'
    fourth.inputs[1].default_value = 2
    window = tree.nodes.new('ShaderNodeMath')
    window.operation = 'SUBTRACT'
    window.inputs[0].default_value = 1
    window.use_clamp = True
    squared = tree.nodes.new('ShaderNodeMath')
    squared.operation = 'POWER'
    squared.inputs[1].default_value = 2
    tree.links.new(distance.outputs['Constant'], part.inputs[0])
    tree.links.new(part.outputs[0], fourth.inputs[0])
    tree.links.new(fourth.outputs[0], window.inputs[1])
    tree.links.new(window.outputs[0], squared.inputs[0])
    tree.links.new(squared.outputs[0], tree.nodes['Emission'].inputs['Strength'])


def lamp_watts(intensity):
    """Candela as Blender's watts: a point light of P W gives P / 4pi W/sr."""
    return 4 * math.pi * intensity / LUMENS_PER_WATT


def lamp_candela(watts):
    """Blender's watts as candela: the inverse of lamp_watts."""
    return watts * LUMENS_PER_WATT / (4 * math.pi)


def lamp_reach(light):
    """The metres that a point light of Magic reaches: its own range, or the distance at which it falls to the
    illuminance that Magic takes to be none."""
    intensity = light.get('intensity', DEFAULT_INTENSITY)
    return light.get('range', 0) or math.sqrt(max(intensity, 0) / FAINTEST_LUX)


def place_point_light(collection, name, light, world):
    c = light.get('color', {'r': 1, 'g': 1, 'b': 1})
    data = bpy.data.lights.new(name, 'POINT')
    data.color = (c['r'], c['g'], c['b'])
    data.energy = lamp_watts(light.get('intensity', DEFAULT_INTENSITY))
    data.shadow_soft_size = light.get('radius', DEFAULT_RADIUS)
    add_range_window(data, lamp_reach(light))
    o = bpy.data.objects.new(name, data)
    # The whole matrix, although a point light shines the same whichever way it is turned: the export reads the
    # entity's place back from it.
    o.matrix_world = swap_pose(world)
    collection.objects.link(o)
    return o


def place_sun(collection, name, components, world):
    """A directional light of a domain as a sun lamp at the rotation, the colour and the strength that the domain's own
    files hold, where its entity stands. The import lights a scene with these, and set_hour puts the light of one hour
    in their place. Magic reads the direction of such a light alone, so the place is for a person to find it by."""
    towards, illuminance, _ = read_sun(components['DirectionalLight'], components)
    o = sun_lamp(name, towards, illuminance)
    # The whole matrix in one write, as a point light's is: a rotation written by itself reaches matrix_world, which
    # sun_direction reads back, only at the next time that Blender works the scene out.
    o.matrix_world = Matrix.Translation(swap_pose(world).to_translation()) @ o.matrix_basis
    collection.objects.link(o)
    return o


def sun_lamp(name, towards, illuminance):
    """A sun lamp that shines from a direction in Magic's axes with an illuminance, through the disc that Magic aims
    its shadow rays through."""
    bright = max(max(illuminance), 1e-9)
    data = bpy.data.lights.new(name, 'SUN')
    data.color = tuple(channel / bright for channel in illuminance)
    data.energy = bright / LUMENS_PER_WATT
    data.angle = math.radians(2 * SHADOW_DISC_DEGREES)
    o = bpy.data.objects.new(name, data)
    o.rotation_euler = Vector((towards.x, towards.z, towards.y)).to_track_quat('Z', 'Y').to_euler()
    return o


def sun_direction(o):
    """The direction towards a sun lamp of Blender, in Magic's axes: the inverse of the aim that sun_lamp gives it."""
    towards = o.matrix_world.to_quaternion() @ Vector((0, 0, 1))
    return Vector((towards.x, towards.z, towards.y)).normalized()


def light_values(o):
    """A sun lamp of Blender as the Transform of Magic's directional light, for a lamp that a person turned. Magic's
    light travels along the entity's forward, so the rotation turns Magic's +Z onto the way that the light goes. The
    lamp's own roll is none of Magic's, so a lamp that nobody turned gives the rotation it was read with."""
    forward = -sun_direction(o)
    up = Vector((0, 1, 0))
    right = up.cross(forward)
    right = right.normalized() if right.length > 1e-6 else Vector((1, 0, 0))
    rotation = Matrix((right, forward.cross(right).normalized(), forward)).transposed().to_quaternion()
    return {'rotation': {'x': rotation.x, 'y': rotation.y, 'z': rotation.z, 'w': rotation.w}}


def point_light_as_read(light):
    """A PointLight component as the import read it, with the value that Magic gives each one it leaves out, so that
    the export can tell a value that a person changed from one that was never written."""
    c = light.get('color', {'r': 1, 'g': 1, 'b': 1})
    return {
        'color': {'r': c['r'], 'g': c['g'], 'b': c['b']},
        'intensity': light.get('intensity', DEFAULT_INTENSITY),
        'radius': light.get('radius', DEFAULT_RADIUS),
        'range': lamp_reach(light),
    }


def reach_node(data):
    """The node of a lamp that holds the metres it reaches, or nothing for a lamp that no import of a domain made. A
    lamp of Blender's own holds no value of Magic, so the export names it in the place of writing it."""
    tree = data.node_tree if data.use_nodes else None
    return tree.nodes.get(REACH_NODE) if tree else None


def point_light_values(o):
    """A lamp of Blender as Magic's PointLight component: the inverse of place_point_light. The reach is the one that
    its window node holds, which is the metres that the light reaches."""
    data = o.data
    reach = math.sqrt(reach_node(data).inputs[1].default_value)
    return {
        'color': {'r': data.color[0], 'g': data.color[1], 'b': data.color[2]},
        'intensity': lamp_candela(data.energy),
        'radius': data.shadow_soft_size,
        'range': reach,
    }


def lit_by(sky):
    """The light that lights the world, from what `query sky` prints: the unit direction towards it in Magic's axes,
    its colour times its intensity in lux above the air, and what it is to the sky. Magic picks it by the renderer's
    own rule."""
    lit = sky['lit']
    towards, illuminance = ([value.get(axis, 0) for axis in 'xyz'] for value in (lit['towards'], lit['illuminance']))
    return Vector(towards), Vector(illuminance), lit['mode']


def place_sky(scene, sun, altitude, brightness):
    """Blender's sky and a sun lamp at the light that lights Magic's world: the lamp gives what the light gives the
    ground, its illuminance above the air less what the air takes out, and the sky gives only what its air scatters.
    The Sky component's brightness multiplies the sky alone, as Magic's sky table holds it, and never the lamp."""
    towards, illuminance, _ = sun
    through = air_transmittance(altitude, towards.y)
    ground = Vector([illuminance[channel] * through[channel] for channel in range(3)])
    if max(ground) > 0:
        scene.collection.objects.link(sun_lamp(SUN_NAME, towards, ground))
    sky_world(scene, towards, sky_strength(sun, brightness))


def place_own_sky(scene, sun, brightness):
    """Blender's sky over a domain that holds a Sky entity, lit from the domain's own directional light. A domain with
    no Sky gets no sky: the import invents none."""
    sky_world(scene, sun[0], sky_strength(sun, brightness))


def sky_world(scene, towards, strength):
    """Blender's sky with the light at a direction in Magic's axes."""
    scene.world = bpy.data.worlds.new('Sky')
    tree = scene.world.node_tree
    sky = tree.nodes.new('ShaderNodeTexSky')
    sky.sky_type = 'MULTIPLE_SCATTERING'
    # The lamp gives the light that comes straight through, so the sky draws no disc.
    sky.sun_disc = False
    sky.sun_elevation = math.asin(max(-1, min(1, towards.y)))
    # A rotation of 0 puts the sun toward +Y in Blender, which is Magic's +Z, and 90 degrees toward +X (measured in
    # Blender 5.3).
    sky.sun_rotation = math.atan2(towards.x, towards.z)
    tree.links.new(sky.outputs[0], tree.nodes['Background'].inputs[0])
    tree.nodes['Background'].inputs[1].default_value = strength


def sky_strength(sun, brightness):
    """The strength of Blender's sky for the light of an hour: Magic scales its sky by the light's illuminance above
    the air and by the Sky component's brightness, so this does the same. The export reads a changed brightness back
    through it."""
    return brightness * max(sun[1]) / LUMENS_PER_WATT / SKY_WATTS_AT_ONE
