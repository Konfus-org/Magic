# The components of an entity that Blender's own data model does not hold, as the custom properties of the object that
# the entity put in the scene. Blender draws a custom property as a field of its own kind, so a person edits a value of
# any component with Blender's own fields, and the export writes each one back.
#
# Magic says which components there are, which members each one has and what each member holds: `query components`.
# Nothing of that stands here. One headless run of Magic gives it to an import, and the scene keeps it, so a file that
# opens again needs no run.
import json
import os

# The components that Blender's own data model holds, with where a person edits each one and which of their members
# Blender holds there. The panel names the place instead of showing those members a second time, and draws a field
# for each other member of the component, which the export writes back as it does the member of any other component.
SHOWN = {
    'Transform': ('the place of the object (Object Properties > Transform)', ('position', 'rotation', 'scale')),
    'Renderer': ('the mesh of the object and its material slots', ('model', 'materials')),
    'PointLight': ('the lamp of the object (Object Data Properties)', ('color', 'intensity', 'range', 'radius')),
    'DirectionalLight': ('the way the object is turned, and the lamp of the object', ('color', 'intensity')),
    'Sky': ("the strength of the world's background", ('brightness',)),
    'Tags': ('the tags property of the object', ()),
}

# The scene keeps what `query components` printed, as the text of its JSON.
SCHEMA = 'magic_components'

# The components that Blender shows another way, which the panel names. The object keeps them as the list of their
# names, so that the panel draws without a read of the prefab files.
MAPPED = 'magic_shown'

# A component of an entity is one custom property of its object, `magic.<Component>`, which holds the members that the
# panel has no field for, as JSON; each member that it has a field for is `magic.<Component>.<member>` beside it. The
# property is there when the entity holds the component, and gone when it does not.
MARK = 'magic.'

# The types of a member that the panel draws as a number and as a whole number. A member of another type is one of the
# kinds that the shape of its value names (kind_of).
NUMBERS = {'Single', 'Double', 'Half'}
WHOLE = {'Byte', 'SByte', 'Int16', 'UInt16', 'Int32', 'UInt32', 'Int64', 'UInt64'}

# How Blender draws a vector and a colour of Magic: the axes of a vector of three by their names, and a colour as a
# swatch with the gamma that Blender's own colours have.
SUBTYPES = {'vector': {3: 'XYZ'}, 'colour': {3: 'COLOR_GAMMA', 4: 'COLOR_GAMMA'}}


def read_schema(printed):
    """The component types of what `query components` printed, by name: each type that the run knows, with each
    member's name, type and default value."""
    return {held['type']: held for held in printed['components']}


def keep_schema(scene, schema):
    """Keeps the component types with the scene, for the panel and for the export."""
    scene[SCHEMA] = json.dumps(schema)


def schema_of(scene):
    """The component types that the import of this scene read."""
    return json.loads(scene.get(SCHEMA, '{}'))


def handle_names(assets, root):
    """Each asset's id as the path that the panel shows. The path is under the folder that holds the project and the
    engine, as a person reads it in the project. The export turns it around, to read a path back as an id."""
    return {held: os.path.relpath(path, root).replace(os.sep, '/') for held, (path, meta) in assets.items()}


def kind_of(field):
    """The kind of field that one member of a component is: a choice of a set, a yes or no, a number, a whole number,
    a text, a vector, a colour, or a handle to an asset. It is nothing for a member whose type the panel has no field
    for, such as a struct of its own, which stays as it was read."""
    if 'values' in field:
        return 'choice'
    held, default = field['type'], field['default']
    if held == 'Boolean':
        return 'switch'
    if held in NUMBERS:
        return 'number'
    if held in WHOLE:
        return 'whole'
    if held == 'String':
        return 'text'
    if isinstance(default, dict):
        keys = set(default)
        if keys == {'id'}:
            return 'handle'
        if keys <= {'x', 'y', 'z', 'w'}:
            return 'vector'
        if keys <= {'r', 'g', 'b', 'a'}:
            return 'colour'
    return None


def same_value(one, other):
    """Whether a value that Blender holds is the value that the record holds: the same numbers under the same keys,
    or the same text, choice or yes and no. A number of 32 bits comes back a millionth away from the number that was
    read, which is no change that a person made."""
    if isinstance(one, dict):
        return isinstance(other, dict) and all(same_value(value, other.get(key)) for key, value in one.items())
    if isinstance(one, bool) or isinstance(other, bool):
        return one == other
    if isinstance(one, (int, float)):
        return isinstance(other, (int, float)) and abs(one - other) <= 1e-5 * max(1.0, abs(one))
    return one == other


def reads_as(kind, value, was):
    """same_value for a value of a named kind: the member of a component, and the row of a material's table. A handle
    and a map hold the id of an asset, which is one asset or another, so such a value must be equal: an id near the id
    that was read is another asset, and not a number that came back a millionth away."""
    return value == was if kind in ('handle', 'map') else same_value(value, was)


def as_property(field, value, names):
    """One member of a component as the value of a custom property of Blender."""
    kind = kind_of(field)
    default = field['default']
    if kind == 'choice':
        return field['values'].index(value) if value in field['values'] else 0
    if kind == 'switch':
        return bool(value)
    if kind == 'number':
        return float(value)
    if kind == 'whole':
        return int(value)
    if kind == 'text':
        return str(value) if value is not None else ''
    if kind in ('vector', 'colour'):
        return [float(value.get(key, default[key])) for key in default]
    return names.get(value.get('id', 0), str(value.get('id', 0)))


def as_value(field, held, ids):
    """The value of a custom property as the member of a component that a prefab holds, or the reason that it is no
    value of that member."""
    kind = kind_of(field)
    if kind == 'choice':
        values = field['values']
        return (values[held], None) if 0 <= held < len(values) else (None, f'{held} is no value of {values}')
    if kind == 'switch':
        return bool(held), None
    if kind == 'number':
        return float(held), None
    if kind == 'whole':
        return int(held), None
    if kind == 'text':
        return str(held), None
    if kind in ('vector', 'colour'):
        return dict(zip(field['default'], [float(part) for part in held])), None
    if held in ids:
        return {'id': ids[held]}, None
    if str(held).strip('-').isdigit():
        return {'id': int(held)}, None
    return None, f'the project holds no asset at {held}'


def members(name, described):
    """Each member of a component that the panel has a field for. A component that Blender holds its own way keeps out
    the members that Blender holds (SHOWN), whose place the panel names instead."""
    blenders = SHOWN[name][1] if name in SHOWN else ()
    return [field for field in described.get('fields', [])
            if kind_of(field) is not None and field['name'] not in blenders]


def rest_of(name, described, values):
    """The members of a component that the panel has no field for, which the export writes back as they were read."""
    shown = {field['name'] for field in members(name, described)}
    return {member: value for member, value in values.items() if member not in shown}


def key_of(name, field):
    """The custom property that holds one member of one component."""
    return f'{MARK}{name}.{field["name"]}'


def put_one(o, name, described, values, names):
    """Puts one component of an entity on its object: the members that the panel has no field for as the JSON of the
    component's own property, and each member that it has a field for as a property of its own (put_fields)."""
    o[MARK + name] = json.dumps(rest_of(name, described, values))
    put_fields(o, name, described, values, names)


def put_fields(o, name, described, values, names):
    """Each member of a component that the panel has a field for, as a property of its own, with the kind of field
    that Blender draws it as."""
    for field in members(name, described):
        key = key_of(name, field)
        value = values[field['name']] if field['name'] in values else field['default']
        o[key] = as_property(field, value, names)
        ui = o.id_properties_ui(key)
        kind = kind_of(field)
        if kind in SUBTYPES:
            ui.update(subtype=SUBTYPES[kind].get(len(field['default']), 'NONE'))
        if kind == 'choice':
            ui.update(items=[(value, value.replace('_', ' ').title(), '', 'NONE', at)
                             for at, value in enumerate(field['values'])])
        ui.update(description=f'{name}.{field["name"]}, a {field["type"]} of Magic')


def put(o, record, schema, names, unknown):
    """Puts the components of an entity on the object that stands for it: each one that Blender's own data model does
    not hold, as the properties that the panel draws, and the names of the ones that it does hold, each with a field
    for every member that Blender does not hold. A component that this run of Magic does not know goes in unknown, by
    its name, and stays as it was read."""
    components = record.get('components', {})
    o[MAPPED] = [name for name in components if name in SHOWN and name in schema]
    for name, values in components.items():
        described = schema.get(name)
        if name in SHOWN:
            if described is not None and isinstance(values, dict):
                put_fields(o, name, described, values, names)
            continue
        if described is None or not isinstance(values, dict):
            o[MARK + name] = json.dumps(values)
            if described is None:
                unknown.setdefault(name, []).append(o.name)
            continue
        put_one(o, name, described, values, names)


def held(o):
    """The components that an object holds for its entity, which the panel shows: the name of each one."""
    return sorted(key[len(MARK):] for key in o.keys()
                  if key.startswith(MARK) and '.' not in key[len(MARK):])


def add(o, name, described):
    """Puts a component on an object, with the values of its type, which the export writes into its entity."""
    put_one(o, name, described, {}, {}) if isinstance(described['default'], dict) \
        else o.__setitem__(MARK + name, json.dumps(described['default']))


def remove(o, name):
    """Takes a component off an object, which the export takes out of its entity."""
    for key in list(o.keys()):
        if key == MARK + name or key.startswith(f'{MARK}{name}.'):
            del o[key]


def write(record, o, schema, ids):
    """Writes the components that the panel shows back into an entity's record: each value that a person changed, each
    component that a person added and each component that a person took away. A member that the panel has no field
    for, and a component that this run of Magic does not know, stay as they were read. The members that Blender holds
    its own way (SHOWN) are none of this: the export reads each of those from Blender's own data. It gives whether the
    record changed, and the reason for each value that it could not write."""
    components = record.setdefault('components', {})
    changed, faults = False, []
    for name in [name for name in components if name not in SHOWN and MARK + name not in o]:
        del components[name]
        changed = True
    for name in held(o) + [name for name in o.get(MAPPED, []) if name in components]:
        if name not in components:
            components[name] = json.loads(o[MARK + name])
            changed = True
        values = components[name]
        if name not in schema or not isinstance(values, dict):
            continue
        for field in members(name, schema[name]):
            key = key_of(name, field)
            if key not in o:
                continue
            value, fault = as_value(field, o[key], ids)
            if fault is not None:
                faults.append(f'{name}.{field["name"]}: {fault}, so it stays as it was read')
                continue
            if reads_as(kind_of(field), value, values.get(field['name'], field['default'])):
                continue
            values[field['name']] = value
            changed = True
    return changed, faults
