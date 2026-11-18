# Reads and writes a Magic project: its assets, its prefabs and the entities of a domain, with the axes and the units
# that turn Magic's numbers into Blender's and back. Magic is left-handed with Y up; Blender is right-handed with Z up.
# A point (x, y, z) in Magic is (x, z, y) in Blender, and a model imported by Blender's FBX importer is in Blender's
# axes, so its Magic axes are the same swap.
#
# Each conversion stands here one time and both directions use it, so that the import and the export cannot drift
# apart.
import functools
import hashlib
import json
import os
import re
import subprocess
import tempfile
from collections import namedtuple

import bpy
from mathutils import Matrix, Quaternion, Vector

from .cache import folder as cache_folder
from .cache import user_folder

# The swap between Magic's axes and Blender's, which is its own inverse.
SWAP = Matrix(((1, 0, 0, 0), (0, 0, 1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))

# Lumens to a watt of light, which turns Magic's photometric values into Blender's radiometric ones.
LUMENS_PER_WATT = 683

# Metres that a chunk of a domain covers on each axis: Domain.ChunkSize.
CHUNK_SIZE = 128

# The folders under a project that hold no asset and no prefab: what a build and git keep there.
SKIPPED = ('bin', 'obj', 'Build', '.git')

# Decimal places that a written number keeps: shorter than a millimetre over the world's widest coordinate, and short
# enough that the files stay readable.
PLACES = 6

# Where a person sets the Magic program that a run starts. The add-on starts the program that it is given and builds
# nothing.
MAGIC_UNSET = ('Set one in Edit > Preferences > Add-ons > Magic Domain > Magic Executable, give one to a script with '
               '--magic, or set the MAGIC_EXE environment variable.')

# Where a record stands: the prefab file that holds it, and the way down to it, which is its place in the file's
# entities and then its place in each children list under it.
Address = namedtuple('Address', 'file path')

# An entity in the scene: its record, its components, where it stands in Magic's axes, and the address of the record.
Placement = namedtuple('Placement', 'entity components world where')


def swap_axes(world):
    """A world matrix of Magic's axes in Blender's, and back: the swap is its own inverse. It mirrors, since the two
    hands differ, and the mirror in a model's mesh undoes it (the meshes)."""
    return SWAP @ world


def swap_pose(world):
    """Where an object with no mesh stands, from Magic's axes in Blender's, and back: the swap is its own inverse.
    It turns the basis as well as the axes, so that a left hand becomes a right hand and the object is not mirrored.
    A marker and a lamp use it; a mesh cannot, since its points carry the mirror."""
    return SWAP @ world @ SWAP


def read_jsonc(path):
    """Reads a JSON file with // comments. A file that is plain JSON, as every chunk of a domain is, is read by the
    reader of the library, which is the difference between a minute and an hour over a domain of a hundred thousand
    entities."""
    text = open(path, encoding='utf-8-sig').read()
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        pass
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


@functools.lru_cache(maxsize=None)
def read_prefab(path):
    """A prefab's records, read one time. A domain of a forest names one tree prefab from tens of thousands of
    instances, and reading it again for each of them is minutes of a walk over the domain. A caller that reads the
    files again after a change on disk clears the cache first."""
    return read_jsonc(path)


def rounded(value):
    """The value with each of its numbers cut to the places that a file keeps, so that a number which only the
    arithmetic of a matrix changed is written as it was read."""
    if isinstance(value, float):
        return round(value, PLACES)
    if isinstance(value, dict):
        return {key: rounded(item) for key, item in value.items()}
    if isinstance(value, list):
        return [rounded(item) for item in value]
    return value


def comment_lines(path):
    """How many // comment lines a file holds. A write from the records does not hold them, so the caller that writes
    one again says in its report that they are lost."""
    return sum(1 for line in open(path, encoding='utf-8-sig') if line.lstrip().startswith('//'))


def write_prefab(path, data):
    """Writes a prefab: one entity to a line, which keeps the file readable and its differences short. A // comment of
    the file that the data was read from is not in the data and is lost, so a caller that changed nothing copies the
    file instead."""
    lines = [json.dumps(rounded(entity), separators=(', ', ': '), ensure_ascii=False)
             for entity in data.get('entities', [])]
    body = ['    "entities": [\n' + ',\n'.join('        ' + line for line in lines) + '\n    ]']
    body += [f'    {json.dumps(key)}: {json.dumps(rounded(value), indent=4)}'
             for key, value in data.items() if key != 'entities']
    with open(path, 'w', encoding='utf-8', newline='\n') as file:
        file.write('{\n' + ',\n'.join(body) + '\n}\n')


def magic_path(magic=None):
    """The Magic program that a run starts: the one that the caller names, then the add-on's preferences, then the
    MAGIC_EXE environment variable. The add-on builds nothing and knows no folder of its own, so a run with none of
    the three set is an error that says where to set one.

    The preferences hold none while the add-on is not installed, as it is not in a script that imports the package
    from its folder."""
    addon = bpy.context.preferences.addons.get(__package__)
    preference = addon.preferences.executable if addon else None
    named = next((path for path in (magic, preference, os.environ.get('MAGIC_EXE')) if path), None)
    if not named:
        raise ValueError(f'This needs a Magic program and none is set. {MAGIC_UNSET}')
    program = bpy.path.abspath(named)
    if not os.path.isfile(program):
        raise ValueError(f'There is no Magic program at {program}. {MAGIC_UNSET}')
    return program


def kill_tree(process):
    """Ends a run of Magic, and every process that the run started."""
    if os.name == 'nt':
        subprocess.run(['taskkill', '/T', '/F', '/PID', str(process.pid)], capture_output=True)
    else:
        process.kill()
    process.wait()


def run_magic(project_path, lines, seconds=600, magic=None):
    """Runs Magic on the project with no window and gives it the console lines. The run ends at the first error that
    Magic logs, and that is an error here too. A line that needs an answer writes it to a file with --out. A run still
    going at the time limit is killed."""
    arguments = [magic_path(magic), 'open', os.path.abspath(project_path), '--headless', 'true', '--fail-on-error']
    started = subprocess.Popen(arguments, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, text=True)
    try:
        printed = started.communicate('\n'.join([*lines, 'exit']) + '\n', timeout=seconds)[0]
    except subprocess.TimeoutExpired:
        kill_tree(started)
        raise ValueError(f'Magic ran {lines} on {project_path} for more than {seconds} s, so the run was ended.')
    if started.returncode != 0:
        raise ValueError(f'Magic did not run {lines} on {project_path}:\n{printed[-4000:]}')


def project_state(project_path):
    """What an answer of Magic depends on in the project: the project file and each prefab under its folder, by path
    and by the time it was written. A query reads these files, so its answer stands while none of them changed."""
    folder = os.path.dirname(os.path.abspath(project_path))
    files = [os.path.abspath(project_path)]
    for top, dirs, names in os.walk(folder):
        dirs[:] = [name for name in dirs if name not in SKIPPED]
        files += [os.path.join(top, name) for name in names if name.endswith('.prefab')]
    return [[path, os.path.getmtime(path)] for path in sorted(files)]


def file_state(paths, under):
    """The state of files that an answer of Magic depends on: each one by its name under a folder, its size and the
    time it was written. A file that is gone while this reads it is left out, and the run answers again."""
    held = []
    for path in sorted(paths):
        try:
            held.append([os.path.relpath(path, under).replace(os.sep, '/'),
                         os.path.getsize(path), os.path.getmtime(path)])
        except OSError:
            continue
    return held


def program_state(program):
    """What an answer of Magic depends on in the build that answers it: the program, and each .dll under its folder,
    which is where the gems and the project's own code stand (Projects/GemRegistry.cs). A gem that was built again
    answers another way, and the program's own file does not change with it."""
    folder = os.path.dirname(os.path.abspath(program))
    found = [os.path.abspath(program)]
    for top, dirs, names in os.walk(folder):
        found += [os.path.join(top, name) for name in names if name.endswith('.dll')]
    return file_state(set(found), folder)


def settings_state(project_path):
    """What an answer of Magic depends on in the settings of the project: each file that the engine keeps for this
    user under the project's name (Configuration/Settings.cs). A `set` of a run before this one stands there, and a
    query answers by it."""
    name = json.load(open(project_path, encoding='utf-8-sig')).get('name', '')
    folder = os.path.join(user_folder(), name, 'Settings')
    if not os.path.isdir(folder):
        return []
    return file_state([os.path.join(folder, held) for held in os.listdir(folder)], folder)


def answers_entry(project_path, steps, program):
    """The file that holds the answers of one run of queries, under the cache folder of the engine: its key holds the
    build that answered, the steps that were asked and the state of the project's files, so that the answers of
    another Magic, of a gem that was built again, of other steps, of other settings or of a project that changed are
    never read."""
    key = json.dumps([program, program_state(program), [list(step) for step in steps],
                      project_state(project_path), settings_state(project_path)])
    return os.path.join(cache_folder(), f'answers-{hashlib.sha1(key.encode()).hexdigest()[:16]}.json')


def read_answers(entry):
    """The answers that an entry holds, or nothing while no entry holds them or the file is damaged. A run answers
    again either way."""
    try:
        with open(entry, encoding='utf-8') as file:
            return json.load(file)
    except (OSError, ValueError):
        return None


def write_answers(entry, answers):
    """Keeps the answers of a run as its entry. The write goes to a name of its own and takes the entry's place in one
    step, so that a run which ends in the middle leaves no half entry behind."""
    os.makedirs(os.path.dirname(entry), exist_ok=True)
    half = f'{entry}.{os.getpid()}'
    with open(half, 'w', encoding='utf-8') as file:
        json.dump(answers, file)
    os.replace(half, entry)


def run_queries(project_path, steps):
    """What each query of Magic answers, in the order asked. A step is the console lines to run and the query to
    answer, as ('query components',) or ('set Day.Hour 12', 'wait 2s', 'query DeferredRendering.Sun'). One headless
    run of Magic answers every step: each query writes its JSON to a file with --out, and this reads it back. The
    answers are kept, so the same steps on a project that nothing changed start Magic no second time."""
    program = magic_path()
    entry = answers_entry(project_path, steps, program)
    kept = read_answers(entry)
    if kept is not None:
        return kept
    answers = []
    with tempfile.TemporaryDirectory() as folder:
        lines, files = ['wait loaded'], []
        for at, step in enumerate(steps):
            out = os.path.join(folder, f'{at}.json')
            files.append(out)
            lines += [*step[:-1], f'{step[-1]} --out "{out}"']
        run_magic(project_path, lines, magic=program)
        for out in files:
            with open(out, encoding='utf-8') as file:
                answers.append(json.load(file))
    write_answers(entry, answers)
    return answers


def find_root(project_path):
    """The folder that holds both the project and the engine: the first folder over the project that holds a src
    folder. A project outside such a folder keeps its own folder, and reaches the engine's assets no more."""
    folder = os.path.dirname(os.path.abspath(project_path))
    while True:
        if os.path.isdir(os.path.join(folder, 'src')):
            return folder
        above = os.path.dirname(folder)
        if above == folder:
            return os.path.dirname(os.path.abspath(project_path))
        folder = above


def index_assets(root):
    """Each asset's id, from its .meta file, to the path of the file beside it. The root is the folder that holds the
    project and the engine, since a project uses the assets of both."""
    assets = {}
    starts = [os.path.join(root, folder) for folder in ('src', 'samples') if os.path.isdir(os.path.join(root, folder))]
    for start in starts or [root]:
        for top, dirs, files in os.walk(start):
            dirs[:] = [name for name in dirs if name not in SKIPPED]
            for name in files:
                if name.endswith('.meta'):
                    path = os.path.join(top, name)
                    meta = read_jsonc(path)
                    if 'id' in meta:
                        assets[meta['id']] = (path[:-5], meta)
    return assets


def magic_matrix(transform):
    """A Transform component as a matrix."""
    p = transform.get('position', {})
    r = transform.get('rotation', {})
    s = transform.get('scale', {})
    rotation = Quaternion((r.get('w', 1), r.get('x', 0), r.get('y', 0), r.get('z', 0)))
    scale = Matrix.Diagonal((s.get('x', 1), s.get('y', 1), s.get('z', 1), 1))
    return Matrix.Translation((p.get('x', 0), p.get('y', 0), p.get('z', 0))) @ rotation.to_matrix().to_4x4() @ scale


def transform_values(matrix):
    """A matrix as a Transform component: the inverse of magic_matrix. A matrix that mirrors or shears is no position,
    rotation and scale, and comes back turned."""
    position, rotation, scale = matrix.decompose()
    return {
        'position': {'x': position.x, 'y': position.y, 'z': position.z},
        'rotation': {'x': rotation.x, 'y': rotation.y, 'z': rotation.z, 'w': rotation.w},
        'scale': {'x': scale.x, 'y': scale.y, 'z': scale.z},
    }


def same_matrix(one, other):
    """Whether two matrices stand for the same place. A matrix of Blender holds floats of 32 bits, so a place three
    kilometres from the origin comes back a fifth of a millimetre away; the tolerance grows with the widest number of
    the matrix, which makes it a millimetre there and finer near the origin. Measured over the cabin domain, the
    widest difference of a matrix that nothing moved is a quarter of that tolerance."""
    widest = max(max(abs(matrix[row][column]) for row in range(4) for column in range(4)) for matrix in (one, other))
    return all(abs(one[row][column] - other[row][column]) <= 1e-6 * max(1.0, widest)
               for row in range(4) for column in range(4))


def file_path(path):
    """One spelling of a file's path, so that the address of a record is the same whichever way a caller named the
    folders over it."""
    return os.path.abspath(path)


def path_text(path):
    """A record's way down as one string, which an object of Blender holds as a property."""
    return '.'.join(str(step) for step in path)


def path_of(text):
    """The way down that path_text wrote."""
    return tuple(int(step) for step in text.split('.')) if text else ()


def record_at(data, path):
    """The record at a way down in a prefab's data."""
    record = data['entities'][path[0]]
    for step in path[1:]:
        record = record['children'][step]
    return record


def chunk_of_name(prefab_name):
    """The chunk that a prefab's name names, or nothing when the name is no chunk's."""
    parts = os.path.splitext(os.path.basename(prefab_name))[0].split('_')
    if len(parts) != 3:
        return None
    try:
        return tuple(int(part) for part in parts)
    except ValueError:
        return None


def chunk_corner(prefab_name):
    """Where a prefab of a domain stands: a name of three whole numbers is that chunk, at its corner, and any other
    name is a prefab at the domain's origin (the domain importer)."""
    chunk = chunk_of_name(prefab_name)
    return Matrix.Translation(Vector(chunk) * CHUNK_SIZE) if chunk else Matrix.Identity(4)


def chunk_at(position):
    """The chunk that a point of Magic's world belongs to: the inverse of chunk_corner."""
    return tuple(int(position[axis] // CHUNK_SIZE) for axis in range(3))


def chunk_name(chunk):
    """The prefab file of a chunk."""
    return '{}_{}_{}.prefab'.format(*chunk)


def own_walk(record, world, where):
    """Each entity that one record of a prefab places itself: the record, and its children under it. It does not go
    into a prefab that the record brings in, whose entities stand in that prefab and are the same for every instance
    of it."""
    yield Placement(record, record.get('components', {}), world, where)
    for index, child in enumerate(record.get('children', [])):
        child_where = Address(where.file, where.path + (index,))
        yield from own_walk(child, world @ magic_matrix(child.get('components', {}).get('Transform', {})), child_where)


def own_placements(file, data, parent):
    """Each entity of one prefab file that the file itself places, with the world matrix of each in Magic's axes. A
    record that brings a prefab in is one placement of its own, and the prefab stays closed."""
    for index, record in enumerate(data['entities']):
        world = parent @ magic_matrix(record.get('components', {}).get('Transform', {}))
        yield from own_walk(record, world, Address(file_path(file), (index,)))


def prefab_of(assets, record):
    """The prefab file that a record brings in, or nothing when it brings none."""
    return file_path(assets[record['prefab']['id']][0]) if 'prefab' in record else None


def model_ids(assets, datas):
    """The id of each model that a domain draws, in the order that its entities first draw it, each prefab that the
    domain brings in opened one time. An import reads its models before it places the entities, so that reading one
    model is a part of its own with the model's name."""
    found, opened = [], set()

    def walk(records):
        for record in records:
            model = record.get('components', {}).get('Renderer', {}).get('model')
            if model is not None and model['id'] not in found:
                found.append(model['id'])
            inner = prefab_of(assets, record)
            if inner is not None and inner not in opened:
                opened.add(inner)
                walk(read_prefab(inner)['entities'])
            walk(record.get('children', []))

    for data in datas:
        walk(data['entities'])
    return found


def domain_prefabs(folder):
    """The prefab files of a domain, in the order that a walk of the domain reads them."""
    return sorted(name for name in os.listdir(folder) if name.endswith('.prefab'))


def domain_folder(assets, project_path, domain=None):
    """The folder of a domain of a project: the one the project opens, or the one with that name under the project's
    Domains folder."""
    if domain:
        folder = os.path.join(os.path.dirname(project_path), 'Domains', domain)
        if not os.path.isdir(folder):
            raise ValueError(f'The project has no domain named {domain} ({folder}).')
        return folder
    project = json.load(open(project_path, encoding='utf-8-sig'))
    return os.path.dirname(assets[project['entryPoint']['id']][0])


def domain_names(project_path):
    """The domains that a person opens by name: the folders under the project's Domains folder."""
    folder = os.path.join(os.path.dirname(project_path), 'Domains')
    if not os.path.isdir(folder):
        return []
    return sorted(name for name in os.listdir(folder) if os.path.isdir(os.path.join(folder, name)))
