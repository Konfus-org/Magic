# The one camera model of the reference tools: Magic's axes, the projection of a world point into the picture, and the
# answer to one question, does every point of each view stay in the picture at each camera angle. Magic's camera at no
# rotation looks along +Z, with +X right and +Y up. It takes plain numbers, so made-up data tests it. To run those
# tests, run this file with Blender's Python or the system's:
#   python tools/Reference/Camera.py
import math

import numpy as np

import Settings

# The patch that a point is read over, in pixels across and down.
PATCH = 5


def turn(rotation, v):
    """A vector turned by a rotation (x, y, z, w)."""
    q = np.array(rotation[:3])
    return v + 2 * np.cross(q, np.cross(q, v) + rotation[3] * v)


def turn_back(rotation, v):
    return turn([-rotation[0], -rotation[1], -rotation[2], rotation[3]], v)


def field_of_view(view):
    """The vertical field of view of a view in degrees, which is the tests' own value where the view states none."""
    return view.get('fieldOfView', Settings.TESTS['fieldOfView'])


def half_sizes(view, width, height):
    """How far the picture's right edge and its top edge are from its middle, one metre in front of the camera."""
    tangent = math.tan(math.radians(field_of_view(view)) / 2)
    return tangent * width / height, tangent


def yaw_degrees(rotation):
    """How far the camera is turned about up, which names the angle a view is measured at."""
    forward = turn(rotation, np.array([0.0, 0.0, 1.0]))
    return math.degrees(math.atan2(forward[0], forward[2]))


def place(view, pose, point, width, height):
    """A point's place in the picture, from 0 to 1 across and down, and why the camera does not see it, or None when
    it does."""
    p = point['position']
    v = turn_back(pose[3:], np.array([p['x'] - pose[0], p['y'] - pose[1], p['z'] - pose[2]]))
    across, down = half_sizes(view, width, height)
    if v[2] <= 0:
        return 0.5, 0.5, f'{-v[2]:.2f} m behind the camera'
    x, y = 0.5 + 0.5 * v[0] / (v[2] * across), 0.5 - 0.5 * v[1] / (v[2] * down)
    if not (0 <= x <= 1 and 0 <= y <= 1):
        return x, y, f'at {x:.2f}, {y:.2f} of the picture, outside it'
    return x, y, None


def project(view, pose, point, width, height):
    """A point's place in the picture. A point the camera does not see is a fault of the view, not a measurement: the
    patch under it would be read at the picture's edge."""
    x, y, fault = place(view, pose, point, width, height)
    if fault:
        raise SystemExit(f"The point {point['name']!r} of the view {view['name']}, at a camera yaw of "
                         f'{yaw_degrees(pose[3:]):.1f} degrees, is {fault}. Move the point or the ReferenceView '
                         'entity, or lower the angle the view is measured at. --check-points names every such point '
                         'before a render.')
    return x, y


def unproject(view, pose, x, y, depth, width, height):
    """The world position a pixel shows, from its place in the picture from 0 to 1 across and down, and the metres in
    front of the camera that the Depth view gives it."""
    across, down = half_sizes(view, width, height)
    v = np.array([(2 * x - 1) * depth * across, (1 - 2 * y) * depth * down, depth])
    return turn(pose[3:], v) + np.array(pose[:3])


def patch(picture, x, y, width, height):
    """The mean colour of the PATCH by PATCH pixels at a place in the picture, from 0 to 1 across and down."""
    half = PATCH // 2
    cx = min(max(int(x * (width - 1)), half), width - half - 1)
    cy = min(max(int(y * (height - 1)), half), height - half - 1)
    return picture[cy - half:cy + half + 1, cx - half:cx + half + 1].reshape(-1, 3).mean(axis=0)


def check_points(jobs, width, height, margin):
    """Prints how near an edge the points of each view come at each camera angle the tests measure them at, and ends
    with a fault that names every point outside the picture or nearer its edge than the margin. The visual tests run
    it before Magic, so a point that no camera sees costs a second and not a render."""
    faults = []
    for job in jobs:
        view = Settings.view(job['view'])
        nearest, which = 1.0, ''
        places = job['poses']
        for place_of in places:
            label, pose = place_of['label'], place_of['pose']
            for point in view['points']:
                x, y, fault = place(view, pose, point, width, height)
                edge = min(x, y, 1 - x, 1 - y)
                if not fault and edge < margin:
                    fault = f'at {x:.2f}, {y:.2f} of the picture, nearer its edge than the margin {margin:g}'
                if fault:
                    faults.append(f'{view["name"]}, {label}: the point {point["name"]!r} is {fault}.')
                elif edge < nearest:
                    nearest, which = edge, f'{point["name"]!r} {label}'
        nearest_text = f'{which} comes {nearest:.2f} from an edge' if which else 'every point is outside'
        print(f'{view["name"]:<16} {len(view["points"]):2d} points at {len(places)} angle(s): {nearest_text}')
    if faults:
        raise SystemExit('\n'.join(faults) + '\nMove each point or its ReferenceView entity.')


def _tests():
    """The projection on made-up data, with the answer that the data gives it."""
    view = {'name': 'test', 'fieldOfView': 90}  # a view states its own field of view, so no data file is read
    pose = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0]
    width, height = 200, 100
    # A camera that looks along +Z at a 90 degree vertical field of view. A point straight ahead is in the middle; a
    # point nine tenths of a metre up and one metre ahead is near the top; a point behind is seen by no camera.
    for name, position, want in (('ahead', (0, 0, 1), (0.5, 0.5)),
                                 ('near the top', (0, 0.9, 1), (0.5, 0.05)),
                                 ('right edge', (2, 0, 1), (1.0, 0.5))):
        x, y, fault = place(view, pose, {'name': name, 'position': dict(zip('xyz', map(float, position)))},
                            width, height)
        print(f'place {name:<13} {x:.3f}, {y:.3f} (want {want[0]:.3f}, {want[1]:.3f}), fault {fault}')
    x, y, fault = place(view, pose, {'name': 'behind', 'position': {'x': 0.0, 'y': 0.0, 'z': -3.0}}, width, height)
    print(f'place behind        {fault} (want 3.00 m behind the camera)')

    # A point projected and read back at the depth it was found at gives the position it started from.
    there = {'name': 'corner', 'position': {'x': 1.0, 'y': 0.5, 'z': 4.0}}
    x, y = project(view, pose, there, width, height)
    back = unproject(view, pose, x, y, 4.0, width, height)
    print(f'unproject         {back[0]:.3f}, {back[1]:.3f}, {back[2]:.3f} (want 1.000, 0.500, 4.000)')

    # A patch at the middle of a picture reads the pixels there, and a patch at a corner stays inside the picture.
    picture = np.tile(np.arange(width, dtype=np.float64)[None, :, None], (height, 1, 3))
    print(f'patch             middle {patch(picture, 0.5, 0.5, width, height)[0]:.1f} (want 99.0), '
          f'left edge {patch(picture, 0.0, 0.0, width, height)[0]:.1f} (want 2.0)')


if __name__ == '__main__':
    _tests()
