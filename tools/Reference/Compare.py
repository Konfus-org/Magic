# Compares Magic's light before the exposure with the Cycles reference of the same view. Run it with Blender's Python:
#   blender -b --python tools/Reference/Compare.py -- --view cabin-wall --reference ref.exr --magic a.pfm b.pfm --out dir
# It writes ratio.png and split.png into the out folder and prints a table: for each point of the view, its place in
# the picture, Magic's light over the reference's, and the colour (r/g, b/g) of each.
import argparse
import json
import math
import os
import sys

import bpy
import numpy as np

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
LUMINANCE = np.array([0.2126, 0.7152, 0.0722])


def read_pfm(path):
    with open(path, 'rb') as f:
        kind = f.readline().strip()
        width, height = (int(v) for v in f.readline().split())
        scale = float(f.readline())
        data = np.frombuffer(f.read(), dtype='<f4' if scale < 0 else '>f4')
    channels = 3 if kind == b'PF' else 1
    return np.flipud(data.reshape(height, width, channels)).astype(np.float64)


def read_exr(path):
    image = bpy.data.images.load(path)
    width, height = image.size
    pixels = np.array(image.pixels[:], dtype=np.float64).reshape(height, width, 4)[..., :3]
    bpy.data.images.remove(image)
    return np.flipud(pixels)


def write_png(path, rgb):
    """Writes colour from 0 to 1, already encoded for the screen, top row first."""
    height, width = rgb.shape[:2]
    image = bpy.data.images.new('out', width, height)
    image.pixels = np.dstack([np.flipud(rgb), np.ones((height, width))]).ravel()
    image.filepath_raw = path
    image.file_format = 'PNG'
    image.save()
    bpy.data.images.remove(image)


def encode(linear):
    linear = np.clip(linear, 0, 1)
    return np.where(linear <= 0.0031308, linear * 12.92, 1.055 * linear ** (1 / 2.4) - 0.055)


def project(view, point, width, height):
    """A point's place in the picture, from 0 to 1 across and down: its world position seen through the view's camera,
    or its x and y as they are. Magic's axes: at a yaw of 0 the camera looks along +Z, with +X right and +Y up."""
    if 'position' not in point:
        return point['x'], point['y']
    yaw, pitch = math.radians(view['yaw']), math.radians(view['pitch'])
    p, c = point['position'], view['position']
    v = np.array([p['x'] - c['x'], p['y'] - c['y'], p['z'] - c['z']])
    # Undo the yaw about up, then the pitch about the camera's right.
    v = np.array([v[0] * math.cos(yaw) - v[2] * math.sin(yaw), v[1], v[0] * math.sin(yaw) + v[2] * math.cos(yaw)])
    v = np.array([v[0], v[1] * math.cos(pitch) + v[2] * math.sin(pitch), -v[1] * math.sin(pitch) + v[2] * math.cos(pitch)])
    tangent = math.tan(math.radians(view.get('fieldOfView', 60)) / 2)
    return 0.5 + 0.5 * v[0] / (v[2] * tangent * width / height), 0.5 - 0.5 * v[1] / (v[2] * tangent)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--view', required=True)
    parser.add_argument('--reference', required=True, help="The Cycles EXR.")
    parser.add_argument('--magic', nargs='+', required=True, help="Magic's PFMs of the view, averaged.")
    parser.add_argument('--out', required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    views = json.load(open(os.path.join(ROOT, 'tools', 'Scripts', 'ReferenceViews.json')))
    view = next(v for v in views if v['name'] == args.view)

    magic = np.mean([read_pfm(p) for p in args.magic], axis=0)
    # Cycles' radiance in W/sr/m2, in Magic's nits at 683 lm/W.
    reference = read_exr(args.reference) * 683
    if reference.shape != magic.shape:
        raise SystemExit(f'The reference is {reference.shape}, Magic is {magic.shape}: render it at the same size.')
    height, width = magic.shape[:2]
    magic_light = np.maximum(magic @ LUMINANCE, 1e-6)
    reference_light = np.maximum(reference @ LUMINANCE, 1e-6)

    # The ratio map: grey-green is the same light, red brighter than the reference, blue darker, full at 8 times.
    stops = np.clip(np.log2(magic_light / reference_light) / 3, -1, 1)
    ratio = np.dstack([0.5 + 0.5 * stops, 0.5 - 0.5 * np.abs(stops), 0.5 - 0.5 * stops])
    os.makedirs(args.out, exist_ok=True)
    write_png(os.path.join(args.out, 'ratio.png'), ratio)

    # The split picture: Magic on the left, the reference on the right, both at one exposure taken from Magic's
    # median light, which shows the median at 18 percent grey.
    exposure = 0.18 / float(np.median(magic_light))
    split = reference.copy()
    split[:, :width // 2] = magic[:, :width // 2]
    split = encode(split * exposure)
    split[:, width // 2 - 1:width // 2 + 1] = 1
    write_png(os.path.join(args.out, 'split.png'), split)

    print(f'ratio map: {os.path.join(args.out, "ratio.png")}')
    print(f'split:     {os.path.join(args.out, "split.png")}  (exposure x{exposure:.4g})')
    print(f'{"point":<24} {"x":>5} {"y":>5} {"magic/ref":>9}  {"r/g":>11}  {"b/g":>11}')
    for point in view['points']:
        x, y = project(view, point, width, height)
        cx = min(max(int(x * (width - 1)), 2), width - 3)
        cy = min(max(int(y * (height - 1)), 2), height - 3)
        m = magic[cy - 2:cy + 3, cx - 2:cx + 3].reshape(-1, 3).mean(axis=0)
        r = reference[cy - 2:cy + 3, cx - 2:cx + 3].reshape(-1, 3).mean(axis=0)
        light = (m @ LUMINANCE) / max(r @ LUMINANCE, 1e-9)
        print(f'{point["name"]:<24} {x:5.2f} {y:5.2f} {light:9.2f}  '
              f'{m[0] / m[1]:5.2f}/{r[0] / r[1]:5.2f}  {m[2] / m[1]:5.2f}/{r[2] / r[1]:5.2f}')
    print(f'{"median of the picture":<24} {"":>5} {"":>5} {np.median(magic_light / reference_light):9.2f}')


main()
