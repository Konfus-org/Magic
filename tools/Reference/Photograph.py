# The one look of every picture that the reference tools write: the exposure, the scene's grade from
# tools/Scripts/VisualTests.json, and Blender's AgX view transform. A comparison's split, the previews of a stored
# reference and the frames of a move all go through it, so the report shows one look. The numbers that the tests
# measure stay the raw light, before this. Reading and writing pictures needs Blender, so run the tools that call
# write_photograph with Blender's Python.
import numpy as np

import Metrics
import Settings

# The middle grey that the exposure puts the picture's median at, and the brightness the grade turns about.
PIVOT = 0.18
# The preview JPEG: half the width and height at this quality is about 100 KB of a 1600 by 900 picture.
QUALITY = 85
GRADE = Settings.TESTS['grade']


def exposure_for(light):
    """The exposure that puts a picture's median light at the middle grey."""
    return PIVOT / max(float(np.median(light)), 1e-9)


def exposure_for_leak(nits):
    """The exposure of the pictures of a view whose reference holds no light, such as a sealed room: it puts the leak
    limit at the middle grey, so a leak shows."""
    return PIVOT / nits


def grade(linear, exposure):
    """The look's grade on a picture: the exposure, then the warm and cool split of the highlights against the
    shadows, then the saturation, then the contrast about the middle grey. Magic's half and the reference's half get
    the same numbers."""
    rgb = np.clip(linear, 0, None) * exposure
    high = Metrics.luminance(rgb)[..., None]
    high = high / (high + PIVOT)
    rgb = rgb * (high * np.array(GRADE['highlights']) + (1 - high) * np.array(GRADE['shadows']))
    grey = Metrics.luminance(rgb)[..., None]
    rgb = np.clip(grey + GRADE['saturation'] * (rgb - grey), 0, None)
    return PIVOT * (rgb / PIVOT) ** GRADE['contrast']


def read_image(path, colorspace, channels=3):
    """The pixels of an EXR or a PNG, top row first, read as the colorspace says: 'Linear Rec.709' for light, or
    'Non-Color' for a PNG's bytes as they are, from 0 to 1. The fourth channel is the alpha."""
    import bpy
    image = bpy.data.images.load(path)
    image.colorspace_settings.name = colorspace
    width, height = image.size
    flat = np.empty(width * height * 4, dtype=np.float32)
    image.pixels.foreach_get(flat)
    bpy.data.images.remove(image)
    return np.flipud(flat.reshape(height, width, 4)[..., :channels].astype(np.float64))


def write_png(path, rgb):
    """Writes colour from 0 to 1, already encoded for the screen, top row first."""
    import bpy
    height, width = rgb.shape[:2]
    image = bpy.data.images.new('out', width, height)
    image.pixels.foreach_set(np.dstack([np.flipud(rgb), np.ones((height, width))]).ravel().astype(np.float32))
    image.filepath_raw = path
    image.file_format = 'PNG'
    image.save()
    bpy.data.images.remove(image)


def write_photograph(path, graded, scale=1):
    """Writes graded light as a photograph of it: Blender's AgX view transform, which rolls the highlights off and
    keeps the colour of a bright surface. The path's suffix gives the format, and scale shrinks the picture. Two
    pictures of one graded array, such as the split and its JPEG, grade it one time."""
    import bpy
    scene = bpy.context.scene
    scene.display_settings.display_device = 'sRGB'
    scene.view_settings.view_transform = 'AgX'
    scene.view_settings.look = 'None'
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1
    scene.render.image_settings.file_format = 'JPEG' if path.endswith('.jpg') else 'PNG'
    scene.render.image_settings.color_mode = 'RGB'
    scene.render.image_settings.quality = QUALITY

    height, width = graded.shape[:2]
    image = bpy.data.images.new('out', width, height, float_buffer=True)
    image.colorspace_settings.name = 'Linear Rec.709'
    image.pixels.foreach_set(np.dstack([np.flipud(graded), np.ones((height, width))]).ravel().astype(np.float32))
    if scale != 1:
        image.scale(int(width * scale), int(height * scale))
    image.save_render(path, scene=scene)
    bpy.data.images.remove(image)


def write_preview(exr, path, dark_exposure):
    """A stored reference alone as a photograph, which the docs show, at half the size. A reference that holds no light
    takes the exposure of the leak limit, because its own median says nothing."""
    reference = read_image(exr, 'Linear Rec.709') * Metrics.NITS_PER_WATT
    light = Metrics.luminance(reference)
    exposure = dark_exposure if float(np.median(light)) <= 0 else exposure_for(light)
    write_photograph(path, grade(reference, exposure), scale=0.5)
    print(f'preview    {path}  (AgX, exposure x{exposure:.4g})')
