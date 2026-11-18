# Renders the views of tools/Scripts/ReferenceViews.json with Cycles and writes a linear HDR EXR of each. The scene
# comes from the Magic domain add-on in tools/Blender, which says what the reference cannot copy. Run it with
# Blender's own Python:
#   blender -b --python tools/Reference/BuildScene.py -- --jobs jobs.json --magic Build/bin/Debug/Magic.exe
# --jobs is a JSON list of {"view", "hour", "pose", "out", "mask", "preview"}: it builds the scene one time and
# renders every one of them from that build, which is what tools/Scripts/UpdateReferences.ps1 gives it: the build is
# most of the time. Each job carries the pose of its camera in Magic's axes, which the engine itself resolved from
# the entity that carries the view tag, so this script finds no camera of its own. The size of the picture and the
# Cycles settings come from tools/Scripts/VisualTests.json.
import argparse
import json
import math
import os
import sys
import time

import bpy
import numpy as np
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import Camera  # noqa: E402
import Photograph  # noqa: E402
import Settings  # noqa: E402

# The add-on that builds the scene, which runs from its folder and needs no install.
sys.path.insert(0, os.path.join(Settings.ROOT, 'tools', 'Blender'))
from magic_domain import import_domain, set_hour  # noqa: E402
from magic_domain.lights import lit_by  # noqa: E402
from magic_domain.project import magic_matrix, run_queries  # noqa: E402

# The camera that set_view makes anew for each picture, and removes from the one before.
CAMERA_NAME = 'ReferenceCamera'


def project_path(view):
    return os.path.join(Settings.ROOT, view['project'])


def place_camera(scene, view, pose):
    """Poses the camera at the pose of the view, as the visual tests pose Magic's, and gives back where it stands in
    Magic's axes. The pose holds a position and a rotation in Magic's axes, as the manifest of the references keeps
    them. Magic's camera looks along its +Z with its +Y up; Blender's looks along its -Z."""
    world = magic_matrix({'position': dict(zip('xyz', pose['position'])),
                          'rotation': dict(zip('xyzw', pose['rotation']))})
    position, rotation = world.to_translation(), world.to_quaternion()
    right, up, forward = (rotation @ Vector(axis) for axis in ((1, 0, 0), (0, 1, 0), (0, 0, 1)))
    swap = Matrix(((1, 0, 0), (0, 0, 1), (0, 1, 0)))
    basis = Matrix((swap @ right, swap @ up, -(swap @ forward))).transposed()
    camera = bpy.data.objects.new(CAMERA_NAME, bpy.data.cameras.new(CAMERA_NAME))
    camera.matrix_world = Matrix.Translation((position.x, position.z, position.y)) @ basis.to_4x4()
    camera.data.sensor_fit = 'VERTICAL'
    camera.data.angle_y = math.radians(Camera.field_of_view(view))
    camera.data.clip_start = 0.05
    camera.data.clip_end = 20000
    scene.collection.objects.link(camera)
    scene.camera = camera
    return position


def setting_owner(scene, path):
    """The object that holds a settings path, and the name of the setting on it."""
    owner = scene
    parts = path.split('.')
    for part in parts[:-1]:
        owner = getattr(owner, part)
    return owner, parts[-1]


def apply_settings(scene, settings):
    """Sets Cycles by the names of its own settings, which tools/Scripts/VisualTests.json holds as data: each key is
    the path of a setting under the scene, such as cycles.adaptive_threshold, and its value what to set it to."""
    for path, value in settings.items():
        owner, name = setting_owner(scene, path)
        setattr(owner, name, value)


def set_picture(scene):
    """How a reference is written: linear light in an EXR, with the sky left in. The mask and the preview of a job
    each write a picture of their own kind, so every one of these values is set again for each render, and a job
    gives the same file whatever its place in a batch."""
    scene.view_settings.view_transform = 'Standard'
    scene.render.film_transparent = False
    scene.render.image_settings.file_format = 'OPEN_EXR'
    scene.render.image_settings.color_mode = 'RGB'
    # A reference is kept in the repository, so it is packed: half floats, which git keeps at a third of the size of
    # 32-bit floats, with a compression that loses nothing of what is left. Measured on cabin-outside at 12 against
    # the 32-bit file: the median of the lit light moves 0.0004 %, the patch percentiles under 0.003 %, the worst lit
    # pixel 0.05 %, and no value is lost to endlessness.
    scene.render.image_settings.color_depth = '16'
    scene.render.image_settings.exr_codec = 'ZIP'


def set_cycles(scene, width, height):
    scene.render.engine = 'CYCLES'
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    set_picture(scene)
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
    # Every other key of the cycles block of VisualTests.json is the path of a Cycles setting under the scene.
    apply_settings(scene, {path: value for path, value in Settings.TESTS['cycles'].items() if path != 'samples'})
    # The scene is synced to the device one time and kept for every render of the run.
    scene.render.use_persistent_data = True
    scene.cycles.denoiser = 'OPENIMAGEDENOISE'
    scene.cycles.denoising_input_passes = 'RGB_ALBEDO_NORMAL'
    preferences = bpy.context.preferences.addons['cycles'].preferences
    for kind in ('OPTIX', 'CUDA', 'HIP', 'ONEAPI', 'METAL'):
        try:
            preferences.compute_device_type = kind
            preferences.get_devices()
            if any(d.type == kind for d in preferences.devices):
                for d in preferences.devices:
                    d.use = d.type == kind
                cycles.device = 'GPU'
                print(f'rendering on the {kind} device')
                break
        except TypeError:
            continue


def build_world(view, width, height):
    """The part of the scene that no view and no hour changes: the domain and the Cycles settings. Building it is most
    of the time of a render, so one build serves every picture."""
    started = time.monotonic()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    domain = import_domain(project_path(view))
    set_cycles(domain.scene, width, height)
    drawn = len([o for o in domain.collection.all_objects if o.type != 'EMPTY'])
    print(f'built {drawn} objects over {len(bpy.data.meshes)} meshes and {len(domain.markers)} markers '
          f'in {time.monotonic() - started:.1f} s')
    return domain


def query_light(view, hours):
    """The light that lights the world at each hour, by the hour, and what the sky's light is multiplied by. Magic
    holds its day at the hour first (`set Day.Hour`), and waits for the day cycle to turn the lights. For the hour
    None, the lights are where the world has them once it is loaded. One run of Magic answers every hour."""
    held = [[] if hour is None else [f'set Day.Hour {hour:g}', 'wait 2s'] for hour in hours]
    answers = run_queries(project_path(view),
                          [(*lines, 'query DeferredRendering.Sun') for lines in held]
                          + [('query ProceduralSky.Brightness',)])
    return {hour: lit_by(answers[at]['value']) for at, hour in enumerate(hours)}, answers[-1]['value']


def set_view(domain, view, sun, brightness, pose):
    """Everything one picture changes: the camera at the pose of the view, and the light of the hour, from what
    `query DeferredRendering.Sun` prints at it."""
    if CAMERA_NAME in bpy.data.objects:
        bpy.data.objects.remove(bpy.data.objects[CAMERA_NAME], do_unlink=True)
    position = place_camera(domain.scene, view, pose)
    return set_hour(domain, sun, brightness, position.y)


def render(scene, path, samples, settings):
    """Writes the picture of the view the scene holds. A render on the GPU that the GPU refuses is taken again on the
    CPU."""
    set_picture(scene)
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    apply_settings(scene, settings)
    scene.render.filepath = os.path.abspath(path)
    try:
        bpy.ops.render.render(write_still=True)
    except RuntimeError as failure:
        print(f'the {scene.cycles.device} render failed ({failure}): taking it again on the CPU')
        scene.cycles.device = 'CPU'
        bpy.ops.render.render(write_still=True)


def write_mask(scene, path):
    """Writes a black and white PNG of the view the scene already holds, black where the sky shows and white where a
    surface does, which keeps the sky out of the numbers that Compare.py takes over the picture. One sample is enough
    for the alpha that the mask is made from, and the scene is the built one, so the mask costs seconds. set_picture
    sets each of these values again for the next render, so the mask puts none of them back."""
    scene.render.film_transparent = True
    scene.render.image_settings.color_mode = 'RGBA'
    scene.cycles.samples = 1
    scene.cycles.use_denoising = False
    alpha = os.path.abspath(path) + '.exr'
    scene.render.filepath = alpha
    bpy.ops.render.render(write_still=True)
    # The mask holds one of two values at each pixel, so it is kept as a small PNG and not as the HDR picture with an
    # alpha that a transparent render writes: 2 MB in place of 18 MB of a 1920 by 987 view.
    rendered = bpy.data.images.load(alpha)
    width, height = rendered.size
    pixels = np.empty(width * height * 4, dtype=np.float32)
    rendered.pixels.foreach_get(pixels)
    bpy.data.images.remove(rendered)
    os.remove(alpha)
    surface = np.where(pixels.reshape(-1, 4)[:, 3] < 0.5, 0, 1)
    mask = bpy.data.images.new('mask', width, height)
    mask.colorspace_settings.name = 'Non-Color'
    mask.pixels.foreach_set(np.column_stack([surface, surface, surface, np.ones_like(surface)])
                            .ravel().astype(np.float32))
    mask.file_format = 'PNG'
    mask.filepath_raw = os.path.abspath(path)
    mask.save()
    bpy.data.images.remove(mask)


def write_preview(exr, path):
    """The preview of a reference, written from the picture that was just rendered: the same look as a comparison's
    split, at half the size. It is written here so that no second run of Blender reads the EXR again."""
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    Photograph.write_preview(exr, path, Photograph.exposure_for_leak(Settings.limit('leakNits')['max']))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--jobs', required=True,
                        help='A JSON list of {"view", "hour", "pose", "out", "mask", "preview"} to render from one '
                             'build of the scene.')
    parser.add_argument('--samples', type=int,
                        help='The Cycles samples of every job. Without it, the samples of VisualTests.json.')
    parser.add_argument('--settings', help='A JSON object of Cycles settings by name, over the ones of '
                                          'VisualTests.json.')
    parser.add_argument('--magic', help='The Magic program that answers the light of each hour and the component '
                                       'types. Without it, the MAGIC_EXE environment variable.')
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    # Magic answers the light of each hour here, and the import of the domain asks it for the component types. Each
    # module of the add-on reads the program from the environment, so the option goes there for this run.
    if args.magic:
        os.environ['MAGIC_EXE'] = os.path.abspath(args.magic)
    by_name = {v['name']: v for v in Settings.views()}
    settings = json.loads(args.settings) if args.settings else {}
    samples = args.samples if args.samples else Settings.TESTS['cycles']['samples']
    with open(args.jobs, encoding='utf-8') as f:
        jobs = json.load(f)
    projects = sorted({by_name[job['view']]['project'] for job in jobs})
    if len(projects) > 1:
        raise SystemExit(f'The jobs name {len(projects)} projects ({", ".join(projects)}). One build holds the world '
                         'of one project, so give each project a run of its own.')
    # One run of Magic gives the light of every hour, before the build, which is the long part.
    hours = sorted({job.get('hour') for job in jobs}, key=lambda hour: -1 if hour is None else hour)
    suns, brightness = query_light(by_name[jobs[0]['view']], hours)
    domain = build_world(by_name[jobs[0]['view']], *Settings.picture_size())
    for job in jobs:
        started = time.monotonic()
        lit = set_view(domain, by_name[job['view']], suns[job.get('hour')], brightness, job['pose'])
        render(domain.scene, job['out'], samples, settings)
        if job.get('mask'):
            write_mask(domain.scene, job['mask'])
        if job.get('preview'):
            write_preview(job['out'], job['preview'])
        print(f"rendered {job['view']} at {job.get('hour')}, lit by the {lit}, "
              f'in {time.monotonic() - started:.1f} s')


main()
