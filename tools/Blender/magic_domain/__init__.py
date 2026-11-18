# A Blender add-on that reads and writes a domain of a Magic project. File > Import > Magic opens a .magic project and
# builds one of its domains, with the light that the domain's own files hold; File > Export > Magic writes the scene
# back as that domain. A render script puts the light of one hour in its place with set_hour.
#
# Object Properties > Magic holds the components of the entity that an object stands for, which the export writes back
# (components.py). The sidebar of the 3D viewport > Magic > Magic Domain hides the chunks of the domain that stand far
# away, which makes a large domain turn at speed (chunks.py).
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
import sys
import time

import blf
import bpy
import gpu
from bpy.props import EnumProperty, FloatProperty, StringProperty
from bpy_extras.io_utils import ImportHelper
from gpu_extras.batch import batch_for_shader
from mathutils import Vector

from .cache import folder as cache_folder
from .chunks import FAR, clip_for, cull_chunks, domain_box, left_out_chunks, middle_of
from .components import MAPPED, MARK, SHOWN, add as add_values, held, key_of, members, schema_of
from .components import remove as remove_values
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

# Seconds between the ticks of an import, and the seconds of each tick that it builds in. Blender draws the progress
# bar between two slices, so the bar moves about four times a second and Esc stops the import within a quarter of a
# second. The import builds the domain out of the view layer, so a tick draws the bar alone and the window import
# takes the time of the plain call.
TICK = 0.05
SLICE = 0.2

# The progress bar of an import: its width and height in pixels at a UI scale of 1, the colour that dims the 3D
# viewport behind it, the colour of the bar's track and of the part that is done.
BAR_SIZE = (480, 20)
SHADE = (0.0, 0.0, 0.0, 0.5)
TRACK = (0.16, 0.16, 0.16, 1.0)
FILL = (0.28, 0.45, 0.8, 1.0)

# The choice that stands for no component, which the add button shows when the entity holds every type that Magic
# knows.
NOTHING = 'NONE'

# Blender's icon for each kind of report, and the order that the result dialog draws the kinds in: what the import
# built, then its warnings, then its errors. Blender's own Info editor draws a report with the same icons.
ICONS = {'INFO': 'INFO', 'WARNING': 'ERROR', 'ERROR': 'CANCEL'}
ORDER = ['INFO', 'WARNING', 'ERROR']

# The count of objects over which a scene draws slowly in Blender, because the viewport takes time for each object
# that it draws. Blender turns over some thousands of objects at speed, and tens of thousands slowly.
MANY_OBJECTS = 10_000

# The view distance in metres that the panel opens on, which is some chunks of the world on each side of the view
# (the chunks). Use Recommended Settings applies this one.
VIEW_DISTANCE = 500.0

# The size in pixels that Use Recommended Settings limits a texture of the viewport to. It is Blender's own
# preference Viewport > Textures > Limit Size, which holds for every file that this Blender opens.
TEXTURE_LIMIT = 'CLAMP_2048'


class MAGIC_AP_preferences(bpy.types.AddonPreferences):
    """Edit > Preferences > Add-ons > Magic Domain: the Magic program that an import and an export run. The add-on
    starts the program that it is given, with no window, and builds nothing (project.magic_path)."""

    bl_idname = __package__

    executable: StringProperty(
        name='Magic Executable',
        description='The Magic program that an import and an export run with no window. Build it first, and pick the '
                    'program of that build',
        subtype='FILE_PATH',
    )

    def draw(self, context):
        self.layout.prop(self, 'executable')


class Result:
    """What one import said: one row for each thing it says, with the kind of report that gives the row its icon, the
    clip end that draws the whole domain where a viewport draws too near, and the count of the objects of a scene that
    draws slowly. The import hands this to the result dialog as it ends."""

    def __init__(self):
        self.rows = []
        self.clip_end = 0.0
        self.objects = 0


def entries():
    """How many meshes of models the add-on keeps now (cache.py). An import that reads a model from its file keeps the
    mesh, which takes seconds, so a count that went up says why the import was slow."""
    return len(os.listdir(cache_folder())) if os.path.isdir(cache_folder()) else 0


def distance(value):
    """A distance in metres as a person reads it: kilometres over a kilometre, and metres under one."""
    return f'{value / 1000:,.1f} km' if value >= 1000 else f'{value:,.0f} m'


def far_warning(context, objects):
    """The warning for a domain that the 3D viewports draw too near of, with the clip end that draws all of it, or
    nothing while every viewport draws far enough. A new Blender draws to 1000 m and looks at the world's origin, so a
    domain of kilometres, or one that stands kilometres away from the origin, shows nothing until the clip end goes up.

    It measures the places of the objects alone: a walk over the points of every mesh costs seconds over tens of
    thousands of objects, and the places give the reach of the domain to the metre."""
    spaces = [area.spaces.active for area in context.screen.areas if area.type == 'VIEW_3D'] if context.screen else []
    places = [o.matrix_world.translation for o in objects]
    if not spaces or not places:
        return None
    width = max(max(place[axis] for place in places) - min(place[axis] for place in places) for axis in range(3))
    # The distance from where each viewport looks from, which is where its clip end is measured from.
    far = max((place - space.region_3d.view_matrix.inverted().translation).length
              for space in spaces for place in places)
    draws = min(space.clip_end for space in spaces)
    if width <= draws and far <= draws:
        return None
    # The clip end as a person sets one by hand: the next power of ten over the distance, so 4.2 km gives 10,000 m.
    value = 10.0 ** math.ceil(math.log10(far))
    # One short line each: a label of a dialog does not wrap, and Blender cuts a line that is too long for the dialog.
    return ((f'The domain is {distance(width)} wide and reaches {distance(far)} from the viewport, '
             f'which draws {distance(draws)}.',
             'The viewport shows nothing of the domain that lies farther away than that.'), value)


def redraw_views(context):
    """Asks Blender to draw each 3D viewport of the window again, so that the progress bar of an import moves."""
    for area in context.window.screen.areas:
        if area.type == 'VIEW_3D':
            area.tag_redraw()


def fill(x, y, width, height, color):
    """Draws one rectangle of one colour in the pixels of the region that Blender draws now."""
    shader = gpu.shader.from_builtin('UNIFORM_COLOR')
    batch = batch_for_shader(shader, 'TRIS', {'pos': [(x, y), (x + width, y), (x + width, y + height), (x, y + height)]},
                             indices=[(0, 1, 2), (0, 2, 3)])
    shader.uniform_float('color', color)
    batch.draw(shader)


def centered_text(text, y, size):
    """Draws one line of white text in the middle of the width of the region that Blender draws now."""
    blf.size(0, size)
    blf.color(0, 1.0, 1.0, 1.0, 1.0)
    width, _ = blf.dimensions(0, text)
    blf.position(0, (bpy.context.region.width - width) / 2, y, 0)
    blf.draw(0, text)


def clear_scene(scene):
    """Takes the objects of a scene away, with the collections that hold them, the data that only they used and the
    record of the domain that an import left, so that an import after this gives the same scene as an import into a
    new file. It is Blender's own Clean Up > Purge over the objects of one scene."""
    for o in list(scene.collection.all_objects):
        bpy.data.objects.remove(o, do_unlink=True)
    for collection in list(scene.collection.children):
        bpy.data.collections.remove(collection)
    for key in [key for key in scene.keys() if key.startswith('magic_')]:
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
    # No undo step: one step of Blender's undo holds the whole file, and writing a file of tens of thousands of
    # objects costs seconds and hundreds of megabytes. Ctrl+Z after the import steps back to the file as it stood
    # before the import, which takes the domain away: that is what an undo of an import means.
    bl_options = {'REGISTER'}

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
        self.bar = bpy.types.SpaceView3D.draw_handler_add(self.draw_bar, (), 'WINDOW', 'POST_PIXEL')
        context.window.cursor_modal_set('WAIT')
        self.timer = context.window_manager.event_timer_add(TICK, window=context.window)
        context.window_manager.modal_handler_add(self)
        return {'RUNNING_MODAL'}

    def draw_bar(self):
        """The progress of the import over the middle of each 3D viewport, which it dims: what is built now, the part
        of the work that is done, and the key that stops it."""
        region = bpy.context.region
        scale = bpy.context.preferences.system.ui_scale
        width, height = min(BAR_SIZE[0] * scale, region.width - 40 * scale), BAR_SIZE[1] * scale
        x, y = (region.width - width) / 2, (region.height - height) / 2
        gpu.state.blend_set('ALPHA')
        fill(0, 0, region.width, region.height, SHADE)
        fill(x, y, width, height, TRACK)
        fill(x, y, width * self.build.factor, height, FILL)
        gpu.state.blend_set('NONE')
        centered_text(f'Magic: {self.build.text}', y + height + 12 * scale, 16 * scale)
        centered_text('Esc to stop', y - 24 * scale, 12 * scale)

    def modal(self, context, event):
        """One tick of the import: it builds for part of the tick and gives the rest to Blender, which draws the
        progress bar. It takes every other event, so that nothing changes the scene while the domain fills in. Esc
        stops it and takes away what is built."""
        if event.type == 'ESC':
            self.build.drop()
            self.end(context)
            self.say('WARNING', 'The import stopped, and what it built is taken away.')
            self.show_result()
            return {'CANCELLED'}
        if event.type != 'TIMER':
            return {'RUNNING_MODAL'}
        try:
            done = self.build.run(time.monotonic() + SLICE)
        except Exception as failure:
            # Every failure takes the bar away, since the bar dims each 3D viewport. A fault of the add-on itself goes
            # on to Blender with its traceback.
            self.build.drop()
            self.end(context)
            if not isinstance(failure, (OSError, ValueError, KeyError)):
                raise
            self.say('ERROR', str(failure))
            self.show_result()
            return {'CANCELLED'}
        redraw_views(context)
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
        drawn = sum(1 for o in domain.collection.all_objects if o.type != 'EMPTY' or o.instance_collection)
        self.say('INFO', f'{os.path.basename(domain.scene["magic_folder"])}: {drawn:,} objects drawn, '
                         f'{len(self.build.prefabs)} prefabs, {len(domain.markers)} markers, '
                         f'{len(domain.suns)} lights, in {time.monotonic() - self.started:.1f} s')
        read = entries() - self.kept
        if read > 0:
            self.say('INFO', f'{read} of the models were read from their files, which takes seconds each, and their '
                             f'meshes are kept: the next import of this domain appends them.')
        for name, entities in sorted(self.build.unknown.items()):
            self.say('WARNING', f'{len(entities):,} entities hold the component {name}, such as {entities[0]}, which '
                                f'this run of Magic does not know. The panel shows it as the JSON that the import '
                                f'read, and the export writes it back as it was.')
        far = far_warning(context, domain.collection.all_objects)
        if far:
            lines, self.result.clip_end = far
            self.say('WARNING', *lines)
        if drawn > MANY_OBJECTS:
            self.result.objects = drawn
            self.say('WARNING', f'The scene holds {drawn:,} objects, more than the {MANY_OBJECTS:,} that Blender '
                                f'draws at speed. The viewport, the selection and each edit can be slow.')

    def show_result(self):
        """The result of the import in Blender's own dialog, which opens over the viewport as the import ends."""
        MAGIC_OT_import_result.shown = self.result
        bpy.ops.magic.import_result('INVOKE_DEFAULT')

    def end(self, context):
        """Takes away the timer, the bar and the wait cursor of an import that ended, whichever way it ended: no bar is
        left after an import that finished, that a person stopped or that failed."""
        context.window_manager.event_timer_remove(self.timer)
        bpy.types.SpaceView3D.draw_handler_remove(self.bar, 'WINDOW')
        context.window.cursor_modal_restore()
        redraw_views(context)


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
        if self.shown.clip_end or self.shown.objects:
            self.layout.operator(MAGIC_OT_recommended_settings.bl_idname, icon='VIEW3D').clip_end = \
                self.shown.clip_end

    def execute(self, context):
        return {'FINISHED'}


def cull_viewport(context, space):
    """Applies the scene's view distance from one 3D viewport, and gives back how many chunks it hid.

    It measures from the point that the viewport turns around, which Blender calls the view location, and not from
    the point that the view looks from: after View > Frame All the view stands kilometres back from a domain, and the
    point that it turns around is the middle of what it frames."""
    scene = context.scene
    return cull_chunks(context.view_layer, space.region_3d.view_location, scene.magic_view_distance,
                       scene.magic_far_chunks)


def collapse_outliner(context):
    """Closes one level of the rows of each outliner of this screen, so that a collection of the domain does not stand
    open with a row for each object in it. Blender holds the open rows of an outliner in no field that its API reads
    or writes, and this operator of its own is the nearest that the API comes: it closes one level of every row, and
    not the children of one collection. It gives back what it did, for the report."""
    areas = [area for area in context.screen.areas if area.type == 'OUTLINER']
    for area in areas:
        for region in area.regions:
            if region.type == 'WINDOW':
                with context.temp_override(area=area, region=region):
                    bpy.ops.outliner.show_one_level(open=False)
    return f'{len(areas)} Outliners closed one level' if areas else 'no Outliner on this screen'


class MAGIC_OT_recommended_settings(bpy.types.Operator):
    """Frame the whole domain in the 3D viewports of this screen and set them for a large scene: solid shading, no
    outlines, no object extras, relationship lines or statistics, the scene's Simplify for the viewport, the view
    distance of the Magic Domain panel, and Blender's own preference that limits the size of a viewport texture"""

    bl_idname = 'magic.recommended_settings'
    bl_label = 'Use Recommended Settings'
    bl_options = {'INTERNAL'}

    clip_end: FloatProperty(name='Clip End', subtype='DISTANCE')

    def execute(self, context):
        """It changes the view and the settings that draw it, and writes no undo step, as Blender's own
        View > Frame All does. Each setting is one field that a person turns back in the Shading popover, the Overlays
        popover or Render Properties > Simplify.

        It changes no object, no material and no field that a render reads, so the export writes what it wrote before
        and a render draws what it drew before."""
        ends = []
        box = domain_box(context.view_layer)
        middle = middle_of(box) if box else Vector()
        for area in context.screen.areas:
            if area.type != 'VIEW_3D':
                continue
            space = area.spaces.active
            # The clip end goes up and never down, and a viewport that draws far enough of the domain keeps the one
            # it has: a clip end past what the view needs takes the depth that Blender draws the near things with.
            # The clip end that the frame needs comes from the boxes of the chunks (clip_for).
            if self.clip_end:
                space.clip_end = max(space.clip_end, self.clip_end, clip_for(box, space.lens) if box else 0.0)
            for region in area.regions:
                if region.type == 'WINDOW':
                    with context.temp_override(area=area, region=region):
                        bpy.ops.view3d.view_all()
            ends.append(space.clip_end)
            # Material preview and the rendered view draw each material of the scene, and solid shading draws one. The
            # outlines, the extras and the relationship lines each cost a pass over every object of the scene.
            space.shading.type = 'SOLID'
            space.shading.show_object_outline = False
            space.overlay.show_outline_selected = False
            space.overlay.show_extras = False
            space.overlay.show_relationship_lines = False
            space.overlay.show_stats = False
        # Frame All stands the view in the middle of the domain, which the boxes of its chunks give: the point that a
        # viewport turns around still holds the old view here, because Blender moves the view over several frames
        # while its own Smooth View is on.
        hidden = cull_chunks(context.view_layer, middle, context.scene.magic_view_distance,
                             context.scene.magic_far_chunks)
        # Simplify for the viewport alone: the fields of the Render column, such as Max Subdivision there, stay as
        # they are.
        context.scene.render.use_simplify = True
        context.scene.render.simplify_subdivision = 0
        context.scene.render.simplify_child_particles = 0.0
        # A preference, which holds for every file that this Blender opens and not for this one alone. It is not
        # written to disk here: a Blender that opens again limits no texture, unless the person's own Auto-Save
        # Preferences wrote it.
        context.preferences.system.gl_texture_limit = TEXTURE_LIMIT
        self.report({'INFO'}, f'Frame All; Clip End {max(ends, default=0):,.0f} m; Viewport Shading Solid; Outline '
                              f'off; Selected Outline off; Extras off; Relationship Lines off; Statistics off; '
                              f'Simplify on, Max Subdivision 0, Child Particles 0; View Distance '
                              f'{context.scene.magic_view_distance:,.0f} m, {hidden:,} chunks hidden; '
                              f'{collapse_outliner(context)}; Preferences > Viewport > Textures > Limit Size 2048, '
                              f'a preference of this Blender and of no one file')
        return {'FINISHED'}


class MAGIC_OT_view_distance(bpy.types.Operator):
    """Hide the chunks of the domain that stand farther from this viewport than the view distance"""

    bl_idname = 'magic.view_distance'
    bl_label = 'Update'
    bl_options = {'REGISTER'}

    @classmethod
    def poll(cls, context):
        return context.space_data is not None and context.space_data.type == 'VIEW_3D'

    def execute(self, context):
        hidden = cull_viewport(context, context.space_data)
        self.report({'INFO'}, f'{hidden:,} chunks hidden, '
                              f'{len(context.view_layer.objects):,} objects in the view layer')
        return {'FINISHED'}


class MAGIC_OT_show_all(bpy.types.Operator):
    """Show every chunk of the domain again"""

    bl_idname = 'magic.show_all'
    bl_label = 'Show All'
    bl_options = {'REGISTER'}

    def execute(self, context):
        # A view distance with no end holds every chunk, from any point, so the one function shows them all again.
        cull_chunks(context.view_layer, Vector(), math.inf, context.scene.magic_far_chunks)
        self.report({'INFO'}, f'Every chunk shown: {len(context.view_layer.objects):,} objects in the view layer')
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


def component_items(self, context):
    """The components that a person can add to an entity: each type that the import read from Magic, less the ones
    that Blender holds its own way and the ones that the entity holds now."""
    schema = schema_of(context.scene)
    taken = set(held(context.object)) | set(SHOWN)
    left = [name for name in sorted(schema) if name not in taken]
    return [(name, name, '') for name in left] or [(NOTHING, 'The entity holds every component', '')]


class MAGIC_OT_add_component(bpy.types.Operator):
    """Add a component of Magic to this entity, with the values of its type"""

    bl_idname = 'magic.add_component'
    bl_label = 'Add Component'
    bl_options = {'REGISTER', 'UNDO'}
    bl_property = 'component'

    component: EnumProperty(name='Component', description='The component to add', items=component_items)

    def invoke(self, context, event):
        """Blender's own search of a list, which its Add Modifier button opens too."""
        context.window_manager.invoke_search_popup(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        if self.component == NOTHING:
            return {'CANCELLED'}
        add_values(context.object, self.component, schema_of(context.scene)[self.component])
        return {'FINISHED'}


class MAGIC_OT_remove_component(bpy.types.Operator):
    """Take this component off this entity"""

    bl_idname = 'magic.remove_component'
    bl_label = 'Remove Component'
    bl_options = {'REGISTER', 'UNDO'}

    component: StringProperty(name='Component', description='The component to take away')

    def execute(self, context):
        remove_values(context.object, self.component)
        return {'FINISHED'}


class MAGIC_PT_domain(bpy.types.Panel):
    """The view distance of the domain in this scene"""

    bl_idname = 'MAGIC_PT_domain'
    bl_label = 'Magic Domain'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'Magic'

    @classmethod
    def poll(cls, context):
        """The panel stands in a scene that an import built."""
        return 'magic_folder' in context.scene

    def draw(self, context):
        self.layout.use_property_split = True
        self.layout.prop(context.scene, 'magic_view_distance')
        self.layout.prop(context.scene, 'magic_far_chunks')
        row = self.layout.row(align=True)
        row.operator(MAGIC_OT_view_distance.bl_idname, icon='FILE_REFRESH')
        row.operator(MAGIC_OT_show_all.bl_idname, icon='HIDE_OFF')
        out = left_out_chunks(context.view_layer)
        if out:
            self.layout.label(text=f'{out:,} chunks are out of the view layer, which a render does not draw.',
                              icon='INFO')


def draw_shown(layout, o, name, described):
    """One component that Blender's own data model holds: where a person edits what Blender holds of it, and a field
    for each member of it that Blender holds nowhere (SHOWN)."""
    layout.label(text=f'Blender holds {SHOWN[name][0]}.', icon='INFO')
    for field in members(name, described):
        layout.prop(o, f'["{key_of(name, field)}"]', text=field['name'])


def draw_component(layout, o, name, described):
    """One component of an entity, as the fields that Blender draws its values with. A member whose type the add-on
    has no field for, and a component that the run of Magic did not know, stand as the JSON that the import read: a
    person cannot change it, and the export writes it back as it was."""
    for field in members(name, described or {}):
        layout.prop(o, f'["{key_of(name, field)}"]', text=field['name'])
    if described is None:
        layout.label(text='This run of Magic does not know this component.', icon='ERROR')
    kept = o[MARK + name]
    if kept not in ('{}', ''):
        row = layout.row()
        row.enabled = False
        row.prop(o, f'["{MARK}{name}"]', text='Kept')


class MAGIC_PT_components(bpy.types.Panel):
    """The components of the entity that this object stands for"""

    bl_idname = 'MAGIC_PT_components'
    bl_label = 'Magic'
    bl_space_type = 'PROPERTIES'
    bl_region_type = 'WINDOW'
    bl_context = 'object'

    @classmethod
    def poll(cls, context):
        """The panel stands in a scene that an import built, where an object is an entity of a domain."""
        return context.object is not None and 'magic_folder' in context.scene

    def draw(self, context):
        o = context.object
        schema = schema_of(context.scene)
        self.layout.use_property_split = True
        for name in o.get(MAPPED, []):
            header, body = self.layout.panel(f'magic_{name}')
            header.label(text=name)
            if body is not None:
                draw_shown(body, o, name, schema.get(name))
        for name in held(o):
            header, body = self.layout.panel(f'magic_{name}')
            header.label(text=name)
            header.operator(MAGIC_OT_remove_component.bl_idname, text='', icon='X',
                            emboss=False).component = name
            if body is not None:
                draw_component(body, o, name, schema.get(name))
        self.layout.operator(MAGIC_OT_add_component.bl_idname, icon='ADD')


def import_item(self, context):
    """File > Import > Magic: the question first when the scene holds objects, the file browser alone when the scene
    is empty."""
    asks = context.scene.collection.all_objects
    self.layout.operator((MAGIC_OT_import_ask if asks else MAGIC_OT_import_domain).bl_idname)


def export_item(self, context):
    self.layout.operator(MAGIC_OT_export_domain.bl_idname)


# Each class that Blender registers, in the order of this file. An unregister takes them in the other order.
CLASSES = (
    MAGIC_AP_preferences,
    MAGIC_OT_import_ask,
    MAGIC_OT_import_domain,
    MAGIC_OT_import_result,
    MAGIC_OT_recommended_settings,
    MAGIC_OT_view_distance,
    MAGIC_OT_show_all,
    MAGIC_OT_export_domain,
    MAGIC_OT_add_component,
    MAGIC_OT_remove_component,
    MAGIC_PT_domain,
    MAGIC_PT_components,
)


def register():
    for held_class in CLASSES:
        bpy.utils.register_class(held_class)
    bpy.types.Scene.magic_view_distance = FloatProperty(
        name='View Distance', description='Metres from the view location of the viewport, past which a chunk of the '
                                          'domain is hidden', subtype='DISTANCE', default=VIEW_DISTANCE, min=0.0)
    bpy.types.Scene.magic_far_chunks = EnumProperty(
        name='Far Chunks', description='What to do with the chunks that stand farther away than the view distance',
        items=FAR, default='HIDE')
    bpy.types.TOPBAR_MT_file_import.append(import_item)
    bpy.types.TOPBAR_MT_file_export.append(export_item)


def unregister():
    bpy.types.TOPBAR_MT_file_export.remove(export_item)
    bpy.types.TOPBAR_MT_file_import.remove(import_item)
    del bpy.types.Scene.magic_far_chunks
    del bpy.types.Scene.magic_view_distance
    for held_class in reversed(CLASSES):
        bpy.utils.unregister_class(held_class)
    # Blender takes away this module alone when it disables the add-on. The modules of the package stay, so the next
    # enable after an install would run the new module on the old ones. Taking them away loads all of them new.
    for name in [name for name in sys.modules if name.startswith(f'{__package__}.')]:
        del sys.modules[name]
