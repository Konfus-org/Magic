# Measures how the light settles and how noisy it is, from the HDR frames of one run of Magic, in Magic's light before
# the exposure. tools/Scripts/VisualTests.ps1 takes the frames and writes the list of the moves. The frame pictures
# take the AgX curve, which is Blender's own, so run it with Blender from the repository root:
#   blender.exe -b --python tools/Reference/MeasureDenoise.py -- --moves moves.json --out <folder> --json rows.json
# The moves file is a list, in the order the run took them, of
#   {"label", "revealed", "moving": [files], "held": [files], "still": [files]}
# label    names the move in the table, such as "turn6" or "strafe"
# revealed is the edge that the move brings into view, "left" or "right", or absent for the whole picture
# moving   a few frames taken while the move still runs, for the eye only: no row reads them
# held     the frames held still at the view's own pose, from the first frame after the move ends
# still    the frames taken again 8 s after that move: the move's own still light at the same pose
# The camera is at the view's exact pose for every held and still frame, so the two hold the same picture, and their
# difference is the renderer's, not the camera's. The light change is a move of its own: the held frames are the ones
# after the sun moves to another hour, and the still frames the ones after the light has settled.
# For each move it writes the frames until 90 % of 16 pixel patches stay within 5 % of the still light (or 3 times
# their spread over the still frames). A move that moves the camera also gets its noise: the noise of what the move
# brought into view, in the held frames, over the noise of the same area when it is settled. A turn brings in one
# edge of the picture; a strafe uncovers what nearer things hid all over it, so its area is the whole picture.
# It writes the moving and the held frames as PNGs into the out folder, at one exposure and the same look as the
# comparison's pictures, which the report page shows, and leaves the HDR frames where they are.
import argparse
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import Metrics
import Photograph
import Settings


def patches(lum, size=16):
    h, w = lum.shape[0] // size * size, lum.shape[1] // size * size
    return lum[:h, :w].reshape(h // size, size, w // size, size).mean(axis=(1, 3))


def settle(frames, still, spread):
    """The frames until 90 % of the lit patches stay within 5 % of the still light, or within 3 times the patch's own
    spread over the still frames where it is noisier than that."""
    ref = patches(still)
    lit = ref > np.percentile(ref, 10)
    limit = np.maximum(0.05, 3 * spread)[lit]
    share = [(np.abs(patches(f)[lit] / ref[lit] - 1) < limit).mean() for f in frames]
    for i in range(len(frames)):
        if all(s >= 0.9 for s in share[i:]):
            return i
    return None


def spread(frames):
    return np.std([patches(f) for f in frames], axis=0) / patches(np.mean(frames, axis=0))


def strip(image, side, part=0.1):
    """The part of the picture that a move brings into view: one edge for a turn, the whole picture for a strafe,
    which uncovers what nearer things hid all over it."""
    if side is None:
        return image
    cut = int(image.shape[1] * part)
    return image[:, :cut] if side == 'left' else image[:, -cut:]


def strip_error(frame, still, side):
    """The mean of a strip's lit 16 pixel patches' |light - still| over the still's. The dark patches are left out, as
    settle leaves them out, because a patch that is black in the still picture divides by zero."""
    f, s = patches(strip(frame, side)), patches(strip(still, side))
    lit = s > max(np.percentile(s, 10), 1e-6)
    if not lit.any():
        return 0.0
    return float(np.mean(np.abs(f[lit] - s[lit]) / s[lit]))


def move_noise(held, stills, side, floor, count):
    """How much noisier what came into view is, over the first held frames, than it is when it is settled.

    Both halves measure the same strip of the same picture against the same settled light, over the same number of
    frames, so the number is a ratio of noise and not of content. The settled noise is held at a floor, because the
    frames of a picture that has converged differ by less than the measure can tell apart, and a ratio over such a
    number means nothing."""
    settled = np.mean(stills, axis=0)
    after = float(np.mean([strip_error(f, settled, side) for f in held[:count]]))
    still = float(np.mean([strip_error(f, settled, side) for f in stills]))
    return after, still, after / max(still, floor)


def read_light(paths):
    """The light of each frame of a list, read one time."""
    return [Metrics.luminance(Metrics.read_pfm(path)) for path in paths]


def write_frames(moves, out):
    """Writes the moving and the held frames of every move as PNGs, which the report page links, at one exposure and
    the same look as the comparison's own pictures: the scene's grade and the AgX curve. The still frames hold the
    same picture as the held ones, so no row and no page reads a picture of them. The exposure comes from the first
    frame and holds for all of them, so the frames can be compared."""
    pictures, exposure = [], None
    for move in moves:
        for kind in ('moving', 'held'):
            for path in move.get(kind, []):
                rgb = Metrics.read_pfm(path)
                if exposure is None:
                    exposure = Photograph.exposure_for(Metrics.luminance(rgb))
                name = f'{len(pictures):03d}-{move["label"]}-{kind}.png'
                Photograph.write_photograph(os.path.join(out, name), Photograph.grade(rgb, exposure))
                pictures.append({'file': name, 'caption': f'{move["label"]}, {kind}'})
    return pictures


def measure(moves, out, floor, count):
    """The rows and the pictures of every move of one run."""
    rows = []
    pictures = write_frames(moves, out)
    for move in moves:
        label = move['label']
        held, stills = read_light(move['held']), read_light(move['still'])
        settled = settle(held, np.mean(stills, axis=0), spread(stills))
        rows.append({'name': f'{label} settle frames', 'limit': 'settleFrames',
                     'value': settled if settled is not None else len(held) + 1})
        # The noise of a move is the noise of what the move brought into view, so a change that moves no camera,
        # such as the sun moving to another hour, has a settle row alone.
        if not move.get('moving'):
            continue
        side = move.get('revealed')
        after, still, ratio = move_noise(held, stills, side, floor, count)
        rows.append({'name': f'{label} {"whole picture" if side is None else "edge"} noise', 'value': ratio,
                     'limit': 'edgeNoise', 'note': f'{after:.4f} held against {still:.4f} settled'})
    return {'rows': rows, 'pictures': pictures}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--moves', required=True,
                        help='A JSON list of {"label", "revealed", "moving", "held", "still"} in the order the run '
                             'took them.')
    parser.add_argument('--out', required=True, help='The folder the frame pictures are written to.')
    parser.add_argument('--json', help='Writes the rows and the pictures that a test runner reads to this path.')
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    os.makedirs(args.out, exist_ok=True)
    edge = Settings.TESTS['edgeNoise']
    with open(args.moves, encoding='utf-8') as f:
        moves = json.load(f)
    measured = measure(moves, args.out, edge['stillFloor'], edge['heldFrames'])
    print(f'{"row":<34} {"value":>11}  {"limit":<14} {"note"}')
    for row in measured['rows']:
        print(f'{row["name"]:<34} {row["value"]:11.4g}  {row["limit"]:<14} {row.get("note", "")}')
    if args.json:
        with open(args.json, 'w', encoding='utf-8') as f:
            json.dump(measured, f, indent=2)


if __name__ == '__main__':
    main()
