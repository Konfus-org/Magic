# Compares Magic's light before the exposure with the Cycles reference of the same view, and writes the rows that
# tools/Scripts/VisualTests.ps1 judges. Run it with Blender's Python:
#   blender -b --python tools/Reference/Compare.py -- --view cabin-wall --pose <7 numbers> --reference ref.exr \
#     --magic a.pfm b.pfm --angles angles.json --out dir --json metrics.json
# The camera's pose comes from --pose, which the tests read from the manifest of the references. --angles gives the
# frames of the same view at the two camera angles beside it, each with the pose it was taken at.
# It writes the pictures into the out folder and prints a table of each point of the view: its place in the picture,
# Magic's light over the reference's, and the colour (r/g, b/g) of each. The JSON holds one row for each number a
# limit of tools/Scripts/VisualTests.json judges, and names each picture it wrote. With --mask, a Cycles mask of the
# same view, the sky stays out of the percentiles.
# A point whose data states "maxNits" gets a leak row: the light Magic leaves there, which the leakNits limit judges.
# A view whose reference lights no pixel, such as a sealed room, gets those rows alone, because a ratio to no light,
# its colour or its noise reads nothing.
# --pick prints the world position of one pixel.
# --check-points renders nothing and answers one question: does every point of each view stay in the picture at each
# camera angle? The pictures get the scene's look (tools/Reference/Photograph.py); the numbers stay the raw light.
import argparse
import json
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import Camera
import Metrics
import Photograph
import Settings

# The part of the reference's median light under which a pixel counts as unlit and stays out of the percentiles.
FLOOR = 0.01


def read_frames(paths):
    """The light of each frame, read one time. The comparison takes their mean; the flicker and the fireflies read
    them one by one."""
    return [Metrics.read_pfm(path) for path in paths]


def leak_rows(view, pose, magic, width, height):
    """One row for each point that "maxNits" marks as a leak point: the light that Magic leaves there, which the
    leakNits limit of tools/Scripts/VisualTests.json judges. A sealed room marks each of its points, and so may a
    point of any view that must stay dark. The number of the field is the mark alone; the limit holds the value."""
    rows = []
    for point in view['points']:
        if 'maxNits' not in point:
            continue
        x, y = Camera.project(view, pose, point, width, height)
        light = float(Metrics.luminance(Camera.patch(magic, x, y, width, height)))
        rows.append({'name': f'leak at {point["name"]}', 'value': light, 'limit': 'leakNits'})
    return rows


def angle_lights(view, pose, frames, width, height):
    """The light of each point of the view in Magic's frames at one camera angle, averaged over the frames."""
    picture = np.mean(frames, axis=0)
    lights = []
    for point in view['points']:
        x, y = Camera.project(view, pose, point, width, height)
        lights.append(float(Metrics.luminance(Camera.patch(picture, x, y, width, height))))
    return lights


def angle_rows(view, angles, at_the_view, width, height):
    """One row for each point: how far its light moves over the camera angles the view is measured at. A point on a
    surface that the renderer lights the same way from each side holds still. The capture turns the camera to make
    its console lines, so it is the one owner of the turn: each angle comes in with its own pose and frames."""
    by_angle = []
    for angle in angles:
        by_angle.append(angle_lights(view, angle['pose'], read_frames(angle['frames']), width, height))
    rows = []
    for i, point in enumerate(view['points']):
        lights = [at_the_view[i]] + [angle[i] for angle in by_angle]
        middle = max(float(np.mean(lights)), 1e-9)
        apart = max(abs(light / middle - 1) for light in lights)
        rows.append({'name': f'{point["name"]} at three angles', 'value': apart, 'limit': 'pointAcrossAngles'})
    return rows


def print_rows(rows):
    print(f'{"row":<34} {"value":>11}  {"limit":<22}')
    for row in rows:
        print(f'{row["name"]:<34} {row["value"]:11.4g}  {row.get("limit", ""):<22}')


def write_pictures(out, magic, reference, exposure):
    """The split picture: Magic on the left and the reference on the right, with a white line between them, at the one
    exposure the caller gives for both. The graded light is made one time and written twice."""
    width = magic.shape[1]
    split = reference.copy()
    split[:, :width // 2] = magic[:, :width // 2]
    split[:, width // 2 - 1:width // 2 + 1] = 1e6
    graded = Photograph.grade(split, exposure)
    Photograph.write_photograph(os.path.join(out, 'split.png'), graded)
    Photograph.write_photograph(os.path.join(out, 'split.jpg'), graded, scale=0.5)
    print(f'split.png  {os.path.join(out, "split.png")}  (AgX, exposure x{exposure:.4g})')
    print(f'split.jpg  {os.path.join(out, "split.jpg")}  (the split at half the size)')
    return [{'file': 'split.png', 'caption': 'Magic on the left, Cycles on the right'},
            {'file': 'split.jpg', 'caption': 'the split at half the size'}]


def pick(view, pose, args):
    """Prints the world position of the pixel that --pick names, read from a screenshot of the Depth debug view."""
    depth_view = Photograph.read_image(args.depth, 'Non-Color')[..., 0]
    height, width = depth_view.shape[:2]
    if not (0 <= args.pick[0] < width and 0 <= args.pick[1] < height):
        raise SystemExit(f'The picture is {width} by {height}: --pick is outside it.')
    value = float(depth_view[args.pick[1], args.pick[0]])
    if value <= 1 / 255:
        raise SystemExit('The pixel is black in the Depth view: it is the sky, or past DeferredRendering.DepthViewFar.')
    # The Depth view's log scale, undone (ShowSurface.slang, DebugView.Depth).
    depth = 2 ** ((1 - value) * math.log2(1 + args.far)) - 1
    x, y = (args.pick[0] + 0.5) / width, (args.pick[1] + 0.5) / height
    p = Camera.unproject(view, pose, x, y, depth, width, height)
    step = 2 ** (math.log2(1 + args.far) / 255) - 1
    print(f'depth     {depth:.3f} m in front of the camera, within {step:.1%} of it')
    print(f'position  "x": {p[0]:.3f}, "y": {p[1]:.3f}, "z": {p[2]:.3f}')


def compare(view, pose, args):
    """Every row and every picture of one view at one hour."""
    frames = read_frames(args.magic)
    magic = np.mean(frames, axis=0)
    reference = Photograph.read_image(args.reference, 'Linear Rec.709') * Metrics.NITS_PER_WATT
    if reference.shape != magic.shape:
        raise SystemExit(f'The reference is {reference.shape}, Magic is {magic.shape}: render it at the same size.')
    height, width = magic.shape[:2]
    os.makedirs(args.out, exist_ok=True)

    # The percentiles over the pixels the reference lights, so that the ones it leaves black do not count, and the
    # sky is left out: it is the renderer's own, and it fills the outside views. The floor is taken before the light
    # is held over zero, so that a reference which lights nothing lights no pixel here either.
    reference_light = Metrics.luminance(reference)
    floor = FLOOR * float(np.median(reference_light))
    lit = reference_light > floor
    magic_light = np.maximum(Metrics.luminance(magic), 1e-6)
    reference_light = np.maximum(reference_light, 1e-6)
    if args.mask:
        sky = Photograph.read_image(args.mask, 'Non-Color', channels=1)[..., 0] < 0.5
        if sky.shape != lit.shape:
            raise SystemExit(f'The mask is {sky.shape}, the picture is {lit.shape}: render it at the same size.')
        lit &= ~sky

    # A reference that lights no pixel says nothing about a ratio, a colour or a noise. Its pictures take the exposure
    # of the leak limit, which puts that limit at the middle grey, so a leak shows.
    if not lit.any():
        exposure = Photograph.exposure_for_leak(Settings.limit('leakNits')['max'])
        pictures = write_pictures(args.out, magic, reference, exposure)
        return {'rows': leak_rows(view, pose, magic, width, height), 'pictures': pictures}

    exposure = Photograph.exposure_for(reference_light)
    pictures = write_pictures(args.out, magic, reference, exposure)

    # The ratio map: grey-green is the same light, red brighter than the reference, blue darker, full at 8 times.
    stops = np.clip(np.log2(magic_light / reference_light) / 3, -1, 1)
    Photograph.write_png(os.path.join(args.out, 'ratio.png'),
                         np.dstack([0.5 + 0.5 * stops, 0.5 - 0.5 * np.abs(stops), 0.5 - 0.5 * stops]))
    mean_flip, flip_map = Metrics.flip(magic, reference, exposure)
    Photograph.write_png(os.path.join(args.out, 'flip.png'), flip_map)
    pictures += [{'file': 'ratio.png', 'caption': 'red is brighter than Cycles, blue darker, full at 8 times'},
                 {'file': 'flip.png', 'caption': "FLIP's error map: black is no difference"}]
    print(f'ratio.png  {os.path.join(args.out, "ratio.png")}')
    print(f'flip.png   {os.path.join(args.out, "flip.png")}')

    # The ratio over patches of pixels, where the reference's own noise cancels.
    patches = Metrics.patch_ratios(magic_light, reference_light, lit)
    still_light = [Metrics.luminance(frame) for frame in frames]
    rows = [
        {'name': 'patch ratio median', 'value': float(np.median(patches)), 'limit': 'patchRatioMedian'},
        {'name': 'patch ratio 10th', 'value': float(np.percentile(patches, 10)), 'limit': 'patchRatioLow'},
        {'name': 'patch ratio 90th', 'value': float(np.percentile(patches, 90)), 'limit': 'patchRatioHigh'},
        {'name': 'fireflies in a million', 'limit': 'firefliesPerMillion',
         'value': Metrics.fireflies(still_light, reference_light)},
        {'name': 'banding', 'limit': 'bandingPart',
         'value': Metrics.banding(Metrics.display_levels(magic_light, exposure),
                                  Metrics.display_levels(reference_light, exposure), lit)},
        {'name': 'mean FLIP', 'value': mean_flip, 'limit': 'meanFlip'},
        {'name': 'flicker', 'value': Metrics.flicker(still_light, lit), 'limit': 'flickerPart'},
    ]

    # Each point: the colour of Magic's patch against the reference's, which a limit judges, and the light that the
    # three-angle rows read.
    point_lights = []
    print(f'{"point":<24} {"x":>5} {"y":>5} {"magic/ref":>9}  {"r/g":>11}  {"b/g":>11}')
    for point in view['points']:
        x, y = Camera.project(view, pose, point, width, height)
        m = Camera.patch(magic, x, y, width, height)
        r = Camera.patch(reference, x, y, width, height)
        point_lights.append(float(Metrics.luminance(m)))
        ratio = point_lights[-1] / max(float(Metrics.luminance(r)), 1e-9)
        colour = max(abs(m[0] / m[1] / (r[0] / r[1]) - 1), abs(m[2] / m[1] / (r[2] / r[1]) - 1))
        rows.append({'name': f'colour of {point["name"]}', 'value': colour, 'limit': 'pointColour'})
        print(f'{point["name"]:<24} {x:5.2f} {y:5.2f} {ratio:9.2f}  '
              f'{m[0] / m[1]:5.2f}/{r[0] / r[1]:5.2f}  {m[2] / m[1]:5.2f}/{r[2] / r[1]:5.2f}')
    print(f'{"median of the picture":<24} {"":>5} {"":>5} '
          f'{float(np.median(magic_light[lit] / reference_light[lit])):9.2f}'
          f'  (over {floor:.4g} nits, {lit.mean():.0%} of the pixels)')

    if args.angles:
        with open(args.angles, encoding='utf-8') as f:
            rows += angle_rows(view, json.load(f), point_lights, width, height)
    rows += leak_rows(view, pose, magic, width, height)
    return {'rows': rows, 'pictures': pictures}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--view')
    parser.add_argument('--reference', help='The Cycles EXR.')
    parser.add_argument('--magic', nargs='+',
                        help="Magic's still frames of the view. The comparison takes their mean; the flicker and the "
                             'fireflies read them one by one.')
    parser.add_argument('--angles',
                        help='A JSON list of {"label", "pose", "frames"}: the camera angles beside the view, each '
                             "with Magic's frames of it. The three-angle row of each point reads them. A view whose "
                             'points all state a maxNits of their own is measured at its own pose alone, so the '
                             'capture gives it no angle.')
    parser.add_argument('--out', help='The folder the pictures are written to.')
    parser.add_argument('--json', help='Writes the rows and the pictures that a test runner reads to this path.')
    parser.add_argument('--pose', nargs=7, type=float, metavar=('X', 'Y', 'Z', 'QX', 'QY', 'QZ', 'QW'),
                        help="Where the camera stands and how it is turned, from the view's ReferenceView entity.")
    parser.add_argument('--mask', help='A Cycles mask of the same view, which is black where the sky shows.')
    parser.add_argument('--check-points', metavar='JOBS',
                        help='A JSON list of [{view, poses}], where each pose is {"label", "pose"}: the places the '
                             'capture measures the view at. Prints where each point falls at each of them, and ends '
                             'with a fault when one is outside the picture or nearer its edge than the margin that '
                             'VisualTests.json states. Renders nothing and writes nothing.')
    parser.add_argument('--pick', nargs=2, type=int, metavar=('X', 'Y'),
                        help="Prints the world position of this pixel, counted from the picture's top left corner.")
    parser.add_argument('--depth', help='A screenshot of the Depth debug view, which --pick reads.')
    parser.add_argument('--far', type=float, default=1000,
                        help='DeferredRendering.DepthViewFar of the run that took the Depth screenshot.')
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])

    if args.check_points:
        width, height = Settings.picture_size()
        with open(args.check_points, encoding='utf-8') as f:
            jobs = json.load(f)
        Camera.check_points(jobs, width, height, Settings.TESTS['pointMargin'])
        return
    if not (args.view and args.pose):
        raise SystemExit('Give --view and --pose.')
    view = Settings.view(args.view)

    if args.pick:
        if not args.depth:
            raise SystemExit('--pick needs --depth, a screenshot of the Depth debug view of the same view.')
        pick(view, args.pose, args)
        return
    if not (args.reference and args.magic and args.out and args.json):
        raise SystemExit('To compare, give --reference, --magic, --out and --json, and --angles for the three-angle '
                         'rows. To read a pixel, give --pick and --depth.')
    measured = {'view': view['name'], **compare(view, args.pose, args)}
    print_rows(measured['rows'])
    with open(args.json, 'w', encoding='utf-8') as f:
        json.dump(measured, f, indent=2)
    print(f'metrics:   {args.json}')


if __name__ == '__main__':
    main()
