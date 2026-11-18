# A Blender add-on that reads and writes a domain of a Magic project. File > Import > Magic opens a .magic project and
# builds one of its domains, with the light that the domain's own files hold; File > Export > Magic writes the scene
# back as that domain. A render script puts the light of one hour in its place with set_hour.
#
# A script uses the same functions with no install: put the folder over this one on the path and import the package.
#
#   import sys
#   sys.path.insert(0, 'tools/Blender')
#   from magic_domain import import_domain, export_domain, set_hour
#
# The scene holds every entity, mesh, transform and material that Magic draws, with the same maps and values, the
# lamps, the sun and the sky. These cannot be the same, so a difference at one of them can come from this add-on and
# not from Magic:
#
# - The sun's disc. Magic draws it over its solid angle and then dims it to a twentieth (Atmosphere.slang, "* 0.05"),
#   which no physical disc does. This add-on draws no disc, so the sun in frame is black here and bright in Magic.
# - The sky's colour. Magic's sky is its own air, scaled by the light's colour times its intensity. Blender's sky is
#   Blender's air, and only its brightness can be set, so its tint at a low sun and at night is its own. The brightness
#   here is the light's brightest channel. Magic scales the sky the camera sees by all three (Atmosphere.slang's
#   Evaluate) and the sky's light on the ground by the red one alone (Scene/Lights.slang's GetOpenSkyRadiance), which
#   under the moon's blue light is 1.33 times apart.
# - A point light near its bulb. Magic takes the distance to the bulb's centre, no nearer than its radius; Cycles
#   takes the distance to a point on the bulb's face. Past about ten radii the two agree to under a percent.
# - The soft edge of a shadow. Magic aims every shadow ray through a disc of one degree, about four times the sun's own
#   (Scene/Lights.slang), and this add-on gives the sun lamp the same two degrees across, for the sun and for the moon.
# - The flicker of the fire and the lantern. A script drives it each frame in Magic; the light here is the one in the
#   prefab.
# - The moon's disc and the stars. Magic draws neither, although DirectionalLight.cs says the sky draws the moon full,
#   so neither does this add-on.
# - The transmittance of the air. Magic reads it from a table of 256 by 64 and interpolates; this add-on integrates it
#   in the 40 steps that the table's own entries are made in (Atmosphere.slang, GetAirTransmittance).
import math
import os
import time

import bpy
from bpy.props import EnumProperty, FloatProperty, StringProperty
from bpy_extras.io_utils import ImportHelper

from .cache import folder as cache_folder
from .domain import Build, Domain, Report, export_domain, import_domain, set_hour
from .project import domain_folder, domain_names, find_root, index_assets

__all__ = ['Build', 'Domain', 'Report', 'export_domain', 'import_domain', 'set_hour']

# The choice that stands for the domain that the project opens, which no folder can be named.
ENTRY = '*'

# What an import does with the objects that the scene holds. In a window KEEP is the first default, because an import
# that takes a person's work away without a word is the worse fault; after that, the choice of the last import is the
# default. With no window there is nobody to ask and a script wants the domain alone, so an import clears unless the
# caller sets the property itself.
OBJECTS = [
    ('KEEP', 'Keep Objects', 'Add the domain to the objects that the scene holds'),
    ('CLEAR', 'Clear Scene', 'Take the objects of the scene away, and the data that only they used'),
]

# Seconds between the ticks of an import, and the seconds of each tick that it builds in. Blender draws the status
# bar and answers between two slices, so the bar moves about four times a second and Esc stops the import within a
# quarter of a second. The import builds the domain out of the view layer, so a tick draws the bar alone and the
# window import takes the time of the plain call.
TICK = 0.05
SLICE = 0.2

# Blender's icon for each kind of report, and the order that the result dialog draws the kinds in: what the import
# built, then its warnings, then its errors. Blender's own Info editor draws a report with the same icons.
ICONS = {'INFO': 'INFO', 'WARNING': 'ERROR', 'ERROR': 'CANCEL'}
ORDER = ['INFO', 'WARNING', 'ERROR']


class Result:
    """What one import said: one row for each thing it says, with the kind of report that gives the row its icon, and
    the clip end that draws the whole domain where a viewport draws too near. The import hands this to the result
    dialog as it ends."""

    def __init__(self):
        self.rows = []
        self.clip_end = 0.0


def entries():
    """How many meshes of models the add-on keeps now (cache.py). An import that reads a model from its file keeps the
    mesh, which takes seconds, so a count that went up says why the import was slow."""
    return len(os.listdir(cache_folder())) if os.path.isdir(cache_folder()) else 0


def distance(value):
    """A distance in metres as a person reads it: kilometres over a kilometre, and metres under one."""
    return f'{value / 1000:,.1f} km' if value >= 1000 else f'{value:,.0f} m'


def round_up(value):
    """The clip end for a distance, as a person sets one by hand: the next power of ten over it, so 4.2 km gives
    10,000 m."""
    return 10.0 ** math.ceil(math.log10(value))


def view_point(space):
    """Where a 3D viewport looks from, in Blender's axes. The clip end is the distance from here."""
    return space.region_3d.view_matrix.inverted().translation


def far_warning(context, scene):
    """The warning for a domain that the 3D viewports draw too near of, with the clip end that draws all of it, or
    nothing while every viewport draws far enough. A new Blender draws to 1000 m and looks at the world's origin, so a
    domain of kilometres, or one that stands kilometres away from the origin, shows nothing until the clip end goes up.

    It measures the places of the objects alone: a walk over the points of every mesh costs seconds over tens of
    thousands of objects, and the places give the reach of the domain to the metre."""
    spaces = [area.spaces.active for area in context.screen.areas if area.type == 'VIEW_3D'] if context.screen else []
    places = [o.matrix_world.translation for o in scene.collection.objects]
    if not spaces or not places:
        return None
    width = max(max(place[axis] for place in places) - min(place[axis] for place in places) for axis in range(3))
    far = max((place - view_point(space)).length for space in spaces for place in places)
    draws = min(space.clip_end for space in spaces)
    if width <= draws and far <= draws:
        return None
    value = round_up(far)
    # One short line each: a label of a dialog does not wrap, and Blender cuts a line that is too long for the dialog.
    return ((f'The domain is {distance(width)} wide and reaches {distance(far)} from the viewport, '
             f'which draws {distance(draws)}.',
             'The viewport shows nothing of the domain that lies farther away than that.',
             f'In the 3D viewport press N, open the View tab, and raise Clip End to {value:,.0f} m.',
             'Then View > Frame All (the Home key) brings the domain into view.'), value)


def redraw_status(context):
    """Asks Blender to draw the status bar again, so that the bar of an import moves. Setting the status text is what
    tags it, since the status bar is no area of a screen: measured over 60 ticks, this draws the bar 59 times and a
    tag of the areas draws it none."""
    context.workspace.status_text_set(None)


def clear_scene(scene):
    """Takes the objects of a scene away, with the collections that hold them, the data that only they used and the
    record of the domain that an import left, so that an import after this gives the same scene as an import into a
    new file. It is Blender's own Clean Up > Purge over the objects of one scene."""
    for o in list(scene.collection.all_objects):
        bpy.data.objects.remove(o, do_unlink=True)
    for collection in list(scene.collection.children):
        bpy.data.collections.remove(collection)
    for key in ('magic_project', 'magic_folder'):
        if key in scene:
            del scene[key]
    bpy.ops.outliner.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)


def domain_choices(self, context):
    """The domains that the picked project holds, the one it opens first."""
    names = domain_names(self.filepath) if self.filepath.endswith('.magic') else []
    return [(ENTRY, 'The one the project opens', '')] + [(name, name, '') for name in names]


class MAGIC_OT_import_ask(bpy.types.Operator):
    """Build a domain of a Magic project in this scene"""

    bl_idname = 'magic.import_ask'
    bl_label = 'Magic (.magic)'
    bl_options = {'REGISTER'}

    objects: EnumProperty(name='Objects', description='What to do with the objects that the scene holds',
                          items=OBJECTS, default='KEEP')

    def invoke(self, context, event):
        """The question, in Blender's own dialog: it stands before the file browser, because a clear takes data away.
        Blender keeps the choice of the last import and opens the dialog on it."""
        return context.window_manager.invoke_props_dialog(self, width=360, title='Import a Magic domain',
                                                          confirm_text='Continue')

    def draw(self, context):
        count = len(context.scene.collection.all_objects)
        self.layout.label(text=f'This scene holds {count:,} object{"" if count == 1 else "s"}.')
        self.layout.prop(self, 'objects', expand=True)

    def execute(self, context):
        bpy.ops.magic.import_domain('INVOKE_DEFAULT', objects=self.objects)
        return {'FINISHED'}


class MAGIC_OT_import_domain(bpy.types.Operator, ImportHelper):
    """Build a domain of a Magic project in this scene"""

    bl_idname = 'magic.import_domain'
    bl_label = 'Magic (.magic)'
    bl_options = {'REGISTER', 'UNDO'}

    filename_ext = '.magic'
    filter_glob: StringProperty(default='*.magic', options={'HIDDEN'})
    domain: EnumProperty(name='Domain', description='The domain to build', items=domain_choices)
    objects: EnumProperty(name='Objects', description='What to do with the objects that the scene holds',
                          items=OBJECTS, default='KEEP')

    def clears(self):
        """Whether the import takes the objects of the scene away: what the property says, and with no window what it
        says only when the caller set it, since nobody was asked."""
        if bpy.app.background and not self.properties.is_property_set('objects'):
            return True
        return self.objects == 'CLEAR'

    def execute(self, context):
        self.result = Result()
        self.started = time.monotonic()
        self.kept = entries()
        if self.clears():
            clear_scene(context.scene)
        elif 'magic_folder' in context.scene:
            # An export writes one domain, since the scene keeps the record of one project and one folder. It knows
            # the records of that folder alone, so each object of the domain before this one is a new entity to it.
            # The person hears it here, and not after the export wrote the two domains into one.
            before = os.path.basename(context.scene['magic_folder'])
            self.say('WARNING', f'The scene already holds the domain {before}. An export writes one domain: it would '
                                f'write the objects of {before} into the domain that comes in now, as new entities. '
                                f'Import a second domain into a file of its own.')
        self.build = Build(self.filepath, None if self.domain == ENTRY else self.domain, context.scene)
        if bpy.app.background:
            # Blender in the background has no event loop to tick a timer and no bar to draw: a script that calls the
            # operator there builds the domain in one step.
            self.build.run(math.inf)
            self.say_what_it_built(context)
            return {'FINISHED'}
        bpy.types.STATUSBAR_HT_header.append(self.draw_bar)
        self.timer = context.window_manager.event_timer_add(TICK, window=context.window)
        context.window_manager.modal_handler_add(self)
        return {'RUNNING_MODAL'}

    def draw_bar(self, header, context):
        """The progress of the import in the status bar, where Blender draws the progress of its own long jobs. It is
        Blender's own bar: the part of the work that is done, what is built now, and the key that stops it."""
        layout = header.layout
        layout.separator_spacer()
        layout.progress(factor=self.build.factor, text=f'Magic: {self.build.text}', type='BAR')
        layout.label(text='Esc to stop')
        layout.separator_spacer()

    def modal(self, context, event):
        """One tick of the import: it builds for part of the tick and gives the rest to Blender, so that a person
        turns the view while the domain fills in. Esc stops it and takes away what is built."""
        if event.type == 'ESC':
            self.build.drop()
            self.end(context)
            self.say('WARNING', 'The import stopped, and what it built is taken away.')
            self.show_result()
            return {'CANCELLED'}
        if event.type != 'TIMER':
            return {'PASS_THROUGH'}
        try:
            done = self.build.run(time.monotonic() + SLICE)
        except (OSError, ValueError, KeyError) as failure:
            self.build.drop()
            self.end(context)
            self.say('ERROR', str(failure))
            self.show_result()
            return {'CANCELLED'}
        redraw_status(context)
        if not done:
            return {'RUNNING_MODAL'}
        self.end(context)
        self.say_what_it_built(context)
        self.show_result()
        return {'FINISHED'}

    def say(self, kind, *lines):
        """One row of the result: Blender's report, so that the Info editor, the status bar and a console hold it, and
        the record that the result dialog draws at the end. A row of more than one line draws one label each."""
        self.report({kind}, ' '.join(lines))
        self.result.rows.append((kind, lines))

    def say_what_it_built(self, context):
        """What the import put in the scene, and what it knows that a person should hear: the models that it read from
        their files, which is why a first import is slow, and a domain that the viewports draw too near of."""
        domain = self.build.domain
        drawn = sum(1 for o in domain.scene.collection.objects if o.type != 'EMPTY' or o.instance_collection)
        self.say('INFO', f'{os.path.basename(domain.scene["magic_folder"])}: {drawn:,} objects drawn, '
                         f'{len(self.build.prefabs)} prefabs, {len(domain.markers)} markers, '
                         f'{len(domain.suns)} lights, in {time.monotonic() - self.started:.1f} s')
        read = entries() - self.kept
        if read > 0:
            self.say('INFO', f'{read} of the models were read from their files, which takes seconds each, and their '
                             f'meshes are kept: the next import of this domain appends them.')
        far = far_warning(context, domain.scene)
        if far:
            lines, self.result.clip_end = far
            self.say('WARNING', *lines)

    def show_result(self):
        """The result of the import in Blender's own dialog, which opens over the viewport as the import ends."""
        MAGIC_OT_import_result.shown = self.result
        bpy.ops.magic.import_result('INVOKE_DEFAULT')

    def end(self, context):
        """Takes away the timer and the bar of an import that ended, whichever way it ended: no bar is left in the
        status bar after an import that finished, that a person stopped or that failed."""
        context.window_manager.event_timer_remove(self.timer)
        bpy.types.STATUSBAR_HT_header.remove(self.draw_bar)
        redraw_status(context)


class MAGIC_OT_view_far(bpy.types.Operator):
    """Raise Clip End of the 3D viewports of this screen, and frame the whole scene in them"""

    bl_idname = 'magic.view_far'
    bl_label = 'Set Clip End and Frame All'
    bl_options = {'INTERNAL'}

    clip_end: FloatProperty(name='Clip End', subtype='DISTANCE')

    def execute(self, context):
        """It changes the view alone and writes no undo step, as Blender's own View > Frame All does."""
        for area in context.screen.areas:
            if area.type != 'VIEW_3D':
                continue
            space = area.spaces.active
            space.clip_end = self.clip_end
            for region in area.regions:
                if region.type == 'WINDOW':
                    with context.temp_override(area=area, region=region):
                        bpy.ops.view3d.view_all()
            # Frame All frames every object as Blender draws it. One model that is kilometres wide, such as a terrain,
            # reaches far past the places of the domain and puts the view that far back. The whole scene stands inside
            # twice the distance that the view frames from, so the clip end goes there where that is farther. It goes
            # no farther: a clip end past what the view needs takes the depth that Blender draws the near things with.
            space.clip_end = max(space.clip_end, 2 * space.region_3d.view_distance)
        return {'FINISHED'}


class MAGIC_OT_import_result(bpy.types.Operator):
    """What the import built, with its warnings and its errors"""

    bl_idname = 'magic.import_result'
    bl_label = 'Magic import'
    bl_options = {'INTERNAL'}

    # The result that the import which just ended hands over. One import runs at a time, and the dialog opens as that
    # import ends.
    shown = Result()

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self, width=560, title='Magic import',
                                                          confirm_text='Close')

    def draw(self, context):
        column = self.layout.column()
        for kind, lines in sorted(self.shown.rows, key=lambda row: ORDER.index(row[0])):
            for number, line in enumerate(lines):
                column.label(text=line, icon=ICONS[kind] if number == 0 else 'BLANK1')
        if self.shown.clip_end:
            self.layout.operator(MAGIC_OT_view_far.bl_idname, icon='VIEW3D').clip_end = self.shown.clip_end

    def execute(self, context):
        return {'FINISHED'}


class MAGIC_OT_export_domain(bpy.types.Operator):
    """Write this scene back as the domain of a Magic project that it came from"""

    bl_idname = 'magic.export_domain'
    bl_label = 'Magic (.magic)'
    bl_options = {'REGISTER'}

    project: StringProperty(name='Project', description='The .magic file of the project', subtype='FILE_PATH')
    domain: StringProperty(name='Domain', description='The domain to write')
    folder: StringProperty(name='To folder', description='Where to write the prefabs; empty writes over the domain '
                                                         'that the scene came from', subtype='DIR_PATH')

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self, width=420)

    def draw(self, context):
        folder = context.scene.get('magic_folder')
        if folder:
            self.layout.label(text=f'Overwrite the domain {os.path.basename(folder)} of '
                                   f'{os.path.basename(context.scene["magic_project"])}?')
            self.layout.prop(self, 'folder')
            return
        self.layout.label(text='This scene came from no import: name the project and the domain.')
        self.layout.prop(self, 'project')
        self.layout.prop(self, 'domain')
        self.layout.prop(self, 'folder')

    def execute(self, context):
        scene = context.scene
        try:
            if 'magic_folder' not in scene:
                assets = index_assets(find_root(self.project))
                scene['magic_project'] = os.path.abspath(bpy.path.abspath(self.project))
                scene['magic_folder'] = domain_folder(assets, scene['magic_project'], self.domain or None)
            report = export_domain(scene, bpy.path.abspath(self.folder) if self.folder else None)
        except (OSError, ValueError, KeyError) as failure:
            self.report({'ERROR'}, str(failure))
            return {'CANCELLED'}
        for line in report.lines():
            print(line)
        self.report({'WARNING'} if report.skipped else {'INFO'}, '; '.join(report.lines()))
        return {'FINISHED'}


def import_item(self, context):
    """File > Import > Magic: the question first when the scene holds objects, the file browser alone when the scene
    is empty."""
    asks = context.scene.collection.all_objects
    self.layout.operator((MAGIC_OT_import_ask if asks else MAGIC_OT_import_domain).bl_idname)


def export_item(self, context):
    self.layout.operator(MAGIC_OT_export_domain.bl_idname)


def register():
    bpy.utils.register_class(MAGIC_OT_import_ask)
    bpy.utils.register_class(MAGIC_OT_import_domain)
    bpy.utils.register_class(MAGIC_OT_view_far)
    bpy.utils.register_class(MAGIC_OT_import_result)
    bpy.utils.register_class(MAGIC_OT_export_domain)
    bpy.types.TOPBAR_MT_file_import.append(import_item)
    bpy.types.TOPBAR_MT_file_export.append(export_item)


def unregister():
    bpy.types.TOPBAR_MT_file_export.remove(export_item)
    bpy.types.TOPBAR_MT_file_import.remove(import_item)
    bpy.utils.unregister_class(MAGIC_OT_export_domain)
    bpy.utils.unregister_class(MAGIC_OT_import_result)
    bpy.utils.unregister_class(MAGIC_OT_view_far)
    bpy.utils.unregister_class(MAGIC_OT_import_domain)
    bpy.utils.unregister_class(MAGIC_OT_import_ask)
