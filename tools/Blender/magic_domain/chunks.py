# The chunks of a domain in the scene, and the view distance that makes a large domain turn at speed.
#
# Blender takes time for each object that it draws, and a domain holds tens of thousands of them. The import puts the
# objects of one chunk of Magic's world in a collection of its own, named for the chunk, so that the view distance
# answers for a whole chunk at one time: it leaves the collection out of the view layer, which costs nothing for the
# objects in it, or it draws the objects as boxes.
#
# A collection of a chunk carries the box that the chunk covers, in Blender's axes, so that the distance to it is a
# read of six numbers and no walk over its objects.
import math

from mathutils import Vector

from .project import CHUNK_SIZE, chunk_of_name

# The box that a chunk covers, on the chunk's collection: the lowest corner and the highest, in Blender's axes.
BOX = 'magic_chunk'

# Half the width of the sensor that Blender measures the lens of a viewport over, in metres: the angle that a
# viewport sees is twice the arctangent of this over its lens (View > Lens).
SENSOR_HALF = 0.018

# What the view distance does with the chunks that stand farther away than it.
FAR = [
    ('HIDE', 'Hide', 'Leave the chunk out of the view layer, which costs nothing for the objects in it'),
    ('BOUNDS', 'Bounding Box', 'Draw each object of the chunk as the box that it fills'),
]


def box_of(prefab_name):
    """The box that the chunk of a prefab file covers, in Blender's axes, or nothing when the name is no chunk's. A
    chunk covers CHUNK_SIZE metres on each axis from its corner (the project)."""
    chunk = chunk_of_name(prefab_name)
    if chunk is None:
        return None
    low = Vector((chunk[0], chunk[2], chunk[1])) * CHUNK_SIZE
    return tuple(low) + tuple(low + Vector((CHUNK_SIZE,) * 3))


def distance_to(box, point):
    """How far a point stands from the nearest face of a box, and zero inside it."""
    return math.sqrt(sum(max(box[axis] - point[axis], point[axis] - box[axis + 3], 0.0) ** 2 for axis in range(3)))


def domain_box(view_layer):
    """The box that covers every chunk of the domain under a view layer, in Blender's axes, or nothing while the view
    layer holds no chunk."""
    boxes = [child.collection[BOX] for child in layer_chunks(view_layer.layer_collection)]
    if not boxes:
        return None
    return (tuple(min(box[axis] for box in boxes) for axis in range(3))
            + tuple(max(box[axis + 3] for box in boxes) for axis in range(3)))


def middle_of(box):
    """The point in the middle of a box."""
    return Vector(tuple((box[axis] + box[axis + 3]) / 2 for axis in range(3)))


def reach_of(box, point):
    """How far the farthest corner of a box stands from a point."""
    return math.sqrt(sum(max(abs(point[axis] - box[axis]), abs(point[axis] - box[axis + 3])) ** 2
                         for axis in range(3)))


def clip_for(box, lens):
    """The clip end in metres that draws a whole box from a view which frames it with that lens: Blender frames a
    scene from the reach of it over the sine of half the angle that the lens sees, and the far side of the box stands
    one reach beyond the middle that the view looks at. It is measured from the box and not from the view, which
    Blender moves over several frames while its own Smooth View is on."""
    reach = reach_of(box, middle_of(box))
    return reach + reach / math.sin(math.atan(SENSOR_HALF / (lens * 0.001)))


def layer_chunks(layer):
    """Each layer collection of a chunk of a domain under a layer collection of a view layer."""
    for child in layer.children:
        if BOX in child.collection:
            yield child
        else:
            yield from layer_chunks(child)


def left_out_chunks(view_layer):
    """How many chunks the view layer leaves out now. A render draws no object of such a chunk, so the panel says the
    count. A chunk that draws as boxes is in the view layer, and a render draws its objects whole."""
    return sum(1 for child in layer_chunks(view_layer.layer_collection) if child.exclude)


def cull_chunks(view_layer, point, far, choice):
    """Hides each chunk that stands farther from a point than a distance in metres, and shows every other chunk. The
    choice says how a far chunk goes: out of the view layer, or drawn as boxes. A distance of math.inf shows all of
    them again. It gives back how many chunks it hid."""
    hidden = 0
    for child in layer_chunks(view_layer.layer_collection):
        away = distance_to(child.collection[BOX], point) > far
        if away:
            hidden += 1
        child.exclude = away and choice == 'HIDE'
        # Every object of a chunk draws the same way, since this one loop sets them together: the first object of the
        # collection says what the chunk draws as now, and a chunk that draws that way already costs nothing.
        objects = child.collection.objects
        draws = 'BOUNDS' if away and choice == 'BOUNDS' else 'TEXTURED'
        if objects and objects[0].display_type != draws:
            for o in objects:
                o.display_type = draws
    return hidden
