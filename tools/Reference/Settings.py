# The data files that the reference tools and the visual tests both read: the tests' settings and the views. Each file
# is plain JSON with no comments, so a plain parser reads it. No tool holds a copy of a value that these files state.
import json
import os

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))


def _read(name):
    with open(os.path.join(ROOT, 'tools', 'Scripts', name), encoding='utf-8') as f:
        return json.load(f)


# The visual tests' own data: the look, the size of the picture, the limits and the tolerances.
TESTS = _read('VisualTests.json')


def views():
    """The views, their hours and their points, which the tests and the references both read."""
    return _read('ReferenceViews.json')


def view(name):
    """One view by its name."""
    found = next((v for v in views() if v['name'] == name), None)
    if found is None:
        raise SystemExit(f'ReferenceViews.json holds no view named {name!r}.')
    return found


def limit(key):
    """The limit that VisualTests.json states under a key, as a dictionary of min and max."""
    return TESTS['limits'][key]


def picture_size():
    """The width and height that the tests capture at and the references are rendered at."""
    return TESTS['picture']['width'], TESTS['picture']['height']
